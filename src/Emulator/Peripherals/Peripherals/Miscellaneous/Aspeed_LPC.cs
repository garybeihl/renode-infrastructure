// Copyright (c) 2026 Microsoft
// Licensed under the MIT license.
//
// Aspeed AST2600 LPC Controller with 4 KCS channels
// Ported from QEMU hw/misc/aspeed_lpc.c
//
// Enhanced with IPMI override table and host-side injection
// for Birchstream co-simulation support.

using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class Aspeed_LPC : IDoubleWordPeripheral, IKnownSize, IGPIOSender
    {
        public Aspeed_LPC()
        {
            registers = new uint[RegisterSpaceSize / 4];
            IRQ = new GPIO();
            ipmiOverrides = new Dictionary<ushort, byte[]>();
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            if (offset < 0 || offset >= RegisterSpaceSize)
            {
                this.Log(LogLevel.Warning, "LPC read out of range: 0x{0:X}", offset);
                return 0;
            }

            int idx = (int)(offset / 4);
            uint val = registers[idx];

            // IDR read side-effects: clear IBF, lower IRQ
            switch (offset)
            {
                case IDR1:
                    ClearIBF(0, STR1);
                    break;
                case IDR2:
                    ClearIBF(1, STR2);
                    break;
                case IDR3:
                    ClearIBF(2, STR3);
                    break;
                case IDR4:
                    ClearIBF(3, STR4);
                    break;
            }

            return val;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if (offset < 0 || offset >= RegisterSpaceSize)
            {
                this.Log(LogLevel.Warning, "LPC write out of range: 0x{0:X}", offset);
                return;
            }

            int idx = (int)(offset / 4);

            switch (offset)
            {
                // IDR write: store data, set IBF, raise IRQ if enabled
                case IDR1:
                    registers[idx] = value & 0xFF;
                    SetIBF(0, STR1);
                    break;
                case IDR2:
                    registers[idx] = value & 0xFF;
                    SetIBF(1, STR2);
                    break;
                case IDR3:
                    registers[idx] = value & 0xFF;
                    SetIBF(2, STR3);
                    break;
                case IDR4:
                    registers[idx] = value & 0xFF;
                    SetIBF(3, STR4);
                    break;

                // ODR write: store data, set OBF
                case ODR1:
                    registers[idx] = value & 0xFF;
                    registers[STR1 / 4] |= STR_OBF;
                    break;
                case ODR2:
                    registers[idx] = value & 0xFF;
                    registers[STR2 / 4] |= STR_OBF;
                    break;
                case ODR3:
                    registers[idx] = value & 0xFF;
                    registers[STR3 / 4] |= STR_OBF;
                    break;
                case ODR4:
                    registers[idx] = value & 0xFF;
                    registers[STR4 / 4] |= STR_OBF;
                    break;

                // STR: R/W for all bits
                case STR1:
                case STR2:
                case STR3:
                case STR4:
                    registers[idx] = value & 0xFF;
                    break;

                // HICR0: channel enables — may affect IRQ state
                case HICR0:
                    registers[idx] = value;
                    UpdateIRQ();
                    break;

                // HICR2: IBF IRQ enables
                case HICR2:
                    registers[idx] = value;
                    UpdateIRQ();
                    break;

                // HICR4: KCS3 enable bit
                case HICR4:
                    registers[idx] = value;
                    UpdateIRQ();
                    break;

                // HICRB: KCS4 enable + IBF IRQ enable
                case HICRB:
                    registers[idx] = value;
                    UpdateIRQ();
                    break;

                default:
                    registers[idx] = value;
                    break;
            }
        }

        public void Reset()
        {
            Array.Clear(registers, 0, registers.Length);
            registers[HICR7 / 4] = Hicr7ResetValue;
            subdeviceIrqsPending = 0;
            IRQ.Unset();
        }

        public long Size => RegisterSpaceSize;

        public GPIO IRQ { get; }

        // Configurable HICR7 (chip ID) — survives reset
        public uint Hicr7ResetValue { get; set; } = 0;

        // --- XDMA Boot Metadata Properties ---

        public uint XdmaBaseAddress { get; set; } = 0x80001000;

        public uint XdmaTransferSize { get; set; } = 0x200000;

        // --- IPMI Override Table ---

        /// <summary>
        /// Configures an auto-response for a given IPMI (netFn, cmd) pair.
        /// When a host IPMI command matching this pair is injected, the override
        /// response is immediately written to the ODR with completion code 0x00.
        /// </summary>
        /// <summary>
        /// Configures an auto-response with empty response data.
        /// </summary>
        public void SetIpmiOverride(byte netFn, byte cmd)
        {
            SetIpmiOverride(netFn, cmd, new byte[0]);
        }

        public void SetIpmiOverride(byte netFn, byte cmd, byte[] responseData)
        {
            ushort key = MakeOverrideKey(netFn, cmd);
            ipmiOverrides[key] = responseData ?? new byte[0];
            this.Log(LogLevel.Debug, "IPMI override set: netFn=0x{0:X2} cmd=0x{1:X2} responseLen={2}",
                     netFn, cmd, (responseData != null ? responseData.Length : 0));
        }

        /// <summary>
        /// Removes an override for the given IPMI (netFn, cmd) pair.
        /// </summary>
        public void ClearIpmiOverride(byte netFn, byte cmd)
        {
            ushort key = MakeOverrideKey(netFn, cmd);
            if (ipmiOverrides.Remove(key))
            {
                this.Log(LogLevel.Debug, "IPMI override cleared: netFn=0x{0:X2} cmd=0x{1:X2}", netFn, cmd);
            }
        }

        /// <summary>
        /// Clears all IPMI overrides.
        /// </summary>
        public void ClearAllIpmiOverrides()
        {
            ipmiOverrides.Clear();
            this.Log(LogLevel.Debug, "All IPMI overrides cleared");
        }

        // --- Host-side IPMI Injection ---

        /// <summary>
        /// Simulates the host writing an IPMI command to a KCS channel.
        /// Follows the KCS write protocol: CMD_DATA set + netFn to IDR,
        /// clear CMD_DATA + cmd to IDR, data bytes to IDR, then END byte.
        /// If an override exists, the response is immediately placed in the ODR.
        /// </summary>
        /// <summary>
        /// Simulates a host IPMI command with no request data on channel 0.
        /// </summary>
        public void SendHostIpmiCommand(byte netFn, byte cmd)
        {
            SendHostIpmiCommand(netFn, cmd, null, 0);
        }

        /// <summary>
        /// Simulates a host IPMI command with no request data on specified channel.
        /// </summary>
        public void SendHostIpmiCommand(byte netFn, byte cmd, int channel)
        {
            SendHostIpmiCommand(netFn, cmd, null, channel);
        }

        public void SendHostIpmiCommand(byte netFn, byte cmd, byte[] requestData, int channel = 0)
        {
            if (channel < 0 || channel > 3)
            {
                this.Log(LogLevel.Warning, "IPMI inject: invalid channel {0}", channel);
                return;
            }

            long idrOffset = GetIDROffset(channel);
            long odrOffset = GetODROffset(channel);
            long strOffset = GetSTROffset(channel);

            this.Log(LogLevel.Debug, "IPMI inject: ch={0} netFn=0x{1:X2} cmd=0x{2:X2} dataLen={3}",
                     channel, netFn, cmd, (requestData != null ? requestData.Length : 0));

            // KCS write protocol: set CMD_DATA bit, write netFn to IDR
            registers[strOffset / 4] |= STR_CD;
            WriteDoubleWord(idrOffset, netFn);

            // Clear CMD_DATA, write cmd to IDR
            registers[strOffset / 4] &= ~STR_CD;
            WriteDoubleWord(idrOffset, cmd);

            // Write each data byte to IDR
            if (requestData != null)
            {
                foreach (byte b in requestData)
                {
                    WriteDoubleWord(idrOffset, b);
                }
            }

            // Write END byte (0x00) with CMD_DATA set
            registers[strOffset / 4] |= STR_CD;
            WriteDoubleWord(idrOffset, 0x00);

            // Check for override response
            ushort key = MakeOverrideKey(netFn, cmd);
            byte[] overrideResponse;
            if (ipmiOverrides.TryGetValue(key, out overrideResponse))
            {
                this.Log(LogLevel.Debug, "IPMI override hit: netFn=0x{0:X2} cmd=0x{1:X2}, responding with {2} bytes",
                         netFn, cmd, overrideResponse.Length);

                // Write completion code 0x00 to ODR
                WriteDoubleWord(odrOffset, 0x00);

                // Write override response data bytes to ODR
                foreach (byte b in overrideResponse)
                {
                    WriteDoubleWord(odrOffset, b);
                }
            }
            else
            {
                this.Log(LogLevel.Debug, "IPMI no override for netFn=0x{0:X2} cmd=0x{1:X2}, leaving for BMC software",
                         netFn, cmd);
            }
        }

        // --- Birchstream Boot Overrides ---

        /// <summary>
        /// Configures default IPMI overrides for Birchstream co-simulation boot flow.
        /// </summary>
        public void SetupBirchstreamDefaults()
        {
            this.Log(LogLevel.Debug, "Setting up Birchstream boot IPMI overrides");

            // Get Device ID (netFn=0x06, cmd=0x01): Birchstream BMC device ID
            SetIpmiOverride(0x06, 0x01, new byte[] {
                0x20,       // Device ID
                0x01,       // Device Revision
                0x02,       // Firmware Revision 1
                0x03,       // Firmware Revision 2
                0x02,       // IPMI Version (2.0)
                0xBF,       // Additional Device Support
                0x2A, 0xCD, 0x00, // Manufacturer ID (Aspeed)
                0x00, 0x00  // Product ID
            });

            // Get Boot Options (netFn=0x08, cmd=0x09): boot device = eSPI SAF
            SetIpmiOverride(0x08, 0x09, new byte[] {
                0x01,       // Parameter version
                0x05,       // Parameter selector (boot flags)
                0x80,       // Parameter valid / boot flags valid
                0x24,       // Boot device = eSPI SAF (bits 5:2 = 0b1001)
                0x00,       // BIOS verbosity / console redirection
                0x00        // BIOS shared mode override
            });

            // Get System Boot Options (netFn=0x00, cmd=0x08): XDMA metadata
            SetIpmiOverride(0x00, 0x08, new byte[] {
                (byte)(XdmaBaseAddress & 0xFF),
                (byte)((XdmaBaseAddress >> 8) & 0xFF),
                (byte)((XdmaBaseAddress >> 16) & 0xFF),
                (byte)((XdmaBaseAddress >> 24) & 0xFF),
                (byte)(XdmaTransferSize & 0xFF),
                (byte)((XdmaTransferSize >> 8) & 0xFF),
                (byte)((XdmaTransferSize >> 16) & 0xFF),
                (byte)((XdmaTransferSize >> 24) & 0xFF)
            });
        }

        // --- Private helpers ---

        private bool IsChannelEnabled(int ch)
        {
            switch (ch)
            {
                case 0: return (registers[HICR0 / 4] & HICR0_LPC1E) != 0;
                case 1: return (registers[HICR0 / 4] & HICR0_LPC2E) != 0;
                case 2: return (registers[HICR0 / 4] & HICR0_LPC3E) != 0
                             && (registers[HICR4 / 4] & HICR4_KCSENBL) != 0;
                case 3: return (registers[HICRB / 4] & HICRB_LPC4E) != 0;
                default: return false;
            }
        }

        private bool IsIBFIRQEnabled(int ch)
        {
            if (!IsChannelEnabled(ch))
                return false;

            switch (ch)
            {
                case 0: return (registers[HICR2 / 4] & HICR2_IBFIE1) != 0;
                case 1: return (registers[HICR2 / 4] & HICR2_IBFIE2) != 0;
                case 2: return (registers[HICR2 / 4] & HICR2_IBFIE3) != 0;
                case 3: return (registers[HICRB / 4] & HICRB_IBFIE4) != 0;
                default: return false;
            }
        }

        private void SetIBF(int ch, long strOffset)
        {
            registers[strOffset / 4] |= STR_IBF;

            if (IsIBFIRQEnabled(ch))
            {
                subdeviceIrqsPending |= (1u << ch);
                UpdateIRQ();
            }
        }

        private void ClearIBF(int ch, long strOffset)
        {
            bool wasSet = (registers[strOffset / 4] & STR_IBF) != 0;
            registers[strOffset / 4] &= ~STR_IBF;

            if (wasSet)
            {
                subdeviceIrqsPending &= ~(1u << ch);
                UpdateIRQ();
            }
        }

        private void UpdateIRQ()
        {
            if (subdeviceIrqsPending != 0)
                IRQ.Set(true);
            else
                IRQ.Set(false);
        }

        private static ushort MakeOverrideKey(byte netFn, byte cmd)
        {
            return (ushort)((netFn << 8) | cmd);
        }

        private static long GetIDROffset(int channel)
        {
            switch (channel)
            {
                case 0: return IDR1;
                case 1: return IDR2;
                case 2: return IDR3;
                case 3: return IDR4;
                default: return IDR1;
            }
        }

        private static long GetODROffset(int channel)
        {
            switch (channel)
            {
                case 0: return ODR1;
                case 1: return ODR2;
                case 2: return ODR3;
                case 3: return ODR4;
                default: return ODR1;
            }
        }

        private static long GetSTROffset(int channel)
        {
            switch (channel)
            {
                case 0: return STR1;
                case 1: return STR2;
                case 2: return STR3;
                case 3: return STR4;
                default: return STR1;
            }
        }

        private uint[] registers;
        private uint subdeviceIrqsPending;
        private readonly Dictionary<ushort, byte[]> ipmiOverrides;

        // Register space
        private const int RegisterSpaceSize = 0x1000;

        // HICR registers
        private const long HICR0 = 0x00;
        private const long HICR1 = 0x04;
        private const long HICR2 = 0x08;
        private const long HICR3 = 0x0C;
        private const long HICR4 = 0x10;
        private const long HICR5 = 0x80;
        private const long HICR6 = 0x84;
        private const long HICR7 = 0x88;
        private const long HICR8 = 0x8C;
        private const long HICRB = 0x100;

        // KCS Channel 1-3 registers
        private const long IDR1 = 0x24;
        private const long IDR2 = 0x28;
        private const long IDR3 = 0x2C;
        private const long ODR1 = 0x30;
        private const long ODR2 = 0x34;
        private const long ODR3 = 0x38;
        private const long STR1 = 0x3C;
        private const long STR2 = 0x40;
        private const long STR3 = 0x44;

        // KCS Channel 4 registers
        private const long IDR4 = 0x114;
        private const long ODR4 = 0x118;
        private const long STR4 = 0x11C;

        // HICR0 bits
        private const uint HICR0_LPC1E = (1u << 5);
        private const uint HICR0_LPC2E = (1u << 6);
        private const uint HICR0_LPC3E = (1u << 7);

        // HICR2 bits
        private const uint HICR2_IBFIE1 = (1u << 1);
        private const uint HICR2_IBFIE2 = (1u << 2);
        private const uint HICR2_IBFIE3 = (1u << 3);

        // HICR4 bits
        private const uint HICR4_KCSENBL = (1u << 2);

        // HICRB bits
        private const uint HICRB_LPC4E = (1u << 0);
        private const uint HICRB_IBFIE4 = (1u << 1);

        // STR bits
        private const uint STR_OBF = (1u << 0);
        private const uint STR_IBF = (1u << 1);

        // KCS protocol constants
        private const uint STR_CD = (1u << 3);
        private const byte KCS_WRITE_START = 0x61;
        private const byte KCS_WRITE_END = 0x62;
    }
}