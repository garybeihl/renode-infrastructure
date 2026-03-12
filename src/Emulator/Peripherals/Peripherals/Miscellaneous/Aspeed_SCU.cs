//
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // Aspeed AST2600 System Control Unit (SCU)
    // Reference: QEMU hw/misc/aspeed_scu.c, u-boot drivers/clk/aspeed/clk_ast2600.c
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public sealed class Aspeed_SCU : BasicDoubleWordPeripheral, IKnownSize
    {
        public Aspeed_SCU(IMachine machine) : base(machine)
        {
            DefineRegisters();
        }

        public long Size => 0x1000;

        private bool IsUnlocked => protectionKey.Value == ProtectionKeyValue;

        private void DefineRegisters()
        {
            // SCU000 - Protection Key Register
            Registers.ProtectionKey.Define(this, 0x0)
                .WithValueField(0, 32, out protectionKey, name: "PROTECTION_KEY",
                    writeCallback: (_, value) =>
                    {
                        if(value == ProtectionKeyValue)
                        {
                            this.Log(LogLevel.Debug, "SCU unlocked");
                        }
                    });

            // SCU004 - Silicon Revision ID
            Registers.SiliconRevision.Define(this, SiliconRevisionAST2600A3)
                .WithValueField(0, 32, FieldMode.Read, name: "SILICON_REV");

            // SCU010 - Clock Stop Control Register
            Registers.ClockStop1.Define(this, 0x0)
                .WithValueField(0, 32, name: "CLK_STOP1");

            // SCU014 - Clock Stop Control Clear Register
            Registers.ClockStop1Clear.Define(this)
                .WithValueField(0, 32, name: "CLK_STOP1_CLR");

            // SCU040 - Hardware Strap 1
            Registers.HardwareStrap1.Define(this, DefaultHWStrap1)
                .WithValueField(0, 32, FieldMode.Read, name: "HW_STRAP1");

            // SCU050 - System Reset Control Register 1
            Registers.ResetControl1.Define(this, 0x0)
                .WithValueField(0, 32, name: "SYS_RESET_CTRL1");

            // SCU054 - System Reset Control Clear 1
            Registers.ResetControl1Clear.Define(this, 0x0)
                .WithValueField(0, 32, name: "SYS_RESET_CLR1");

            // SCU060 - System Reset Control Register 2
            Registers.ResetControl2.Define(this, 0x0)
                .WithValueField(0, 32, name: "SYS_RESET_CTRL2");

            // --- PLL Registers ---
            // Each PLL has a parameter register (R/W) and an extension register.
            // The extension register bit 31 = PLL lock status (always locked in emulation).

            // SCU200 - HPLL Parameter
            Registers.HPLL.Define(this, 0x1000405F)
                .WithValueField(0, 32, name: "HPLL_PARAM");

            // SCU204 - HPLL Extension (bit 31 = locked)
            Registers.HPLLExt.Define(this, PllLockedBit)
                .WithValueField(0, 31, name: "HPLL_EXT_PARAMS")
                .WithFlag(31, FieldMode.Read, name: "HPLL_LOCK",
                    valueProviderCallback: _ => true);

            // SCU210 - APLL Parameter
            Registers.APLL.Define(this, 0x10004077)
                .WithValueField(0, 32, name: "APLL_PARAM");

            // SCU214 - APLL Extension (bit 31 = locked)
            Registers.APLLExt.Define(this, PllLockedBit)
                .WithValueField(0, 31, name: "APLL_EXT_PARAMS")
                .WithFlag(31, FieldMode.Read, name: "APLL_LOCK",
                    valueProviderCallback: _ => true);

            // SCU220 - MPLL Parameter (DDR clock)
            Registers.MPLL.Define(this, 0x1008405F)
                .WithValueField(0, 32, name: "MPLL_PARAM");

            // SCU224 - MPLL Extension (bit 31 = locked)
            Registers.MPLLExt.Define(this, PllLockedBit)
                .WithValueField(0, 31, name: "MPLL_EXT_PARAMS")
                .WithFlag(31, FieldMode.Read, name: "MPLL_LOCK",
                    valueProviderCallback: _ => true);

            // SCU240 - EPLL Parameter (Ethernet clock)
            Registers.EPLL.Define(this, 0x1004077F)
                .WithValueField(0, 32, name: "EPLL_PARAM");

            // SCU244 - EPLL Extension (bit 31 = locked)
            Registers.EPLLExt.Define(this, PllLockedBit)
                .WithValueField(0, 31, name: "EPLL_EXT_PARAMS")
                .WithFlag(31, FieldMode.Read, name: "EPLL_LOCK",
                    valueProviderCallback: _ => true);

            // SCU260 - DPLL Parameter (Display clock)
            Registers.DPLL.Define(this, 0x10000000)
                .WithValueField(0, 32, name: "DPLL_PARAM");

            // SCU264 - DPLL Extension (bit 31 = locked)
            Registers.DPLLExt.Define(this, PllLockedBit)
                .WithValueField(0, 31, name: "DPLL_EXT_PARAMS")
                .WithFlag(31, FieldMode.Read, name: "DPLL_LOCK",
                    valueProviderCallback: _ => true);

            // --- Clock Source Selection ---

            // SCU300 - Clock Source Selection 1
            Registers.ClockSel1.Define(this, 0x0)
                .WithValueField(0, 32, name: "CLK_SEL1");

            // SCU304 - Clock Source Selection 2
            Registers.ClockSel2.Define(this, 0x0)
                .WithValueField(0, 32, name: "CLK_SEL2");

            // SCU308 - Clock Source Selection 3
            Registers.ClockSel3.Define(this, 0x0)
                .WithValueField(0, 32, name: "CLK_SEL3");

            // SCU310 - Clock Source Selection 4
            Registers.ClockSel4.Define(this, 0x0)
                .WithValueField(0, 32, name: "CLK_SEL4");

            // SCU340 - Clock Source Selection 5
            Registers.ClockSel5.Define(this, 0x0)
                .WithValueField(0, 32, name: "CLK_SEL5");

            // SCU350 - Clock Duty Selection
            Registers.ClockDuty.Define(this, 0x0)
                .WithValueField(0, 32, name: "CLK_DUTY");

            // --- Strap and miscellaneous ---

            // SCU500 - Hardware Strap Security
            Registers.HardwareStrapSecurity.Define(this, 0x0)
                .WithValueField(0, 32, FieldMode.Read, name: "HW_STRAP_SEC");

            // SCU510 - Hardware Strap Security 2
            Registers.HardwareStrapSecurity2.Define(this, 0x0)
                .WithValueField(0, 32, FieldMode.Read, name: "HW_STRAP_SEC2");

            // --- CPU scratch pad registers (0x180-0x1AC, 20 dwords) ---
            // u-boot uses these for SMP boot protocol and general storage
            for(uint i = 0; i < 20; i++)
            {
                var offset = 0x180 + (i * 4);
                ((Registers)offset).Define(this, 0x0)
                    .WithValueField(0, 32, name: $"CPU_SCRATCH_{i}");
            }

            // SCU820/824/C24 - Misc config registers touched by u-boot
            Registers.MiscCtrl820.Define(this, 0x0)
                .WithValueField(0, 32, name: "MISC_CTRL_820");

            Registers.MiscCtrl824.Define(this, 0x0)
                .WithValueField(0, 32, name: "MISC_CTRL_824");

            Registers.MiscCtrlC24.Define(this, 0x0)
                .WithValueField(0, 32, name: "MISC_CTRL_C24");
        }

        private IValueRegisterField protectionKey;

        private const uint SiliconRevisionAST2600A3 = 0x05030303;
        private const uint ProtectionKeyValue = 0x1688A8A8;
        private const uint DefaultHWStrap1 = 0x00000000;
        // Bit 31 = PLL locked
        private const uint PllLockedBit = 0x80000000;

        private enum Registers
        {
            ProtectionKey         = 0x000,
            SiliconRevision       = 0x004,
            ClockStop1            = 0x010,
            ClockStop1Clear       = 0x014,
            HardwareStrap1        = 0x040,
            ResetControl1         = 0x050,
            ResetControl1Clear    = 0x054,
            ResetControl2         = 0x060,
            // CPU scratch pad: 0x180-0x1CC (defined dynamically)
            HPLL                  = 0x200,
            HPLLExt               = 0x204,
            APLL                  = 0x210,
            APLLExt               = 0x214,
            MPLL                  = 0x220,
            MPLLExt               = 0x224,
            EPLL                  = 0x240,
            EPLLExt               = 0x244,
            DPLL                  = 0x260,
            DPLLExt               = 0x264,
            ClockSel1             = 0x300,
            ClockSel2             = 0x304,
            ClockSel3             = 0x308,
            ClockSel4             = 0x310,
            ClockSel5             = 0x340,
            ClockDuty             = 0x350,
            HardwareStrapSecurity = 0x500,
            HardwareStrapSecurity2= 0x510,
            MiscCtrl820           = 0x820,
            MiscCtrl824           = 0x824,
            MiscCtrlC24           = 0xC24,
        }
    }
}
