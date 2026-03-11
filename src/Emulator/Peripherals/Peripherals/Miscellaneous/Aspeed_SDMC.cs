//
// Copyright (c) 2026 Gary Beihl
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // Aspeed AST2600 SDRAM Memory Controller (SDMC)
    // Reference: QEMU hw/misc/aspeed_sdmc.c
    //
    // Reports memory configuration so u-boot SPL can determine DRAM size.
    // For 1 GiB: config register encodes size index 2 (256M=0, 512M=1, 1024M=2, 2048M=3).
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public sealed class Aspeed_SDMC : BasicDoubleWordPeripheral, IKnownSize
    {
        public Aspeed_SDMC(IMachine machine) : base(machine)
        {
            DefineRegisters();
        }

        public long Size => 0x1000;

        public override void Reset()
        {
            base.Reset();
            // PHY status: set phy ok (bit 1), PVT cal ok (bit 3 clear)
            RegistersCollection.Write(0x400, 0x00000002);
            // PHY eye window: all passing
            RegistersCollection.Write(0x400 + 0x50, 0x0FFFFFFF);
            RegistersCollection.Write(0x400 + 0x68, 0x000000FF);
            RegistersCollection.Write(0x400 + 0x7C, 0x000000FF);
        }

        private void DefineRegisters()
        {
            // MCR00 — Protection Key
            Registers.ProtectionKey.Define(this, 0x0)
                .WithValueField(0, 32, out protectionKey, name: "PROT_KEY",
                    writeCallback: (_, value) =>
                    {
                        if(value == ProtectionKeyUnlock)
                        {
                            this.Log(LogLevel.Debug, "SDMC unlocked");
                        }
                    });

            // MCR04 — Configuration (HW_VERSION, VGA_APERTURE, DRAM_SIZE are readonly)
            Registers.Configuration.Define(this, DefaultConfig)
                .WithValueField(0, 2, FieldMode.Read, name: "DRAM_SIZE")
                .WithValueField(2, 2, FieldMode.Read, name: "VGA_APERTURE")
                .WithValueField(4, 1, name: "DRAM_TYPE")
                .WithReservedBits(5, 5)
                .WithFlag(10, name: "CACHE_ENABLE")
                .WithFlag(11, name: "CACHE_RANGE_CTRL")
                .WithFlag(12, name: "CACHE_INITIAL")
                .WithFlag(13, name: "CACHE_DDR4_CONF")
                .WithReservedBits(14, 5)
                .WithFlag(19, FieldMode.Read, name: "CACHE_INITIAL_DONE")
                .WithReservedBits(20, 8)
                .WithValueField(28, 4, FieldMode.Read, name: "HW_VERSION");

            // MCR50 — Interrupt Status
            Registers.InterruptStatus.Define(this, 0x0)
                .WithValueField(0, 32, name: "ISR");

            // MCR60 — Status 1 (PHY status: PLL lock always set)
            Registers.Status1.Define(this, PhyPllLockStatus)
                .WithFlag(0, FieldMode.Read, name: "PHY_BUSY")
                .WithReservedBits(1, 3)
                .WithFlag(4, FieldMode.Read, name: "PHY_PLL_LOCK")
                .WithReservedBits(5, 27);

            // MCR6C — reserved (writable)
            Registers.Reserved6C.Define(this, 0x0)
                .WithValueField(0, 32, name: "MCR6C");

            // MCR70 — ECC Test Control (always done, always pass)
            Registers.ECCTestControl.Define(this, 0x0)
                .WithValueField(0, 32, name: "ECC_TEST_CTRL");

            Registers.TestStartLength.Define(this, 0x0)
                .WithValueField(0, 32, name: "TEST_START_LEN");
            Registers.TestFailDQ.Define(this, 0x0)
                .WithValueField(0, 32, name: "TEST_FAIL_DQ");
            Registers.TestInitValue.Define(this, 0x0)
                .WithValueField(0, 32, name: "TEST_INIT_VAL");
            Registers.DramSW.Define(this, 0x0)
                .WithValueField(0, 32, name: "DRAM_SW");
            Registers.DramTime.Define(this, 0x0)
                .WithValueField(0, 32, name: "DRAM_TIME");
            Registers.ECCErrorInject.Define(this, 0x0)
                .WithValueField(0, 32, name: "ECC_ERR_INJECT");

            // PHY registers at 0x400+
            Registers.PhyStatus.Define(this, 0x00000002)
                .WithValueField(0, 32, name: "PHY_STATUS");
            Registers.PhyEyeWindow1.Define(this, 0x0FFFFFFF)
                .WithValueField(0, 32, name: "PHY_EYE1");
            Registers.PhyEyeWindow2.Define(this, 0x000000FF)
                .WithValueField(0, 32, name: "PHY_EYE2");
            Registers.PhyEyeWindow3.Define(this, 0x000000FF)
                .WithValueField(0, 32, name: "PHY_EYE3");
        }

        private bool IsUnlocked => protectionKey.Value == ProtectionKeyUnlock;

        private IValueRegisterField protectionKey;

        private const uint ProtectionKeyUnlock = 0xFC600309;

        // AST2600 1GiB config:
        //   HW_VERSION = 3 (bits 31:28)
        //   VGA_APERTURE = 64MB = 3 (bits 3:2)
        //   DRAM_SIZE = 1GiB = index 2 (bits 1:0)
        private const uint DefaultConfig = (3u << 28) | (3u << 2) | 2u;

        // PHY PLL lock status (bit 4)
        private const uint PhyPllLockStatus = (1u << 4);

        private enum Registers
        {
            ProtectionKey    = 0x000,
            Configuration    = 0x004,
            InterruptStatus  = 0x050,
            Status1          = 0x060,
            Reserved6C       = 0x06C,
            ECCTestControl   = 0x070,
            TestStartLength  = 0x074,
            TestFailDQ       = 0x078,
            TestInitValue    = 0x07C,
            DramSW           = 0x088,
            DramTime         = 0x08C,
            ECCErrorInject   = 0x0B4,
            PhyStatus        = 0x400,
            PhyEyeWindow1    = 0x450,
            PhyEyeWindow2    = 0x468,
            PhyEyeWindow3    = 0x47C,
        }
    }
}
