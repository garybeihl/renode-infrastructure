// Copyright (c) 2026 Microsoft
// Licensed under the MIT license.
//
// Aspeed AST2600 FTGMAC100 Ethernet MAC Controller (Stub)
// Ported from QEMU hw/net/ftgmac100.c
//
// This is a functional stub that responds to register reads/writes
// and provides PHY (RTL8211E) responses so u-boot/Linux probes
// succeed without hanging. No actual networking is implemented.

using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class Aspeed_FTGMAC100 : IDoubleWordPeripheral, IKnownSize, IGPIOSender
    {
        public Aspeed_FTGMAC100()
        {
            registers = new uint[RegisterSpaceSize / 4];
            phyRegs = new uint[32];
            IRQ = new GPIO();
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            if (offset < 0 || offset >= RegisterSpaceSize)
            {
                this.Log(LogLevel.Warning, "FTGMAC read out of range: 0x{0:X}", offset);
                return 0;
            }

            int idx = (int)(offset / 4);

            switch (offset)
            {
                case FTGMAC100_PHYDATA:
                    // Return PHY read data in bits [31:16]
                    return registers[idx];

                default:
                    return registers[idx];
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if (offset < 0 || offset >= RegisterSpaceSize)
            {
                this.Log(LogLevel.Warning, "FTGMAC write out of range: 0x{0:X}", offset);
                return;
            }

            int idx = (int)(offset / 4);

            switch (offset)
            {
                case FTGMAC100_ISR:
                    // Write-1-to-clear
                    registers[idx] &= ~value;
                    UpdateIRQ();
                    break;

                case FTGMAC100_IER:
                    registers[idx] = value;
                    UpdateIRQ();
                    break;

                case FTGMAC100_MACCR:
                    HandleMACCR(value);
                    break;

                case FTGMAC100_PHYCR:
                    registers[idx] = value;
                    HandlePHYCR(value);
                    break;

                case FTGMAC100_PHYDATA:
                    registers[idx] = value;
                    break;

                default:
                    registers[idx] = value;
                    break;
            }
        }

        public void Reset()
        {
            Array.Clear(registers, 0, registers.Length);

            // Key reset values from QEMU
            registers[FTGMAC100_DBLAC / 4] = 0x00022F00;
            registers[FTGMAC100_RBSR / 4] = 0x640;
            registers[FTGMAC100_REVR / 4] = 0x00;

            // Initialize PHY registers (RTL8211E)
            Array.Clear(phyRegs, 0, phyRegs.Length);
            phyRegs[MII_BMCR] = 0x1000;       // Auto-negotiation enable
            phyRegs[MII_BMSR] = 0x796D;        // Link up, AN complete, capable
            phyRegs[MII_PHYID1] = 0x001C;      // RTL8211E OUI high
            phyRegs[MII_PHYID2] = 0xC916;      // RTL8211E model 0x19, rev 6
            phyRegs[MII_ANAR] = 0x01E1;        // Advertise capabilities
            phyRegs[MII_ANLPAR] = 0x45E1;      // Link partner abilities
            phyRegs[MII_1000BTCR] = 0x0300;    // 1000BASE-T control
            phyRegs[MII_1000BTSR] = 0x7C00;    // 1000BASE-T status

            IRQ.Unset();
        }

        public long Size => RegisterSpaceSize;

        public GPIO IRQ { get; }

        // --- Private helpers ---

        private void HandleMACCR(uint value)
        {
            if ((value & MACCR_SW_RST) != 0)
            {
                // SW_RST: preserve GIGA_MODE and FAST_MODE, reset everything else
                uint preserved = registers[FTGMAC100_MACCR / 4] & (MACCR_GIGA_MODE | MACCR_FAST_MODE);
                Reset();
                registers[FTGMAC100_MACCR / 4] = preserved;
            }
            else
            {
                registers[FTGMAC100_MACCR / 4] = value;
            }
        }

        private void HandlePHYCR(uint value)
        {
            uint phyAddr = (value >> 16) & 0x1F;
            uint phyReg = (value >> 21) & 0x1F;

            if ((value & PHYCR_MIIWR) != 0)
            {
                // PHY write: data from PHYDATA[15:0]
                uint writeData = registers[FTGMAC100_PHYDATA / 4] & 0xFFFF;
                if (phyReg < 32)
                {
                    phyRegs[phyReg] = writeData;
                    // Ensure link always stays up
                    phyRegs[MII_BMSR] |= BMSR_LINK_ST;
                }
                // Clear the write trigger
                registers[FTGMAC100_PHYCR / 4] &= ~PHYCR_MIIWR;
            }
            else if ((value & PHYCR_MIIRD) != 0)
            {
                // PHY read: result goes to PHYDATA[31:16]
                uint readData = 0;
                if (phyReg < 32)
                {
                    readData = phyRegs[phyReg];
                }
                registers[FTGMAC100_PHYDATA / 4] = (readData << 16) | (registers[FTGMAC100_PHYDATA / 4] & 0xFFFF);
                // Clear the read trigger
                registers[FTGMAC100_PHYCR / 4] &= ~PHYCR_MIIRD;
            }
        }

        private void UpdateIRQ()
        {
            uint pending = registers[FTGMAC100_ISR / 4] & registers[FTGMAC100_IER / 4];
            if (pending != 0)
                IRQ.Set(true);
            else
                IRQ.Set(false);
        }

        private uint[] registers;
        private uint[] phyRegs;

        // Register space
        private const int RegisterSpaceSize = 0x1000;

        // Key register offsets
        private const long FTGMAC100_ISR      = 0x00;   // Interrupt Status (W1C)
        private const long FTGMAC100_IER      = 0x04;   // Interrupt Enable
        private const long FTGMAC100_MAC_MADR = 0x08;   // MAC address high
        private const long FTGMAC100_MAC_LADR = 0x0C;   // MAC address low
        private const long FTGMAC100_NPTXR_BADR = 0x20; // TX descriptor ring base
        private const long FTGMAC100_RXR_BADR = 0x24;   // RX descriptor ring base
        private const long FTGMAC100_HPTXR_BADR = 0x28; // High-priority TX ring
        private const long FTGMAC100_ITC      = 0x30;   // Interrupt timer control
        private const long FTGMAC100_DBLAC    = 0x38;   // DMA burst length/arb
        private const long FTGMAC100_REVR     = 0x40;   // Revision
        private const long FTGMAC100_FEAR     = 0x44;   // Feature
        private const long FTGMAC100_RBSR     = 0x4C;   // RX buffer size
        private const long FTGMAC100_MACCR    = 0x50;   // MAC control
        private const long FTGMAC100_PHYCR    = 0x60;   // PHY control (MII)
        private const long FTGMAC100_PHYDATA  = 0x64;   // PHY data (MII)
        private const long FTGMAC100_FCR      = 0x68;   // Flow control

        // MACCR bits
        private const uint MACCR_SW_RST    = (1u << 31);
        private const uint MACCR_GIGA_MODE = (1u << 9);
        private const uint MACCR_FAST_MODE = (1u << 19);

        // PHYCR bits
        private const uint PHYCR_MIIRD = (1u << 26);
        private const uint PHYCR_MIIWR = (1u << 27);

        // PHY (MII) register indices
        private const int MII_BMCR     = 0;   // Basic Mode Control
        private const int MII_BMSR     = 1;   // Basic Mode Status
        private const int MII_PHYID1   = 2;   // PHY ID 1
        private const int MII_PHYID2   = 3;   // PHY ID 2
        private const int MII_ANAR     = 4;   // Auto-Neg Advertisement
        private const int MII_ANLPAR   = 5;   // Auto-Neg Link Partner
        private const int MII_1000BTCR = 9;   // 1000BASE-T Control
        private const int MII_1000BTSR = 10;  // 1000BASE-T Status

        // PHY BMSR bits
        private const uint BMSR_LINK_ST = (1u << 2);
    }
}
