//
// Copyright (c) 2026 Microsoft
// Licensed under the MIT License.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.I3C
{
    // Aspeed AST2600 I3C Controller — single instance per port
    // Based on DesignWare I3C (DW_apb_i3c) register layout
    //
    // Memory map (0x1000):
    //   0x000-0x0FF: Controller registers
    //   0x100-0x1FF: PIO/queue registers
    //   0x200-0x2FF: Extended capability
    //   0x304+:      Device Address Table (DAT)
    //
    // Stub behavior: no I3C devices attached. Commands return error response.
    // Linux aspeed-i3c/dw-i3c-master driver probes successfully.
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public sealed class Aspeed_I3C : IDoubleWordPeripheral, IKnownSize
    {
        public Aspeed_I3C()
        {
            storage = new uint[RegisterSpaceSize / 4];
            responseQueue = new Queue<uint>();
            
            IRQ = new GPIO();
            
            Reset();
        }

        public long Size => RegisterSpaceSize;

        public GPIO IRQ { get; }

        public void Reset()
        {
            Array.Clear(storage, 0, storage.Length);
            responseQueue.Clear();
            cmdQueuePending = 0;

            // HW_CAPABILITY: HDR-DDR(bit2)=0, HDR-TS(bit1)=0, COMBO(bit0)=0
            storage[RegHwCapability / 4] = 0x00000000;

            // QUEUE_SIZE_CAPABILITY: CR_DEPTH=8, IBI_DEPTH=8, TX_DEPTH=8, RX_DEPTH=8, RESP_DEPTH=8, CMD_DEPTH=8
            storage[RegQueueSizeCapability / 4] = (8u << 24) | (8u << 16) | (8u << 12) | (8u << 8) | (8u << 4) | 8u;

            // DATA_BUFFER_STATUS_LEVEL: TX_BUF_EMPTY_LOC=8, RX_BUF_LVL=0
            storage[RegDataBufferStatusLevel / 4] = 8u << 16;

            // QUEUE_STATUS_LEVEL: RESP_BUF_LVL=0, CMD_QUEUE_EMPTY_LOC=8, IBI_BUF_LVL=0, IBI_STATUS_COUNT=0
            storage[RegQueueStatusLevel / 4] = 8u << 8;

            // DEVICE_ADDR_TABLE_POINTER: DAT start offset=0x304/4=0xC1, depth=11
            storage[RegDatPointer / 4] = (11u << 16) | 0xC1u;

            // DEVICE_CTRL: defaults to 0 (controller disabled)
            storage[RegDeviceCtrl / 4] = 0;

            // DEVICE_CTRL_EXTENDED: MODE=0 (master)
            storage[RegDeviceCtrlExtended / 4] = 0;

            // PRESENT_STATE: idle
            storage[RegPresentState / 4] = 0;

            UpdateIrq();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset < 0 || offset >= RegisterSpaceSize)
            {
                return 0;
            }

            switch((uint)offset)
            {
                case RegResponseQueuePort:
                    return DequeueResponse();

                case RegQueueStatusLevel:
                    // Dynamic: embed response queue level
                    return (storage[(uint)offset / 4] & ~0xFFu) | (uint)responseQueue.Count;

                case RegIntrStatus:
                    return storage[(uint)offset / 4];

                default:
                    return storage[(uint)offset / 4];
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset < 0 || offset >= RegisterSpaceSize)
            {
                return;
            }

            switch((uint)offset)
            {
                case RegIntrStatus: // W1C
                    storage[(uint)offset / 4] &= ~value;
                    UpdateIrq();
                    return;

                case RegCommandQueuePort:
                    HandleCommandWrite(value);
                    return;

                case RegResetCtrl:
                    if((value & 0x07) != 0)
                    {
                        // Soft reset: clear queues
                        responseQueue.Clear();
                        cmdQueuePending = 0;
                        storage[RegIntrStatus / 4] = 0;
                        UpdateIrq();
                    }
                    return;

                case RegIntrStatusEnable:
                    storage[(uint)offset / 4] = value;
                    return;

                case RegIntrSignalEnable:
                    storage[(uint)offset / 4] = value;
                    UpdateIrq();
                    return;

                case RegDeviceCtrl:
                    storage[(uint)offset / 4] = value;
                    if((value & DeviceCtrlEnable) != 0)
                    {
                        this.Log(LogLevel.Debug, "I3C controller enabled");
                    }
                    return;

                case RegHwCapability:
                case RegQueueSizeCapability:
                case RegDatPointer:
                case RegPresentState:
                    // Read-only registers — ignore writes
                    return;

                default:
                    storage[(uint)offset / 4] = value;
                    return;
            }
        }

        private void HandleCommandWrite(uint value)
        {
            // DW I3C uses 2-dword command writes: first=arg, second=cmd descriptor
            // The cmd descriptor (bit 30) contains the Transfer-On-Completion (TOC) bit
            cmdQueuePending++;

            if(cmdQueuePending >= 2)
            {
                // Complete 2-dword command: generate error response (no devices)
                // Response format: [31:28]=ERR_STATUS, [27:24]=TID, [23:16]=DATA_LEN, [15]=CCCT, [0]=response
                uint response = (ErrNoDevice << 28); // Error: no device
                responseQueue.Enqueue(response);
                cmdQueuePending = 0;

                // Set RESP_READY_STS
                storage[RegIntrStatus / 4] |= IntrRespReady;
                // Update CMD_QUEUE_EMPTY_LOC (one slot freed)
                uint qsl = storage[RegQueueStatusLevel / 4];
                uint cmdEmpty = ((qsl >> 8) & 0xFF);
                if(cmdEmpty < 8) cmdEmpty++;
                storage[RegQueueStatusLevel / 4] = (qsl & ~0xFF00u) | (cmdEmpty << 8);

                UpdateIrq();
            }
        }

        private uint DequeueResponse()
        {
            if(responseQueue.Count == 0)
            {
                return 0;
            }

            uint resp = responseQueue.Dequeue();

            // Clear RESP_READY if queue drained
            if(responseQueue.Count == 0)
            {
                storage[RegIntrStatus / 4] &= ~IntrRespReady;
                UpdateIrq();
            }

            return resp;
        }

        private void UpdateIrq()
        {
            uint status = storage[RegIntrStatus / 4];
            uint statusEn = storage[RegIntrStatusEnable / 4];
            uint signalEn = storage[RegIntrSignalEnable / 4];

            // IRQ fires only when status is enabled AND signal is enabled
            bool pending = (status & statusEn & signalEn) != 0;
            IRQ.Set(pending);
        }

        private readonly uint[] storage;
        private readonly Queue<uint> responseQueue;
        private uint cmdQueuePending;

        // Register offsets (DW I3C compatible)
        private const uint RegDeviceCtrl             = 0x000;
        private const uint RegDeviceAddr             = 0x004;
        private const uint RegHwCapability           = 0x008;
        private const uint RegCommandQueuePort       = 0x00C;
        private const uint RegResponseQueuePort      = 0x010;
        private const uint RegTxDataPort             = 0x014;
        private const uint RegRxDataPort             = 0x018;
        private const uint RegIbiQueueStatus         = 0x01C;
        private const uint RegQueueThldCtrl          = 0x020;
        private const uint RegDataBufferThldCtrl     = 0x024;
        private const uint RegIbiQueueCtrl           = 0x028;
        private const uint RegResetCtrl              = 0x034;
        private const uint RegPresentState           = 0x054;
        private const uint RegDeviceCtrlExtended     = 0x0B0;
        private const uint RegIntrStatus             = 0x03C;
        private const uint RegIntrStatusEnable       = 0x040;
        private const uint RegIntrSignalEnable       = 0x044;
        private const uint RegIntrForce              = 0x048;
        private const uint RegQueueStatusLevel       = 0x04C;
        private const uint RegDataBufferStatusLevel  = 0x050;
        private const uint RegQueueSizeCapability    = 0x058;
        private const uint RegDatPointer             = 0x05C;

        // Control bits
        private const uint DeviceCtrlEnable = 1u << 31;

        // Interrupt status bits
        private const uint IntrRespReady     = 1u << 4;
        private const uint IntrCmdQueueReady = 1u << 3;
        private const uint IntrTransferErr   = 1u << 9;

        // Error codes
        private const uint ErrNoDevice = 0x03; // Transfer error / no device

        private const int RegisterSpaceSize = 0x1000;
    }
}