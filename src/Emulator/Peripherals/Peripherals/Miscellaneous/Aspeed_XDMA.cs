//
// Copyright (c) 2026 Microsoft
// Licensed under the MIT License.
//
// AST2600 XDMA Engine (Cross-Domain DMA) - Renode model
// Reference: Linux drivers/soc/aspeed/aspeed-xdma.c, QEMU hw/misc/aspeed_xdma.c
//
// Implements circular command queue with 16-byte descriptors,
// sysbus DMA transfers, and W1C completion/error interrupts.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public sealed class Aspeed_XDMA : IDoubleWordPeripheral, IKnownSize, IGPIOSender
    {
        public Aspeed_XDMA(IMachine machine)
        {
            this.machine = machine;
            IRQ = new GPIO();
            storage = new uint[RegisterSpaceSize / 4];
            Reset();
        }

        public long Size => RegisterSpaceSize;
        public GPIO IRQ { get; }

        public void Reset()
        {
            Array.Clear(storage, 0, storage.Length);
            storage[R_STATUS] = STATUS_RESET;
            busy = false;
            IRQ.Unset();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset >= 0 && offset < RegisterSpaceSize)
            {
                return storage[(uint)offset / 4];
            }
            return 0;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset < 0 || offset >= RegisterSpaceSize)
                return;

            var reg = (uint)offset / 4;

            switch(reg)
            {
                case R_CTRL:
                    storage[reg] = value & CTRL_W_MASK;
                    return;

                case R_STATUS:
                    // W1C (write-1-to-clear)
                    storage[reg] &= ~value;
                    UpdateIRQ();
                    return;

                case R_BMC_CMDQ_WRP:
                    storage[reg] = value;
                    TryProcessQueue();
                    return;

                default:
                    storage[reg] = value;
                    return;
            }
        }

        /// <summary>
        /// Process pending descriptors in the circular command queue.
        /// Triggered by WRP write when WRP != RDP.
        /// </summary>
        private void TryProcessQueue()
        {
            if(busy)
            {
                this.Log(LogLevel.Warning, "XDMA: WRP written while engine busy, ignoring");
                return;
            }

            uint wrp = storage[R_BMC_CMDQ_WRP];
            uint rdp = storage[R_BMC_CMDQ_RDP];

            if(wrp == rdp)
                return;

            uint queueBase = storage[R_BMC_CMDQ_ADDR];
            uint queueEnd = storage[R_BMC_CMDQ_ENDP];

            if(queueBase == 0 || queueEnd == 0)
            {
                this.Log(LogLevel.Warning, "XDMA: Queue not configured (base=0x{0:X8} end=0x{1:X8})", queueBase, queueEnd);
                return;
            }

            busy = true;
            bool error = false;
            int processed = 0;
            var sysbus = machine.GetSystemBus(this);

            while(rdp != wrp && processed < MaxDescriptors)
            {
                uint maxEntries = (queueEnd > 0) ? queueEnd / DescriptorSize : 1;
                uint descAddr = queueBase + (maxEntries > 0 ? (rdp % maxEntries) : rdp) * DescriptorSize;

                // Read 16-byte descriptor: src, dst, length, flags
                uint srcAddr, dstAddr, xferLen, flags;
                try
                {
                    srcAddr = sysbus.ReadDoubleWord(descAddr);
                    dstAddr = sysbus.ReadDoubleWord(descAddr + 4);
                    xferLen = sysbus.ReadDoubleWord(descAddr + 8);
                    flags   = sysbus.ReadDoubleWord(descAddr + 12);
                }
                catch(Exception e)
                {
                    this.Log(LogLevel.Error, "XDMA: Failed to read descriptor at 0x{0:X8}: {1}", descAddr, e.Message);
                    error = true;
                    break;
                }

                if(xferLen == 0)
                {
                    this.Log(LogLevel.Warning, "XDMA: Zero-length descriptor at 0x{0:X8}", descAddr);
                    // Zero-length: skip silently
                    processed++;
                    rdp++;
                    continue;
                }

                if((xferLen & 0x7) != 0)
                {
                    this.Log(LogLevel.Warning, "XDMA: Misaligned transfer length 0x{0:X} at 0x{1:X8}", xferLen, descAddr);
                    error = true;
                    break;
                }

                if(xferLen > MaxTransferSize)
                {
                    this.Log(LogLevel.Warning, "XDMA: Transfer too large (0x{0:X} > 0x{1:X}) at 0x{2:X8}", xferLen, MaxTransferSize, descAddr);
                    error = true;
                    break;
                }

                if((srcAddr & 0x7) != 0)
                {
                    this.Log(LogLevel.Warning, "XDMA: Misaligned source address 0x{0:X8} at 0x{1:X8}", srcAddr, descAddr);
                    error = true;
                    break;
                }

                if((dstAddr & 0x7) != 0)
                {
                    this.Log(LogLevel.Warning, "XDMA: Misaligned destination address 0x{0:X8} at 0x{1:X8}", dstAddr, descAddr);
                    error = true;
                    break;
                }

                // Execute DMA transfer via sysbus
                try
                {
                    var data = sysbus.ReadBytes(srcAddr, (int)xferLen);
                    sysbus.WriteBytes(data, dstAddr);
                    this.Log(LogLevel.Debug, "XDMA: DMA 0x{0:X8}->0x{1:X8} len=0x{2:X}", srcAddr, dstAddr, xferLen);
                }
                catch(Exception e)
                {
                    this.Log(LogLevel.Error, "XDMA: DMA transfer failed (0x{0:X8}->0x{1:X8} len=0x{2:X}): {3}", srcAddr, dstAddr, xferLen, e.Message);
                    error = true;
                    break;
                }

                processed++;

                // Advance read pointer (linear counter like WRP)
                rdp++;
            }

            storage[R_BMC_CMDQ_RDP] = rdp;

            if(error)
            {
                storage[R_STATUS] |= STATUS_DS_DIRTY;
                this.Log(LogLevel.Warning, "XDMA: Chain halted after {0} descriptors due to error", processed);
            }
            else
            {
                storage[R_STATUS] |= STATUS_DS_COMP;
                this.Log(LogLevel.Debug, "XDMA: Completed {0} descriptors", processed);
            }

            busy = false;
            UpdateIRQ();
        }

        private void UpdateIRQ()
        {
            uint pending = storage[R_STATUS] & storage[R_CTRL] & IRQ_MASK;
            IRQ.Set(pending != 0);
        }

        private readonly IMachine machine;
        private readonly uint[] storage;
        private bool busy;

        // AST2600 register offsets (word index = byte_offset / 4)
        private const uint R_HOST_CMDQ_ADDR0 = 0x00 / 4;
        private const uint R_HOST_CMDQ_ADDR1 = 0x04 / 4;
        private const uint R_HOST_CMDQ_ENDP  = 0x08 / 4;
        private const uint R_HOST_CMDQ_WRP   = 0x0C / 4;
        private const uint R_HOST_CMDQ_RDP   = 0x10 / 4;
        private const uint R_BMC_CMDQ_ADDR   = 0x14 / 4;
        private const uint R_BMC_CMDQ_ENDP   = 0x18 / 4;
        private const uint R_BMC_CMDQ_WRP    = 0x1C / 4;
        private const uint R_BMC_CMDQ_RDP    = 0x20 / 4;
        private const uint R_CTRL            = 0x38 / 4;
        private const uint R_STATUS          = 0x3C / 4;

        // STATUS register bits (AST2600)
        private const uint STATUS_US_COMP  = 1u << 16;
        private const uint STATUS_DS_COMP  = 1u << 17;
        private const uint STATUS_DS_DIRTY = 1u << 18;
        private const uint STATUS_RESET    = 0xF8000000;
        private const uint IRQ_MASK        = STATUS_US_COMP | STATUS_DS_COMP | STATUS_DS_DIRTY;

        // CTRL register write mask
        private const uint CTRL_W_MASK     = 0x017003FF;

        // Descriptor format
        private const uint DescriptorSize  = 16;
        private const uint MaxTransferSize = 16 * 1024 * 1024; // 16 MB
        private const int  MaxDescriptors  = 256;

        private const int RegisterSpaceSize = 0x1000;
    }
}

