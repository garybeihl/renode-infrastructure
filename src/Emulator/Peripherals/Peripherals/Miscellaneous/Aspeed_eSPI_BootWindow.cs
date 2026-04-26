//
// Copyright (c) 2026 Microsoft
// Licensed under the MIT License.
//
// AST2600 eSPI Boot Window - Renode model
// Reference: Birchstream Simics espi_boot_window orchestrator
//
// 8KB shared memory region with state machine:
// EMPTY -> FILLING -> READY -> CONSUMED
//
// Header at offset 0x00:
//   0x00: Magic (0x45535049 = "ESPI")
//   0x04: State (0=EMPTY, 1=FILLING, 2=READY, 3=CONSUMED)
//   0x08: Chunk offset
//   0x0C: Chunk size
//   0x10: Total image size
//   0x14: CRC-32 (over data, not header)
//   0x18-0x1F: Reserved
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class Aspeed_eSPI_BootWindow : IDoubleWordPeripheral, IKnownSize, IGPIOSender
    {
        public Aspeed_eSPI_BootWindow()
        {
            IRQ = new GPIO();
            memory = new byte[WindowSize];
            Reset();
        }

        public long Size => WindowSize;
        public GPIO IRQ { get; }

        public void Reset()
        {
            Array.Clear(memory, 0, memory.Length);
            state = BootWindowState.Empty;
            WriteHeader();
            IRQ.Unset();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset < 0 || offset >= WindowSize)
                return 0;

            return BitConverter.ToUInt32(memory, (int)offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset < 0 || offset >= WindowSize)
                return;

            var bytes = BitConverter.GetBytes(value);
            Array.Copy(bytes, 0, memory, (int)offset, 4);

            // State field write at offset 0x04 drives state machine
            if(offset == HDR_STATE)
            {
                var newState = (BootWindowState)(value & 0x3);
                HandleStateTransition(newState);
            }
        }

        /// <summary>
        /// Begin filling the boot window with data from BMC.
        /// Transitions EMPTY -> FILLING.
        /// </summary>
        public void BeginFill(uint totalImageSize)
        {
            if(state != BootWindowState.Empty)
            {
                this.Log(LogLevel.Warning, "BootWindow: BeginFill called in state {0}", state);
                return;
            }

            state = BootWindowState.Filling;
            WriteHeaderField(HDR_STATE, (uint)state);
            WriteHeaderField(HDR_TOTAL_SIZE, totalImageSize);
            WriteHeaderField(HDR_CHUNK_OFFSET, 0);
            WriteHeaderField(HDR_CHUNK_SIZE, 0);
            this.Log(LogLevel.Debug, "BootWindow: EMPTY -> FILLING (totalSize=0x{0:X})", totalImageSize);
        }

        /// <summary>
        /// Write a chunk of data into the boot window data area.
        /// </summary>
        public void WriteChunk(uint offset, byte[] data)
        {
            if(state != BootWindowState.Filling)
            {
                this.Log(LogLevel.Warning, "BootWindow: WriteChunk called in state {0}", state);
                return;
            }

            uint dataStart = HeaderSize + offset;
            if(dataStart + data.Length > WindowSize)
            {
                this.Log(LogLevel.Warning, "BootWindow: Chunk exceeds window (offset=0x{0:X} len={1})", offset, data.Length);
                return;
            }

            Array.Copy(data, 0, memory, (int)dataStart, data.Length);
            WriteHeaderField(HDR_CHUNK_OFFSET, offset);
            WriteHeaderField(HDR_CHUNK_SIZE, (uint)data.Length);
        }

        /// <summary>
        /// Mark the boot window as ready for host consumption.
        /// Transitions FILLING -> READY. Computes CRC.
        /// </summary>
        public void MarkReady()
        {
            if(state != BootWindowState.Filling)
            {
                this.Log(LogLevel.Warning, "BootWindow: MarkReady called in state {0}", state);
                return;
            }

            uint totalSize = ReadHeaderField(HDR_TOTAL_SIZE);
            uint crc = ComputeCrc32(memory, (int)HeaderSize, (int)Math.Min(totalSize, (uint)(WindowSize - (int)HeaderSize)));
            WriteHeaderField(HDR_CRC32, crc);

            state = BootWindowState.Ready;
            WriteHeaderField(HDR_STATE, (uint)state);
            this.Log(LogLevel.Debug, "BootWindow: FILLING -> READY (crc=0x{0:X8})", crc);

            // Notify host via IRQ
            IRQ.Set(true);
        }

        private void HandleStateTransition(BootWindowState newState)
        {
            // Host can write CONSUMED to acknowledge
            if(newState == BootWindowState.Consumed && state == BootWindowState.Ready)
            {
                state = BootWindowState.Consumed;
                this.Log(LogLevel.Debug, "BootWindow: READY -> CONSUMED");
                IRQ.Unset();
            }
            else if(newState == BootWindowState.Empty)
            {
                // Reset to empty (e.g., after host reset)
                state = BootWindowState.Empty;
                WriteHeader();
                this.Log(LogLevel.Debug, "BootWindow: -> EMPTY (reset)");
                IRQ.Unset();
            }
            else
            {
                this.Log(LogLevel.Warning, "BootWindow: Invalid transition {0} -> {1}", state, newState);
                // Restore correct state in header
                WriteHeaderField(HDR_STATE, (uint)state);
            }
        }

        private void WriteHeader()
        {
            WriteHeaderField(HDR_MAGIC, Magic);
            WriteHeaderField(HDR_STATE, (uint)state);
            WriteHeaderField(HDR_CHUNK_OFFSET, 0);
            WriteHeaderField(HDR_CHUNK_SIZE, 0);
            WriteHeaderField(HDR_TOTAL_SIZE, 0);
            WriteHeaderField(HDR_CRC32, 0);
        }

        private void WriteHeaderField(long offset, uint value)
        {
            var bytes = BitConverter.GetBytes(value);
            Array.Copy(bytes, 0, memory, (int)offset, 4);
        }

        private uint ReadHeaderField(long offset)
        {
            return BitConverter.ToUInt32(memory, (int)offset);
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

        private readonly byte[] memory;
        private BootWindowState state;

        // Header field offsets
        private const long HDR_MAGIC        = 0x00;
        private const long HDR_STATE        = 0x04;
        private const long HDR_CHUNK_OFFSET = 0x08;
        private const long HDR_CHUNK_SIZE   = 0x0C;
        private const long HDR_TOTAL_SIZE   = 0x10;
        private const long HDR_CRC32        = 0x14;

        private const uint HeaderSize = 0x20;
        private const int  WindowSize = 8192;  // 8 KB
        private const uint Magic      = 0x45535049;  // "ESPI"

        private enum BootWindowState : uint
        {
            Empty    = 0,
            Filling  = 1,
            Ready    = 2,
            Consumed = 3
        }
    }
}
