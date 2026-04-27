//
// Copyright (c) 2026 Microsoft
// Licensed under the MIT license.
//
// AST2600 eSPI Controller — Renode model
// Ported from QEMU hw/misc/aspeed_espi.c
//
// Implements all 4 eSPI channels (Peripheral, Virtual Wire, OOB, Flash),
// FIFO-based TX/RX, DMA address registers, W1C interrupt status,
// capability registers, MMBI, and VW system event handling.
//

using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord |
                         AllowedTranslation.WordToDoubleWord)]
    public class Aspeed_eSPI : IDoubleWordPeripheral, IKnownSize, IGPIOSender
    {
        public Aspeed_eSPI(IMachine machine)
        {
            this.machine = machine;
            IRQ = new GPIO();

            pcRxBuf = new byte[FifoSize];
            pcTxBuf = new byte[FifoSize];
            npTxBuf = new byte[FifoSize];
            oobRxBuf = new byte[FifoSize];
            oobTxBuf = new byte[FifoSize];
            flashRxBuf = new byte[FifoSize];
            flashTxBuf = new byte[FifoSize];

            registers = CreateRegisters();
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            // FIFO read registers need special handling
            switch((uint)offset)
            {
                case 0x018: // PERIF_PC_RX_DATA
                    if((ctrlValue & CtrlPerifPcRxDmaEn) != 0) return 0;
                    if(pcRxPos < pcRxLen) return pcRxBuf[pcRxPos++];
                    return 0;

                case 0x048: // OOB_RX_DATA
                    if((ctrlValue & CtrlOobRxDmaEn) != 0) return 0;
                    if(oobRxPos < oobRxLen) return oobRxBuf[oobRxPos++];
                    return 0;

                case 0x068: // FLASH_RX_DATA
                    if((ctrlValue & CtrlFlashRxDmaEn) != 0) return 0;
                    if(flashRxPos < flashRxLen) return flashRxBuf[flashRxPos++];
                    return 0;
            }

            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            // FIFO write registers need special handling
            switch((uint)offset)
            {
                case 0x028: // PERIF_PC_TX_DATA
                    if((ctrlValue & CtrlPerifPcTxDmaEn) == 0 && pcTxLen < FifoSize)
                        pcTxBuf[pcTxLen++] = (byte)value;
                    return;

                case 0x038: // PERIF_NP_TX_DATA
                    if((ctrlValue & CtrlPerifNpTxDmaEn) == 0 && npTxLen < FifoSize)
                        npTxBuf[npTxLen++] = (byte)value;
                    return;

                case 0x058: // OOB_TX_DATA
                    if((ctrlValue & CtrlOobTxDmaEn) == 0 && oobTxLen < FifoSize)
                        oobTxBuf[oobTxLen++] = (byte)value;
                    return;

                case 0x078: // FLASH_TX_DATA
                    if((ctrlValue & CtrlFlashTxDmaEn) == 0 && flashTxLen < FifoSize)
                        flashTxBuf[flashTxLen++] = (byte)value;
                    return;
            }

            registers.Write(offset, value);
        }

        public void Reset()
        {
            registers.Reset();

            // Set capability reset values (read-only, set after register reset)
            genCapValue = GenCapReset;
            ch0CapValue = Ch0CapReset;
            ch1CapValue = Ch1CapReset;
            ch2CapValue = Ch2CapReset;
            ch3CapValue = Ch3CapReset;
            ch3Cap2Value = Ch3Cap2Reset;

            // CTRL2 default: MCYC read/write disabled
            ctrl2Value = Ctrl2McycRdDis | Ctrl2McycWrDis;

            // SYSEVT: PLTRST# deasserted by default (host powered on)
            sysevtValue = SysevtPltrst;

            // INT_STS: RST_DEASSERT set (eSPI link up)
            intStsValue = IntRstDeassert;

            // Reset FIFO state
            ResetPerifPcRx();
            ResetPerifPcTx();
            ResetPerifNpTx();
            ResetOobRx();
            ResetOobTx();
            ResetFlashRx();
            ResetFlashTx();

            sysevt1Value = 0;
            sysevtIntEnValue = 0;
            sysevtIntStsValue = 0;
            sysevt1IntEnValue = 0;
            sysevt1IntStsValue = 0;
            // Initialize power signals to match S0_Working initial state
            cpuPowerGood = true;
            psPowerOk = true;
            catErr = false;
            hostErrorBits = 0;
            currentAcpiState = AcpiState.S0_Working;
            lastResetSource = 0;
            intEnValue = 0;
            ctrlValue = 0;
            mmbiCtrlValue = 0;
            mmbiIntStsValue = 0;
            mmbiIntEnValue = 0;

            UpdateIrq();
        }

        public long Size => 0x1000;
        public GPIO IRQ { get; }

        // --- Public injection methods for co-simulation ---

        /// <summary>
        /// Inject a peripheral channel RX packet (host → BMC).
        /// </summary>
        public void InjectPerifPcRx(byte cycleType, byte tag, byte[] data)
        {
            uint len = (uint)(data?.Length ?? 0);
            if(len > FifoSize)
            {
                this.Log(LogLevel.Warning, "PC RX packet too large ({0} > {1})", len, FifoSize);
                len = FifoSize;
            }

            if(data != null)
            {
                Array.Copy(data, 0, pcRxBuf, 0, (int)len);
            }
            pcRxLen = len;
            pcRxPos = 0;

            pcRxCtrlValue = ServPend | PackCtrl(cycleType, tag, len);

            intStsValue |= IntPerifPcRxCmplt;
            UpdateIrq();
        }

        /// <summary>
        /// Inject an OOB channel RX packet (host → BMC).
        /// </summary>
        public void InjectOobRx(byte cycleType, byte tag, byte[] data)
        {
            uint len = (uint)(data?.Length ?? 0);
            if(len > FifoSize)
            {
                this.Log(LogLevel.Warning, "OOB RX packet too large ({0} > {1})", len, FifoSize);
                len = FifoSize;
            }

            if(data != null)
            {
                Array.Copy(data, 0, oobRxBuf, 0, (int)len);
            }
            oobRxLen = len;
            oobRxPos = 0;

            oobRxCtrlValue = ServPend | PackCtrl(cycleType, tag, len);

            intStsValue |= IntOobRxCmplt;
            UpdateIrq();
        }



        /// <summary>
        /// Connect an OOB MCTP handler. When BMC sends OOB TX, the handler
        /// receives the raw packet bytes.
        /// </summary>
        public void ConnectOobHandler(Action<byte[]> handler)
        {
            oobMctpHandler = handler;
            this.Log(LogLevel.Info, "eSPI: connected OOB MCTP handler");
        }

        /// <summary>
        /// Inject an MCTP packet into OOB RX (external device → BMC).
        /// </summary>
        public void InjectOobMctp(byte[] mctpPacket)
        {
            InjectOobRx(0x21, 0x00, mctpPacket);
            this.Log(LogLevel.Debug, "eSPI: MCTP OOB inject {0} bytes", mctpPacket.Length);
        }

        /// <summary>
        /// Inject a Flash channel RX packet (host → BMC).
        /// </summary>
        public void InjectFlashRx(byte cycleType, byte tag, byte[] data)
        {
            uint len = (uint)(data?.Length ?? 0);
            if(len > FifoSize)
            {
                this.Log(LogLevel.Warning, "Flash RX packet too large ({0} > {1})", len, FifoSize);
                len = FifoSize;
            }

            if(data != null)
            {
                Array.Copy(data, 0, flashRxBuf, 0, (int)len);
            }
            flashRxLen = len;
            flashRxPos = 0;

            flashRxCtrlValue = ServPend | PackCtrl(cycleType, tag, len);

            intStsValue |= IntFlashRxCmplt;
            UpdateIrq();
        }

        /// <summary>
        /// Inject host-driven Virtual Wire system events.
        /// Only modifies host-driven bits; preserves slave-driven bits.
        /// </summary>
        public void InjectVwSysevt(uint hostEvents)
        {
            uint oldVal = sysevtValue;
            uint newVal = (hostEvents & SysevtHostDrivenMask) |
                          (oldVal & ~SysevtHostDrivenMask);
            sysevtValue = newVal;

            NotifySysevtChange(oldVal, newVal);
        }

        // --- Private implementation ---

        private DoubleWordRegisterCollection CreateRegisters()
        {
            var regs = new Dictionary<long, DoubleWordRegister>();

            // 0x000 CTRL
            regs[0x000] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "CTRL",
                    writeCallback: (_, val) => HandleCtrlWrite((uint)val),
                    valueProviderCallback: _ => ctrlValue);

            // 0x004 STS (read-only)
            regs[0x004] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, FieldMode.Read, name: "STS",
                    valueProviderCallback: _ => stsValue);

            // 0x008 INT_STS (W1C)
            regs[0x008] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "INT_STS",
                    writeCallback: (_, val) =>
                    {
                        intStsValue &= ~(uint)val;
                        UpdateIrq();
                    },
                    valueProviderCallback: _ => intStsValue);

            // 0x00C INT_EN
            regs[0x00C] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "INT_EN",
                    writeCallback: (_, val) =>
                    {
                        intEnValue = (uint)val;
                        UpdateIrq();
                    },
                    valueProviderCallback: _ => intEnValue);

            // 0x010 PERIF_PC_RX_DMA
            regs[0x010] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "PERIF_PC_RX_DMA",
                    writeCallback: (_, val) => pcRxDmaAddr = (uint)val,
                    valueProviderCallback: _ => pcRxDmaAddr);

            // 0x014 PERIF_PC_RX_CTRL (SERV_PEND is W1C)
            regs[0x014] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "PERIF_PC_RX_CTRL",
                    writeCallback: (_, val) =>
                    {
                        if(((uint)val & ServPend) != 0)
                        {
                            pcRxCtrlValue &= ~ServPend;
                            pcRxPos = 0;
                            pcRxLen = 0;
                        }
                    },
                    valueProviderCallback: _ => pcRxCtrlValue);

            // 0x018 PERIF_PC_RX_DATA — handled in ReadDoubleWord
            regs[0x018] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, FieldMode.Read, name: "PERIF_PC_RX_DATA");

            // 0x020 PERIF_PC_TX_DMA
            regs[0x020] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "PERIF_PC_TX_DMA",
                    writeCallback: (_, val) => pcTxDmaAddr = (uint)val,
                    valueProviderCallback: _ => pcTxDmaAddr);

            // 0x024 PERIF_PC_TX_CTRL
            regs[0x024] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "PERIF_PC_TX_CTRL",
                    writeCallback: (_, val) =>
                    {
                        pcTxCtrlValue = (uint)val;
                        if(((uint)val & TrigPend) != 0)
                            CompletePcTx();
                    },
                    valueProviderCallback: _ => pcTxCtrlValue);

            // 0x028 PERIF_PC_TX_DATA — handled in WriteDoubleWord
            regs[0x028] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, FieldMode.Write, name: "PERIF_PC_TX_DATA");

            // 0x030 PERIF_NP_TX_DMA
            regs[0x030] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "PERIF_NP_TX_DMA",
                    writeCallback: (_, val) => npTxDmaAddr = (uint)val,
                    valueProviderCallback: _ => npTxDmaAddr);

            // 0x034 PERIF_NP_TX_CTRL
            regs[0x034] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "PERIF_NP_TX_CTRL",
                    writeCallback: (_, val) =>
                    {
                        npTxCtrlValue = (uint)val;
                        if(((uint)val & TrigPend) != 0)
                            CompleteNpTx();
                    },
                    valueProviderCallback: _ => npTxCtrlValue);

            // 0x038 PERIF_NP_TX_DATA — handled in WriteDoubleWord
            regs[0x038] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, FieldMode.Write, name: "PERIF_NP_TX_DATA");

            // 0x040 OOB_RX_DMA
            regs[0x040] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "OOB_RX_DMA",
                    writeCallback: (_, val) => oobRxDmaAddr = (uint)val,
                    valueProviderCallback: _ => oobRxDmaAddr);

            // 0x044 OOB_RX_CTRL
            regs[0x044] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "OOB_RX_CTRL",
                    writeCallback: (_, val) =>
                    {
                        if(((uint)val & ServPend) != 0)
                        {
                            oobRxCtrlValue &= ~ServPend;
                            oobRxPos = 0;
                            oobRxLen = 0;
                        }
                    },
                    valueProviderCallback: _ => oobRxCtrlValue);

            // 0x048 OOB_RX_DATA — handled in ReadDoubleWord
            regs[0x048] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, FieldMode.Read, name: "OOB_RX_DATA");

            // 0x050 OOB_TX_DMA
            regs[0x050] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "OOB_TX_DMA",
                    writeCallback: (_, val) => oobTxDmaAddr = (uint)val,
                    valueProviderCallback: _ => oobTxDmaAddr);

            // 0x054 OOB_TX_CTRL
            regs[0x054] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "OOB_TX_CTRL",
                    writeCallback: (_, val) =>
                    {
                        oobTxCtrlValue = (uint)val;
                        if(((uint)val & TrigPend) != 0)
                            CompleteOobTx();
                    },
                    valueProviderCallback: _ => oobTxCtrlValue);

            // 0x058 OOB_TX_DATA — handled in WriteDoubleWord
            regs[0x058] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, FieldMode.Write, name: "OOB_TX_DATA");

            // 0x060 FLASH_RX_DMA
            regs[0x060] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "FLASH_RX_DMA",
                    writeCallback: (_, val) => flashRxDmaAddr = (uint)val,
                    valueProviderCallback: _ => flashRxDmaAddr);

            // 0x064 FLASH_RX_CTRL
            regs[0x064] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "FLASH_RX_CTRL",
                    writeCallback: (_, val) =>
                    {
                        if(((uint)val & ServPend) != 0)
                        {
                            flashRxCtrlValue &= ~ServPend;
                            flashRxPos = 0;
                            flashRxLen = 0;
                        }
                    },
                    valueProviderCallback: _ => flashRxCtrlValue);

            // 0x068 FLASH_RX_DATA — handled in ReadDoubleWord
            regs[0x068] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, FieldMode.Read, name: "FLASH_RX_DATA");

            // 0x070 FLASH_TX_DMA
            regs[0x070] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "FLASH_TX_DMA",
                    writeCallback: (_, val) => flashTxDmaAddr = (uint)val,
                    valueProviderCallback: _ => flashTxDmaAddr);

            // 0x074 FLASH_TX_CTRL
            regs[0x074] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "FLASH_TX_CTRL",
                    writeCallback: (_, val) =>
                    {
                        flashTxCtrlValue = (uint)val;
                        if(((uint)val & TrigPend) != 0)
                            CompleteFlashTx();
                    },
                    valueProviderCallback: _ => flashTxCtrlValue);

            // 0x078 FLASH_TX_DATA — handled in WriteDoubleWord
            regs[0x078] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, FieldMode.Write, name: "FLASH_TX_DATA");

            // 0x080 CTRL2
            regs[0x080] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "CTRL2",
                    writeCallback: (_, val) => ctrl2Value = (uint)val,
                    valueProviderCallback: _ => ctrl2Value);

            // 0x084 PERIF_MCYC_SADDR
            regs[0x084] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "PERIF_MCYC_SADDR",
                    writeCallback: (_, val) => mcycSaddrValue = (uint)val,
                    valueProviderCallback: _ => mcycSaddrValue);

            // 0x088 PERIF_MCYC_TADDR
            regs[0x088] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "PERIF_MCYC_TADDR",
                    writeCallback: (_, val) => mcycTaddrValue = (uint)val,
                    valueProviderCallback: _ => mcycTaddrValue);

            // 0x08C PERIF_MCYC_MASK
            regs[0x08C] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "PERIF_MCYC_MASK",
                    writeCallback: (_, val) => mcycMaskValue = (uint)val,
                    valueProviderCallback: _ => mcycMaskValue);

            // 0x090 FLASH_SAFS_TADDR
            regs[0x090] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "FLASH_SAFS_TADDR",
                    writeCallback: (_, val) => flashSafsTaddrValue = (uint)val,
                    valueProviderCallback: _ => flashSafsTaddrValue);

            // 0x094 VW_SYSEVT_INT_EN
            regs[0x094] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "VW_SYSEVT_INT_EN",
                    writeCallback: (_, val) =>
                    {
                        sysevtIntEnValue = (uint)val;
                        // Re-evaluate: newly enabled bits may trigger
                        NotifySysevtChange(0, sysevtValue);
                    },
                    valueProviderCallback: _ => sysevtIntEnValue);

            // 0x098 VW_SYSEVT
            regs[0x098] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "VW_SYSEVT",
                    writeCallback: (_, val) =>
                    {
                        // BMC can write slave-driven bits only
                        sysevtValue = ((uint)val & SysevtSlaveDrivenMask) |
                                      (sysevtValue & SysevtHostDrivenMask);
                    },
                    valueProviderCallback: _ => sysevtValue);

            // 0x09C VW_GPIO_VAL
            regs[0x09C] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "VW_GPIO_VAL",
                    writeCallback: (_, val) =>
                    {
                        uint oldVal = vwGpioValue;
                        vwGpioValue = (uint)val;
                        if(oldVal != val)
                        {
                            intStsValue |= IntVwGpio;
                            UpdateIrq();
                        }
                    },
                    valueProviderCallback: _ => vwGpioValue);

            // 0x0A0 GEN_CAP_N_CONF (read-only)
            regs[0x0A0] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, FieldMode.Read, name: "GEN_CAP_N_CONF",
                    valueProviderCallback: _ => genCapValue);

            // 0x0A4 CH0_CAP_N_CONF (read-only)
            regs[0x0A4] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, FieldMode.Read, name: "CH0_CAP_N_CONF",
                    valueProviderCallback: _ => ch0CapValue);

            // 0x0A8 CH1_CAP_N_CONF (read-only)
            regs[0x0A8] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, FieldMode.Read, name: "CH1_CAP_N_CONF",
                    valueProviderCallback: _ => ch1CapValue);

            // 0x0AC CH2_CAP_N_CONF (read-only)
            regs[0x0AC] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, FieldMode.Read, name: "CH2_CAP_N_CONF",
                    valueProviderCallback: _ => ch2CapValue);

            // 0x0B0 CH3_CAP_N_CONF (read-only)
            regs[0x0B0] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, FieldMode.Read, name: "CH3_CAP_N_CONF",
                    valueProviderCallback: _ => ch3CapValue);

            // 0x0B4 CH3_CAP_N_CONF2 (read-only)
            regs[0x0B4] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, FieldMode.Read, name: "CH3_CAP_N_CONF2",
                    valueProviderCallback: _ => ch3Cap2Value);

            // 0x0C0 VW_GPIO_DIR
            regs[0x0C0] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "VW_GPIO_DIR",
                    writeCallback: (_, val) => vwGpioDirValue = (uint)val,
                    valueProviderCallback: _ => vwGpioDirValue);

            // 0x0C4 VW_GPIO_GRP
            regs[0x0C4] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "VW_GPIO_GRP",
                    writeCallback: (_, val) => vwGpioGrpValue = (uint)val,
                    valueProviderCallback: _ => vwGpioGrpValue);

            // 0x0FC INT_EN_CLR (write clears INT_EN bits)
            regs[0x0FC] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "INT_EN_CLR",
                    writeCallback: (_, val) =>
                    {
                        intEnValue &= ~(uint)val;
                        UpdateIrq();
                    },
                    valueProviderCallback: _ => 0);

            // 0x100 VW_SYSEVT1_INT_EN
            regs[0x100] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "VW_SYSEVT1_INT_EN",
                    writeCallback: (_, val) => sysevt1IntEnValue = (uint)val,
                    valueProviderCallback: _ => sysevt1IntEnValue);

            // 0x104 VW_SYSEVT1
            regs[0x104] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "VW_SYSEVT1",
                    writeCallback: (_, val) =>
                    {
                        // SUSPEND_ACK is slave-driven, SUSPEND_WARN is host-driven
                        sysevt1Value = ((uint)val & Sysevt1SuspendAck) |
                                       (sysevt1Value & Sysevt1SuspendWarn);
                    },
                    valueProviderCallback: _ => sysevt1Value);

            // 0x110 VW_SYSEVT_INT_T0
            regs[0x110] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "VW_SYSEVT_INT_T0",
                    writeCallback: (_, val) => sysevtIntT0Value = (uint)val,
                    valueProviderCallback: _ => sysevtIntT0Value);

            // 0x114 VW_SYSEVT_INT_T1
            regs[0x114] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "VW_SYSEVT_INT_T1",
                    writeCallback: (_, val) => sysevtIntT1Value = (uint)val,
                    valueProviderCallback: _ => sysevtIntT1Value);

            // 0x118 VW_SYSEVT_INT_T2
            regs[0x118] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "VW_SYSEVT_INT_T2",
                    writeCallback: (_, val) => sysevtIntT2Value = (uint)val,
                    valueProviderCallback: _ => sysevtIntT2Value);

            // 0x11C VW_SYSEVT_INT_STS (W1C)
            regs[0x11C] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "VW_SYSEVT_INT_STS",
                    writeCallback: (_, val) =>
                    {
                        sysevtIntStsValue &= ~(uint)val;
                        if(sysevtIntStsValue == 0)
                            intStsValue &= ~IntVwSysevt;
                        UpdateIrq();
                    },
                    valueProviderCallback: _ => sysevtIntStsValue);

            // 0x120 VW_SYSEVT1_INT_T0
            regs[0x120] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "VW_SYSEVT1_INT_T0",
                    writeCallback: (_, val) => sysevt1IntT0Value = (uint)val,
                    valueProviderCallback: _ => sysevt1IntT0Value);

            // 0x124 VW_SYSEVT1_INT_T1
            regs[0x124] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "VW_SYSEVT1_INT_T1",
                    writeCallback: (_, val) => sysevt1IntT1Value = (uint)val,
                    valueProviderCallback: _ => sysevt1IntT1Value);

            // 0x128 VW_SYSEVT1_INT_T2
            regs[0x128] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "VW_SYSEVT1_INT_T2",
                    writeCallback: (_, val) => sysevt1IntT2Value = (uint)val,
                    valueProviderCallback: _ => sysevt1IntT2Value);

            // 0x12C VW_SYSEVT1_INT_STS (W1C)
            regs[0x12C] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "VW_SYSEVT1_INT_STS",
                    writeCallback: (_, val) =>
                    {
                        sysevt1IntStsValue &= ~(uint)val;
                        if(sysevt1IntStsValue == 0)
                            intStsValue &= ~IntVwSysevt1;
                        UpdateIrq();
                    },
                    valueProviderCallback: _ => sysevt1IntStsValue);

            // 0x800 MMBI_CTRL
            regs[0x800] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "MMBI_CTRL",
                    writeCallback: (_, val) => mmbiCtrlValue = (uint)val,
                    valueProviderCallback: _ => mmbiCtrlValue);

            // 0x808 MMBI_INT_STS (W1C)
            regs[0x808] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "MMBI_INT_STS",
                    writeCallback: (_, val) => mmbiIntStsValue &= ~(uint)val,
                    valueProviderCallback: _ => mmbiIntStsValue);

            // 0x80C MMBI_INT_EN
            regs[0x80C] = new DoubleWordRegister(this, 0x0)
                .WithValueField(0, 32, name: "MMBI_INT_EN",
                    writeCallback: (_, val) => mmbiIntEnValue = (uint)val,
                    valueProviderCallback: _ => mmbiIntEnValue);

            // 0x810-0x848 MMBI_HOST_RWP (8 instances, 8 bytes apart)
            for(int i = 0; i < MmbiMaxInst; i++)
            {
                long off = 0x810 + (i * 8);
                int idx = i;
                regs[off] = new DoubleWordRegister(this, 0x0)
                    .WithValueField(0, 32, name: $"MMBI_HOST_RWP{idx}",
                        writeCallback: (_, val) => mmbiHostRwp[idx] = (uint)val,
                        valueProviderCallback: _ => mmbiHostRwp[idx]);
            }

            return new DoubleWordRegisterCollection(this, regs);
        }

        private void HandleCtrlWrite(uint data)
        {
            // SW reset bits are self-clearing
            if((data & CtrlPerifPcRxSwRst) != 0) ResetPerifPcRx();
            if((data & CtrlPerifPcTxSwRst) != 0) ResetPerifPcTx();
            if((data & CtrlPerifNpTxSwRst) != 0) ResetPerifNpTx();
            if((data & CtrlOobRxSwRst) != 0) ResetOobRx();
            if((data & CtrlOobTxSwRst) != 0) ResetOobTx();
            if((data & CtrlFlashRxSwRst) != 0) ResetFlashRx();
            if((data & CtrlFlashTxSwRst) != 0) ResetFlashTx();

            // Store value with reset bits cleared
            ctrlValue = data & ~SwResetMask;
        }

        private void CompletePcTx()
        {
            pcTxCtrlValue &= ~TrigPend;
            pcTxLen = 0;
            intStsValue |= IntPerifPcTxCmplt;
            UpdateIrq();
        }

        private void CompleteNpTx()
        {
            npTxCtrlValue &= ~TrigPend;
            npTxLen = 0;
            intStsValue |= IntPerifNpTxCmplt;
            UpdateIrq();
        }

        private void CompleteOobTx()
        {
            if(oobMctpHandler != null && oobTxLen > 0)
            {
                var pkt = new byte[oobTxLen];
                Array.Copy(oobTxBuf, 0, pkt, 0, oobTxLen);
                this.Log(LogLevel.Debug, "eSPI OOB TX -> PLDM: {0} bytes", oobTxLen);
                oobMctpHandler(pkt);
            }
            oobTxCtrlValue &= ~TrigPend;
            oobTxLen = 0;
            intStsValue |= IntOobTxCmplt;
            UpdateIrq();
        }

        private void CompleteFlashTx()
        {
            flashTxCtrlValue &= ~TrigPend;
            flashTxLen = 0;
            intStsValue |= IntFlashTxCmplt;
            UpdateIrq();
        }

        private void NotifySysevtChange(uint oldVal, uint newVal)
        {
            uint changed = oldVal ^ newVal;
            uint enabled = sysevtIntEnValue;

            if((changed & enabled) != 0)
            {
                sysevtIntStsValue |= (changed & enabled);
                intStsValue |= IntVwSysevt;
                UpdateIrq();
            }
        }

        private void UpdateIrq()
        {
            bool active = (intStsValue & intEnValue) != 0;
            IRQ.Set(active);
        }

        private void ResetPerifPcRx()
        {
            Array.Clear(pcRxBuf, 0, pcRxBuf.Length);
            pcRxLen = 0;
            pcRxPos = 0;
            pcRxCtrlValue = 0;
        }

        private void ResetPerifPcTx()
        {
            Array.Clear(pcTxBuf, 0, pcTxBuf.Length);
            pcTxLen = 0;
            pcTxCtrlValue = 0;
        }

        private void ResetPerifNpTx()
        {
            Array.Clear(npTxBuf, 0, npTxBuf.Length);
            npTxLen = 0;
            npTxCtrlValue = 0;
        }

        private void ResetOobRx()
        {
            Array.Clear(oobRxBuf, 0, oobRxBuf.Length);
            oobRxLen = 0;
            oobRxPos = 0;
            oobRxCtrlValue = 0;
        }

        private void ResetOobTx()
        {
            Array.Clear(oobTxBuf, 0, oobTxBuf.Length);
            oobTxLen = 0;
            oobTxCtrlValue = 0;
        }

        private void ResetFlashRx()
        {
            Array.Clear(flashRxBuf, 0, flashRxBuf.Length);
            flashRxLen = 0;
            flashRxPos = 0;
            flashRxCtrlValue = 0;
        }

        private void ResetFlashTx()
        {
            Array.Clear(flashTxBuf, 0, flashTxBuf.Length);
            flashTxLen = 0;
            flashTxCtrlValue = 0;
        }

        private static uint PackCtrl(byte cyc, byte tag, uint len)
        {
            return ((len & 0xFFF) << 12) | (uint)((tag & 0xF) << 8) | cyc;
        }

        // --- Constants (matching QEMU defines) ---

        private const int FifoSize = 256;
        private const int MmbiMaxInst = 8;

        // CTRL register bits
        private const uint CtrlFlashTxSwRst     = 1u << 31;
        private const uint CtrlFlashRxSwRst     = 1u << 30;
        private const uint CtrlOobTxSwRst       = 1u << 29;
        private const uint CtrlOobRxSwRst       = 1u << 28;
        private const uint CtrlPerifNpTxSwRst   = 1u << 27;
        private const uint CtrlPerifNpRxSwRst   = 1u << 26;
        private const uint CtrlPerifPcTxSwRst   = 1u << 25;
        private const uint CtrlPerifPcRxSwRst   = 1u << 24;
        private const uint CtrlFlashTxDmaEn     = 1u << 23;
        private const uint CtrlFlashRxDmaEn     = 1u << 22;
        private const uint CtrlOobTxDmaEn       = 1u << 21;
        private const uint CtrlOobRxDmaEn       = 1u << 20;
        private const uint CtrlPerifNpTxDmaEn   = 1u << 19;
        private const uint CtrlPerifPcTxDmaEn   = 1u << 17;
        private const uint CtrlPerifPcRxDmaEn   = 1u << 16;

        private const uint SwResetMask =
            CtrlFlashTxSwRst | CtrlFlashRxSwRst |
            CtrlOobTxSwRst | CtrlOobRxSwRst |
            CtrlPerifNpTxSwRst | CtrlPerifNpRxSwRst |
            CtrlPerifPcTxSwRst | CtrlPerifPcRxSwRst;

        // INT_STS / INT_EN bits
        private const uint IntRstDeassert       = 1u << 31;
        private const uint IntOobRxTmout        = 1u << 23;
        private const uint IntVwSysevt1         = 1u << 22;
        private const uint IntFlashTxErr        = 1u << 21;
        private const uint IntOobTxErr          = 1u << 20;
        private const uint IntFlashTxAbt        = 1u << 19;
        private const uint IntOobTxAbt          = 1u << 18;
        private const uint IntPerifNpTxAbt      = 1u << 17;
        private const uint IntPerifPcTxAbt      = 1u << 16;
        private const uint IntFlashRxAbt        = 1u << 15;
        private const uint IntOobRxAbt          = 1u << 14;
        private const uint IntPerifNpRxAbt      = 1u << 13;
        private const uint IntPerifPcRxAbt      = 1u << 12;
        private const uint IntPerifNpTxErr      = 1u << 11;
        private const uint IntPerifPcTxErr      = 1u << 10;
        private const uint IntVwGpio            = 1u << 9;
        private const uint IntVwSysevt          = 1u << 8;
        private const uint IntFlashTxCmplt      = 1u << 7;
        private const uint IntFlashRxCmplt      = 1u << 6;
        private const uint IntOobTxCmplt        = 1u << 5;
        private const uint IntOobRxCmplt        = 1u << 4;
        private const uint IntPerifNpTxCmplt    = 1u << 3;
        private const uint IntPerifPcTxCmplt    = 1u << 1;
        private const uint IntPerifPcRxCmplt    = 1u << 0;

        // CTRL register field bits
        private const uint ServPend = 1u << 31;
        private const uint TrigPend = 1u << 31;

        // SYSEVT host-driven bits
        private const uint SysevtHostRstWarn = 1u << 8;
        private const uint SysevtOobRstWarn  = 1u << 6;
        private const uint SysevtPltrst      = 1u << 5;
        private const uint SysevtSuspend     = 1u << 4;
        private const uint SysevtS5Sleep     = 1u << 2;
        private const uint SysevtS4Sleep     = 1u << 1;
        private const uint SysevtS3Sleep     = 1u << 0;

        private const uint SysevtHostDrivenMask =
            SysevtHostRstWarn | SysevtOobRstWarn | SysevtPltrst |
            SysevtSuspend | SysevtS5Sleep | SysevtS4Sleep | SysevtS3Sleep;

        // SYSEVT slave-driven bits
        private const uint SysevtHostRstAck   = 1u << 27;
        private const uint SysevtRstCpuInit   = 1u << 26;
        private const uint SysevtSlvBootSts   = 1u << 23;
        private const uint SysevtNonFatalErr  = 1u << 22;
        private const uint SysevtFatalErr     = 1u << 21;
        private const uint SysevtSlvBootDone  = 1u << 20;
        private const uint SysevtOobRstAck    = 1u << 16;
        private const uint SysevtNmiOut       = 1u << 10;
        private const uint SysevtSmiOut       = 1u << 9;

        private const uint SysevtSlaveDrivenMask =
            SysevtHostRstAck | SysevtRstCpuInit | SysevtSlvBootSts |
            SysevtNonFatalErr | SysevtFatalErr | SysevtSlvBootDone |
            SysevtOobRstAck | SysevtNmiOut | SysevtSmiOut;

        // SYSEVT1 bits
        private const uint Sysevt1SuspendAck  = 1u << 20;
        private const uint Sysevt1SuspendWarn = 1u << 0;

        // CTRL2 bits
        private const uint Ctrl2McycRdDis = 1u << 6;
        private const uint Ctrl2McycWrDis = 1u << 4;

        // Capability reset values (from QEMU)
        private const uint GenCapReset   = 0x0000F759;
        private const uint Ch0CapReset   = 0x00000073;
        private const uint Ch1CapReset   = 0x00000033;
        private const uint Ch2CapReset   = 0x00000033;
        private const uint Ch3CapReset   = 0x00000003;
        private const uint Ch3Cap2Reset  = 0x00000000;

        // --- State ---

        private readonly IMachine machine;
        private readonly DoubleWordRegisterCollection registers;

        // FIFO buffers
        private readonly byte[] pcRxBuf, pcTxBuf, npTxBuf;
        private readonly byte[] oobRxBuf, oobTxBuf;
        private Action<byte[]> oobMctpHandler;
        private readonly byte[] flashRxBuf, flashTxBuf;

        // FIFO positions/lengths
        private uint pcRxLen, pcRxPos, pcTxLen, npTxLen;
        private uint oobRxLen, oobRxPos, oobTxLen;
        private uint flashRxLen, flashRxPos, flashTxLen;

        // Register backing fields
        private uint ctrlValue, stsValue, intStsValue, intEnValue;
        private uint ctrl2Value;
        private uint pcRxCtrlValue, pcTxCtrlValue, npTxCtrlValue;
        private uint oobRxCtrlValue, oobTxCtrlValue;
        private uint flashRxCtrlValue, flashTxCtrlValue;
        private uint pcRxDmaAddr, pcTxDmaAddr, npTxDmaAddr;
        private uint oobRxDmaAddr, oobTxDmaAddr;
        private uint flashRxDmaAddr, flashTxDmaAddr;
        private uint mcycSaddrValue, mcycTaddrValue, mcycMaskValue;
        private uint flashSafsTaddrValue;
        private uint sysevtValue, sysevtIntEnValue, sysevtIntStsValue;
        private uint sysevt1Value, sysevt1IntEnValue, sysevt1IntStsValue;
        private uint sysevtIntT0Value, sysevtIntT1Value, sysevtIntT2Value;
        private uint sysevt1IntT0Value, sysevt1IntT1Value, sysevt1IntT2Value;
        private uint vwGpioValue, vwGpioDirValue, vwGpioGrpValue;
        private uint genCapValue, ch0CapValue, ch1CapValue;
        private uint ch2CapValue, ch3CapValue, ch3Cap2Value;
        private uint mmbiCtrlValue, mmbiIntStsValue, mmbiIntEnValue;

        // =================================================================
        // eSPI SAF (Slave Attached Flash) Partition Routing
        // Reference: Birchstream Simics oracle
        // =================================================================

        /// <summary>
        /// Handle a SAF (Slave Attached Flash) read request from the host.
        /// Routes the request through the SAF partition map and delivers
        /// data via the normal Flash RX channel path.
        /// </summary>
        /// <param name="hostAddress">Host physical address to read from</param>
        /// <param name="length">Number of bytes to read (max 64 per eSPI spec)</param>
        /// <param name="tag">eSPI transaction tag (0-15)</param>
        public void HandleSafRead(uint hostAddress, uint length, byte tag = 0)
        {
            if(length == 0 || length > SafMaxBurstSize)
            {
                this.Log(LogLevel.Warning, "SAF: Invalid read length {0} (max {1})", length, SafMaxBurstSize);
                return;
            }

            var sysbus = machine.GetSystemBus(this);
            byte[] result = new byte[length];
            uint filled = 0;

            while(filled < length)
            {
                uint hostOff = hostAddress + filled;
                uint remaining = length - filled;
                uint bmcAddr;
                uint regionRemaining;

                if(hostOff < SafBiosRegionEnd)
                {
                    // BIOS region -> FMC flash window
                    bmcAddr = SafBmcFlashBase + hostOff;
                    regionRemaining = SafBiosRegionEnd - hostOff;
                }
                else if(hostOff < SafOsRegionEnd)
                {
                    // OS region -> BMC DRAM
                    bmcAddr = SafBmcDramBase + (hostOff - SafBiosRegionEnd);
                    regionRemaining = SafOsRegionEnd - hostOff;
                }
                else
                {
                    // Hole / unmapped -> 0xFF fill
                    uint fillLen = remaining;
                    for(uint i = 0; i < fillLen; i++)
                        result[filled + i] = 0xFF;
                    filled += fillLen;
                    continue;
                }

                uint chunkLen = Math.Min(remaining, regionRemaining);

                try
                {
                    var data = sysbus.ReadBytes(bmcAddr, (int)chunkLen);
                    Array.Copy(data, 0, result, (int)filled, (int)chunkLen);
                }
                catch(Exception e)
                {
                    this.Log(LogLevel.Error, "SAF: Read failed at BMC addr 0x{0:X8}: {1}", bmcAddr, e.Message);
                    // Fill with 0xFF on error
                    for(uint i = 0; i < chunkLen; i++)
                        result[filled + i] = 0xFF;
                }

                filled += chunkLen;
            }

            this.Log(LogLevel.Debug, "SAF: Read host=0x{0:X8} len={1} -> delivered via Flash RX", hostAddress, length);

            // Deliver through normal Flash RX path
            // Cycle type 0x00 = successful completion with data
            InjectFlashRx(0x00, tag, result);
        }

        // =================================================================
        // Reset/Power Coordination (Phase 1e)
        // Reference: Birchstream Simics reset orchestrator
        // =================================================================

        /// <summary>
        /// Simulate host platform reset (PLTRST# assertion).
        /// Sets PLTRST# bit in VW SYSEVT, triggering BMC-side handler.
        /// </summary>
        public void AssertPlatformReset()
        {
            uint oldVal = sysevtValue;
            sysevtValue &= ~SysevtPltrst;  // PLTRST# active low
            this.Log(LogLevel.Info, "eSPI: PLTRST# asserted (host reset)");
            NotifySysevtChange(oldVal, sysevtValue);
        }

        /// <summary>
        /// Deassert host platform reset (PLTRST# deasserted = host running).
        /// </summary>
        public void DeassertPlatformReset()
        {
            uint oldVal = sysevtValue;
            sysevtValue |= SysevtPltrst;  // PLTRST# deasserted = host running
            this.Log(LogLevel.Info, "eSPI: PLTRST# deasserted (host running)");
            NotifySysevtChange(oldVal, sysevtValue);
        }

        /// <summary>
        /// Simulate host entering sleep state.
        /// </summary>
        public void SetHostSleepState(uint sleepBits)
        {
            uint oldVal = sysevtValue;
            // Set sleep state bits (S3=bit0, S4=bit1, S5=bit2)
            sysevtValue = (sysevtValue & ~(SysevtS3Sleep | SysevtS4Sleep | SysevtS5Sleep)) |
                          (sleepBits & (SysevtS3Sleep | SysevtS4Sleep | SysevtS5Sleep));
            this.Log(LogLevel.Debug, "eSPI: Host sleep state = 0x{0:X}", sleepBits);
            NotifySysevtChange(oldVal, sysevtValue);
        }

        /// <summary>
        /// Simulate host reset warning (OOB_RST_WARN).
        /// Host asserts this before initiating a graceful reset.
        /// </summary>
        public void AssertHostResetWarning()
        {
            uint oldVal = sysevtValue;
            sysevtValue |= SysevtHostRstWarn;
            this.Log(LogLevel.Debug, "eSPI: HOST_RST_WARN asserted");
            NotifySysevtChange(oldVal, sysevtValue);
        }

        /// <summary>
        /// Perform a full cold reset sequence:
        /// 1. Assert PLTRST#
        /// 2. Clear host-driven SYSEVT bits
        /// 3. Reset Flash RX/TX channels
        /// 4. Deassert PLTRST# (host restarts)
        /// </summary>
        public void ColdReset()
        {
            this.Log(LogLevel.Info, "eSPI: Cold reset sequence starting");

            // Assert PLTRST#
            AssertPlatformReset();

            // Clear host-driven SYSEVT bits (sleep, warnings)
            uint oldVal = sysevtValue;
            sysevtValue &= SysevtSlaveDrivenMask;
            NotifySysevtChange(oldVal, sysevtValue);

            // Reset flash channel state
            ResetFlashRx();
            ResetFlashTx();

            // Deassert PLTRST# after reset
            DeassertPlatformReset();

            this.Log(LogLevel.Info, "eSPI: Cold reset sequence complete");
        }

        /// <summary>
        /// Perform a warm reset sequence:
        /// 1. Assert HOST_RST_WARN
        /// 2. Assert PLTRST#
        /// 3. Keep persistent state (flash contents)
        /// 4. Deassert PLTRST#
        /// </summary>
        public void WarmReset()
        {
            this.Log(LogLevel.Info, "eSPI: Warm reset sequence starting");

            AssertHostResetWarning();
            AssertPlatformReset();

            // Warm reset preserves flash/DRAM contents, only resets transient state
            // (Flash channel FIFOs are NOT reset on warm reset)

            DeassertPlatformReset();

            // Clear reset warning
            uint oldVal = sysevtValue;
            sysevtValue &= ~SysevtHostRstWarn;
            NotifySysevtChange(oldVal, sysevtValue);

            this.Log(LogLevel.Info, "eSPI: Warm reset sequence complete");
        }

        // ===== GPIO Power Signal Modeling =====
        // Birchstream power/error signals managed by eSPI controller.
        // BMC outputs: CPUPWRGD (GPIO V[4]), PSPWROK (GPIO G[4])
        // Host inputs to BMC: CATERR (GPIO H[5]), ERR0 (GPIO D[0]), ERR1 (GPIO D[1]), ERR2 (GPIO D[2])

        /// <summary>
        /// Assert CPUPWRGD — BMC indicates CPU power is good.
        /// Corresponds to GPIO group V, bit 4.
        /// </summary>
        public void AssertCpuPowerGood()
        {
            cpuPowerGood = true;
            this.Log(LogLevel.Info, "eSPI: CPUPWRGD asserted (CPU power good)");
        }

        /// <summary>
        /// Deassert CPUPWRGD — CPU power not good.
        /// </summary>
        public void DeassertCpuPowerGood()
        {
            cpuPowerGood = false;
            this.Log(LogLevel.Info, "eSPI: CPUPWRGD deasserted");
        }

        /// <summary>
        /// Assert PSPWROK — BMC indicates platform power supply OK.
        /// Corresponds to GPIO group G, bit 4.
        /// </summary>
        public void AssertPsPowerOk()
        {
            psPowerOk = true;
            this.Log(LogLevel.Info, "eSPI: PSPWROK asserted (power supply OK)");
        }

        /// <summary>
        /// Deassert PSPWROK — platform power supply not OK.
        /// </summary>
        public void DeassertPsPowerOk()
        {
            psPowerOk = false;
            this.Log(LogLevel.Info, "eSPI: PSPWROK deasserted");
        }

        /// <summary>
        /// Inject CATERR from host — catastrophic error signal.
        /// Corresponds to GPIO group H, bit 5.
        /// </summary>
        public void InjectCatErr()
        {
            catErr = true;
            this.Log(LogLevel.Warning, "eSPI: CATERR injected (catastrophic host error)");
        }

        /// <summary>
        /// Clear CATERR.
        /// </summary>
        public void ClearCatErr()
        {
            catErr = false;
            this.Log(LogLevel.Info, "eSPI: CATERR cleared");
        }

        /// <summary>
        /// Inject host error signal (ERR0-ERR2).
        /// errorBits: bit 0 = ERR0, bit 1 = ERR1, bit 2 = ERR2.
        /// </summary>
        public void InjectHostError(uint errorBits)
        {
            hostErrorBits = errorBits & 0x7;
            this.Log(LogLevel.Warning, "eSPI: Host error injected: ERR0={0} ERR1={1} ERR2={2}",
                (errorBits & 1) != 0 ? 1 : 0,
                (errorBits & 2) != 0 ? 1 : 0,
                (errorBits & 4) != 0 ? 1 : 0);
        }

        /// <summary>
        /// Clear all host error signals.
        /// </summary>
        public void ClearHostError()
        {
            hostErrorBits = 0;
            this.Log(LogLevel.Info, "eSPI: Host errors cleared");
        }

        /// <summary>
        /// Get current power signal state (monitor-readable).
        /// Returns: [CPUPWRGD, PSPWROK, CATERR, ERR0, ERR1, ERR2] as a bitmask.
        /// Bit 0: CPUPWRGD, Bit 1: PSPWROK, Bit 2: CATERR,
        /// Bit 3: ERR0, Bit 4: ERR1, Bit 5: ERR2
        /// </summary>
        public uint GetPowerSignalState()
        {
            uint state = 0;
            if(cpuPowerGood) state |= 0x01;
            if(psPowerOk)    state |= 0x02;
            if(catErr)       state |= 0x04;
            state |= (hostErrorBits << 3);
            return state;
        }

        // Power signal state
        private bool cpuPowerGood;
        private bool psPowerOk;
        private bool catErr;
        private uint hostErrorBits;

        // ===== ACPI Power State Machine =====
        // Full S0/S3/S4/S5 state transitions matching Birchstream Simics oracle.
        // State transitions drive PLTRST#, sleep bits, and power signals.

        /// <summary>
        /// Current ACPI power state.
        /// </summary>
        public enum AcpiState
        {
            S0_Working = 0,
            S3_SuspendToRam = 3,
            S4_SuspendToDisk = 4,
            S5_SoftOff = 5,
            G3_MechanicalOff = 6
        }

        /// <summary>
        /// Get current ACPI state as an integer.
        /// </summary>
        public int GetAcpiState()
        {
            return (int)currentAcpiState;
        }

        /// <summary>
        /// Get current ACPI state as a human-readable string.
        /// </summary>
        public string GetAcpiStateName()
        {
            return currentAcpiState.ToString();
        }

        /// <summary>
        /// Transition to a new ACPI state. Validates transition legality.
        /// Valid transitions:
        ///   G3 -> S5 (power button), S5 -> S0 (boot), S0 -> S3/S4/S5 (sleep/shutdown)
        ///   S3 -> S0 (resume), S4 -> S0 (resume), S5 -> S0 (power on)
        ///   Any -> G3 (mechanical off / PSU failure)
        /// </summary>
        public bool TransitionAcpiState(int targetStateInt)
        {
            var target = (AcpiState)targetStateInt;
            var current = currentAcpiState;

            if(!IsValidTransition(current, target))
            {
                this.Log(LogLevel.Warning, "eSPI: Invalid ACPI transition {0} -> {1}", current, target);
                return false;
            }

            // Exit actions for current state
            ExitState(current);

            // Entry actions for target state
            EnterState(target);

            var prev = currentAcpiState;
            currentAcpiState = target;
            this.Log(LogLevel.Info, "eSPI: ACPI state transition {0} -> {1}", prev, target);
            return true;
        }

        /// <summary>
        /// Power on sequence: G3 -> S5 -> S0.
        /// Asserts PSPWROK, CPUPWRGD, deasserts PLTRST#.
        /// </summary>
        public void PowerOn()
        {
            if(currentAcpiState == AcpiState.G3_MechanicalOff)
            {
                TransitionAcpiState((int)AcpiState.S5_SoftOff);
            }
            if(currentAcpiState == AcpiState.S5_SoftOff)
            {
                TransitionAcpiState((int)AcpiState.S0_Working);
            }
        }

        /// <summary>
        /// Graceful shutdown: S0 -> S5.
        /// </summary>
        public void GracefulShutdown()
        {
            TransitionAcpiState((int)AcpiState.S5_SoftOff);
        }

        /// <summary>
        /// Suspend to RAM: S0 -> S3.
        /// </summary>
        public void SuspendToRam()
        {
            TransitionAcpiState((int)AcpiState.S3_SuspendToRam);
        }

        /// <summary>
        /// Resume from suspend: S3/S4 -> S0.
        /// </summary>
        public void Resume()
        {
            if(currentAcpiState == AcpiState.S3_SuspendToRam ||
               currentAcpiState == AcpiState.S4_SuspendToDisk)
            {
                TransitionAcpiState((int)AcpiState.S0_Working);
            }
        }

        /// <summary>
        /// Mechanical power off: any -> G3.
        /// </summary>
        public void MechanicalOff()
        {
            TransitionAcpiState((int)AcpiState.G3_MechanicalOff);
        }

        /// <summary>
        /// WDT-triggered reset. Performs cold reset and returns to S0.
        /// wdtIndex: 0-3 for WDT1-WDT4.
        /// </summary>
        public void WatchdogReset(int wdtIndex)
        {
            this.Log(LogLevel.Warning, "eSPI: WDT{0} triggered system reset", wdtIndex + 1);
            lastResetSource = (uint)(0x10 + wdtIndex); // 0x10-0x13 = WDT1-4
            ColdReset();
        }

        /// <summary>
        /// Get the source of the last reset (for diagnostics).
        /// 0=none, 1=cold, 2=warm, 0x10-0x13=WDT1-4.
        /// </summary>
        public uint GetLastResetSource()
        {
            return lastResetSource;
        }

        private bool IsValidTransition(AcpiState from, AcpiState to)
        {
            if(to == AcpiState.G3_MechanicalOff) return true; // always allowed
            switch(from)
            {
                case AcpiState.G3_MechanicalOff:
                    return to == AcpiState.S5_SoftOff;
                case AcpiState.S5_SoftOff:
                    return to == AcpiState.S0_Working;
                case AcpiState.S0_Working:
                    return to == AcpiState.S3_SuspendToRam ||
                           to == AcpiState.S4_SuspendToDisk ||
                           to == AcpiState.S5_SoftOff;
                case AcpiState.S3_SuspendToRam:
                case AcpiState.S4_SuspendToDisk:
                    return to == AcpiState.S0_Working;
                default:
                    return false;
            }
        }

        private void ExitState(AcpiState state)
        {
            switch(state)
            {
                case AcpiState.S0_Working:
                    AssertPlatformReset();
                    break;
                case AcpiState.S3_SuspendToRam:
                case AcpiState.S4_SuspendToDisk:
                    // Clear sleep bits on exit
                    SetHostSleepState(0);
                    break;
            }
        }

        private void EnterState(AcpiState state)
        {
            switch(state)
            {
                case AcpiState.S0_Working:
                    AssertPsPowerOk();
                    AssertCpuPowerGood();
                    DeassertPlatformReset();
                    SetHostSleepState(0);
                    break;
                case AcpiState.S3_SuspendToRam:
                    SetHostSleepState(1); // S3 = bit 0
                    DeassertCpuPowerGood();
                    break;
                case AcpiState.S4_SuspendToDisk:
                    SetHostSleepState(2); // S4 = bit 1
                    DeassertCpuPowerGood();
                    break;
                case AcpiState.S5_SoftOff:
                    SetHostSleepState(4); // S5 = bit 2
                    DeassertCpuPowerGood();
                    break;
                case AcpiState.G3_MechanicalOff:
                    AssertPlatformReset();
                    DeassertCpuPowerGood();
                    DeassertPsPowerOk();
                    SetHostSleepState(0);
                    ClearCatErr();
                    ClearHostError();
                    break;
            }
        }

        private AcpiState currentAcpiState = AcpiState.S0_Working;
        private uint lastResetSource;

        // SAF partition constants (from Birchstream Simics oracle)
        // Host address 0x00000000 - 0x00FFFFFF -> FMC flash @ BMC 0x20000000
        // Host address 0x01000000 - 0x2FFFFFFF -> DRAM @ BMC 0x82000000
        // Host address 0x30000000+              -> 0xFF hole
        private const uint SafBiosRegionEnd = 0x01000000;  // 16 MB
        private const uint SafOsRegionEnd   = 0x30000000;  // 768 MB total window
        private const uint SafBmcFlashBase  = 0x20000000;  // FMC flash window
        private const uint SafBmcDramBase   = 0x82000000;  // BMC DRAM for OS region
        private const uint SafMaxBurstSize  = 64;          // eSPI spec max

        // =================================================================
        // SAF Boot Header Parsing (96 bytes at OS region offset 0)
        // Reference: Birchstream Simics espi_saf_orchestrator
        // =================================================================

        /// <summary>
/// <summary>
        /// Parameterless overload for Renode command interface.
        /// Validates magic and non-zero image size (skips CRC).
        /// </summary>
        public bool ValidateSafBootHeader()
        {
            var sysbus = machine.GetSystemBus(this);
            byte[] header;
            try
            {
                header = sysbus.ReadBytes(SafBmcDramBase, SafBootHeaderSize);
            }
            catch(Exception e)
            {
                this.Log(LogLevel.Error, "SAF: Failed to read boot header at 0x{0:X8}: {1}", SafBmcDramBase, e.Message);
                return false;
            }
            uint magic = BitConverter.ToUInt32(header, 0);
            if(magic != SafBootMagic)
            {
                this.Log(LogLevel.Warning, "SAF: Bad boot header magic 0x{0:X8} (expected 0x{1:X8})", magic, SafBootMagic);
                return false;
            }
            uint imageSize = BitConverter.ToUInt32(header, 8);
            if(imageSize == 0)
            {
                this.Log(LogLevel.Warning, "SAF: Boot header has zero image size");
                return false;
            }
            return true;
        }

        /// Validate the SAF boot header at the start of the OS region.
        /// Returns true if the header is valid (correct magic, non-zero size, CRC match).
        /// </summary>
        public bool ValidateSafBootHeader(out uint imageSize, out uint entryPoint, out uint flags)
        {
            imageSize = 0;
            entryPoint = 0;
            flags = 0;

            var sysbus = machine.GetSystemBus(this);

            // Read 96-byte header from OS region (DRAM @ SafBmcDramBase)
            byte[] header;
            try
            {
                header = sysbus.ReadBytes(SafBmcDramBase, SafBootHeaderSize);
            }
            catch(Exception e)
            {
                this.Log(LogLevel.Error, "SAF: Failed to read boot header at 0x{0:X8}: {1}", SafBmcDramBase, e.Message);
                return false;
            }

            // Check magic: "SAFB" = 0x53414642
            uint magic = BitConverter.ToUInt32(header, 0);
            if(magic != SafBootMagic)
            {
                this.Log(LogLevel.Warning, "SAF: Bad boot header magic 0x{0:X8} (expected 0x{1:X8})", magic, SafBootMagic);
                return false;
            }

            uint version = BitConverter.ToUInt32(header, 4);
            imageSize = BitConverter.ToUInt32(header, 8);
            entryPoint = BitConverter.ToUInt32(header, 12);
            uint headerCrc = BitConverter.ToUInt32(header, 16);
            flags = BitConverter.ToUInt32(header, 20);

            if(imageSize == 0)
            {
                this.Log(LogLevel.Warning, "SAF: Boot header has zero image size");
                return false;
            }

            // Validate CRC over image data (starting at header offset 0x60)
            uint dataOffset = SafBootDataOffset;
            uint dataLen = Math.Min(imageSize, 16 * 1024 * 1024); // cap at 16MB for validation
            byte[] imageData;
            try
            {
                imageData = sysbus.ReadBytes(SafBmcDramBase + dataOffset, (int)dataLen);
            }
            catch(Exception e)
            {
                this.Log(LogLevel.Error, "SAF: Failed to read image data for CRC: {0}", e.Message);
                return false;
            }

            uint computedCrc = ComputeCrc32(imageData, 0, imageData.Length);
            if(computedCrc != headerCrc)
            {
                this.Log(LogLevel.Warning, "SAF: CRC mismatch (header=0x{0:X8} computed=0x{1:X8})", headerCrc, computedCrc);
                return false;
            }

            this.Log(LogLevel.Info, "SAF: Valid boot header: size=0x{0:X} entry=0x{1:X8} flags=0x{2:X}", imageSize, entryPoint, flags);
            return true;
        }

        /// <summary>
        /// WriteSafBootHeader overload with no image data.
        /// </summary>
        public void WriteSafBootHeader(uint imageSize, uint entryPoint, uint flags)
        {
            WriteSafBootHeader(imageSize, entryPoint, flags, new byte[0]);
        }

        /// <summary>
        /// WriteSafBootHeader overload accepting hex data as a string (e.g., "DEADBEEF").
        /// </summary>
        public void WriteSafBootHeader(uint imageSize, uint entryPoint, uint flags, string hexData)
        {
            byte[] data = string.IsNullOrEmpty(hexData) ? new byte[0] : HexStringToBytes(hexData);
            WriteSafBootHeader(imageSize, entryPoint, flags, data);
        }

        /// <summary>
        /// Write a SAF boot header to the OS region in DRAM.
        /// Used by BMC to prepare an image for host consumption.
        /// </summary>
        public void WriteSafBootHeader(uint imageSize, uint entryPoint, uint flags, byte[] imageData)
        {
            var sysbus = machine.GetSystemBus(this);

            // Write image data first (at header offset 0x60)
            if(imageData != null && imageData.Length > 0)
            {
                sysbus.WriteBytes(imageData, SafBmcDramBase + SafBootDataOffset);
            }

            // Compute CRC over image data
            uint crc = (imageData != null) ? ComputeCrc32(imageData, 0, imageData.Length) : 0;

            // Build 96-byte header
            byte[] header = new byte[SafBootHeaderSize];
            BitConverter.GetBytes(SafBootMagic).CopyTo(header, 0);       // Magic
            BitConverter.GetBytes((uint)1).CopyTo(header, 4);            // Version
            BitConverter.GetBytes(imageSize).CopyTo(header, 8);          // Image size
            BitConverter.GetBytes(entryPoint).CopyTo(header, 12);        // Entry point
            BitConverter.GetBytes(crc).CopyTo(header, 16);               // CRC-32
            BitConverter.GetBytes(flags).CopyTo(header, 20);             // Flags
            // bytes 24-63: UUID + reserved (already zeroed)

            sysbus.WriteBytes(header, SafBmcDramBase);

            this.Log(LogLevel.Debug, "SAF: Boot header written: size=0x{0:X} entry=0x{1:X8} crc=0x{2:X8}", imageSize, entryPoint, crc);
        }

        private static byte[] HexStringToBytes(string hex)
        {
            hex = hex.Replace(" ", "").Replace("0x", "").Replace(",", "");
            byte[] bytes = new byte[hex.Length / 2];
            for(int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static uint ComputeCrc32(byte[] data, int start, int length)
        {
            uint crc = 0xFFFFFFFF;
            for(int i = start; i < start + length && i < data.Length; i++)
            {
                crc ^= data[i];
                for(int j = 0; j < 8; j++)
                {
                    if((crc & 1) != 0)
                        crc = (crc >> 1) ^ 0xEDB88320;
                    else
                        crc >>= 1;
                }
            }
            return crc ^ 0xFFFFFFFF;
        }

        // SAF boot header constants
        private const uint SafBootMagic      = 0x53414642;  // "SAFB"
        private const int  SafBootHeaderSize = 96;
        private const uint SafBootDataOffset = 0x60;        // Image data starts after header

        private uint[] mmbiHostRwp = new uint[MmbiMaxInst];
    }
}



