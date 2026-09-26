//
// RT1180 catch-all for a DECODED-BUT-UNMODELLED peripheral block.
//
// This is a deliberate port of the oracle's `create_unimplemented_device`
// (hw/arm/imxrt1180_soc.c:479), and it exists to make Renode's bus behave the way
// QEMU's does in the one respect that turned out to matter for guest fidelity.
//
// ⭐ WHY A BLOCK THAT MODELS NOTHING IS STILL THE CORRECT THING TO REGISTER.
//
// The peripheral bus is DECODED on silicon. An access to an unmodelled register
// inside a real peripheral's aperture does NOT bus-error on hardware -- it is a live
// address that simply does something we have not modelled. Faulting on it would make
// bring-up impossible, because firmware touches dozens of config registers before the
// one under test. So: inside a real peripheral's space, read 0 and discard writes.
//
// Genuinely UNMAPPED memory is the opposite case and MUST fault. That distinction is
// not cosmetic -- see EXPERIMENT.md, "ADDRESS-0 ROOT CAUSE". Zephyr's
// arch_user_string_nlen dereferences a pointer IN ORDER TO TEST whether it is
// reachable, bracketed by z_arm_user_string_nlen_fault_start/_end, and catches the
// fault. Suppress that fault and a NULL reads back as an EMPTY STRING, after which
// execution walks into a validator it should never have reached. We spent two days
// inside the MPU chasing a symptom whose cause was this, two subsystems away.
//
//     A DEFAULT THAT SUPPRESSES AN ERROR THE GUEST IS WRITTEN TO DEPEND ON DOES NOT
//     DEGRADE GRACEFULLY -- IT RELOCATES THE SYMPTOM.
//
// So the platform wants BOTH tiers, exactly as the oracle has them:
//   - this block over each decoded-but-unmodelled peripheral aperture -> returns 0
//   - nothing at all over unmapped memory, with the bus set to fault -> BusFault
//
// Reads return 0 and writes are DISCARDED, matching the oracle rather than improving
// on it: a register file that echoed writes back would diverge from QEMU the moment a
// driver read back a value QEMU reports as 0. If a block here ever needs real
// behaviour, it graduates to its own model -- that is the intended migration path, and
// the log line below is what tells you which block is asking.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_UnimplementedBlock : IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_UnimplementedBlock(IMachine machine, ulong size = 0x10000)
        {
            Size = (long)size;
        }

        public uint ReadDoubleWord(long offset)
        {
            // DEBUG, not WARNING: these are EXPECTED accesses to a decoded aperture.
            // At WARNING they would drown the real unmapped-access signal, which is the
            // thing this whole design exists to keep visible.
            this.Log(LogLevel.Debug, "Unimplemented block: read from offset 0x{0:X}, returning 0", offset);
            return 0;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            this.Log(LogLevel.Debug, "Unimplemented block: write to offset 0x{0:X}, value 0x{1:X}, discarded", offset, value);
        }

        public void Reset()
        {
        }

        public long Size { get; }
    }
}
