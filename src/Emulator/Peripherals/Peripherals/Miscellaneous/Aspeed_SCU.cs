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
    // Aspeed AST2600 System Control Unit (SCU)
    // Reference: QEMU hw/misc/aspeed_scu.c
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

            // SCU050 - System Reset Control Register
            Registers.ResetControl1.Define(this, 0x0)
                .WithValueField(0, 32, name: "SYS_RESET_CTRL1");

            // SCU200 - Hardware Strap 2
            Registers.HardwareStrap2.Define(this, DefaultHWStrap2)
                .WithValueField(0, 32, FieldMode.Read, name: "HW_STRAP2");

            // SCU500 - Hardware Strap Security
            Registers.HardwareStrapSecurity.Define(this, 0x0)
                .WithValueField(0, 32, FieldMode.Read, name: "HW_STRAP_SEC");
        }

        private IValueRegisterField protectionKey;

        // AST2600-A3 silicon revision
        private const uint SiliconRevisionAST2600A3 = 0x05030303;
        private const uint ProtectionKeyValue = 0x1688A8A8;
        // Default hardware strap: boot from SPI, 1GiB DRAM
        private const uint DefaultHWStrap1 = 0x00000000;
        private const uint DefaultHWStrap2 = 0x00000000;

        private enum Registers
        {
            ProtectionKey         = 0x000,
            SiliconRevision       = 0x004,
            ClockStop1            = 0x010,
            ClockStop1Clear       = 0x014,
            HardwareStrap1        = 0x040,
            ResetControl1         = 0x050,
            HardwareStrap2        = 0x200,
            HardwareStrapSecurity = 0x500,
        }
    }
}
