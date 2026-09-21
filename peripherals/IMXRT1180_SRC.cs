//
// i.MX RT1180 SRC + BLK_CTRL_S_AONMIX — Cortex-M7 boot/release control.
// PORTED FROM THE QEMU ORACLE: hw/misc/imxrt1180_src.c. Not re-derived.
//
// This is the TWO-GATE AMP BOOT, which @rt1180emulator records as the hardest
// single mechanism in their project (commit 31a8c444c4).
//
// The boot Cortex-M33 releases the Cortex-M7 (per the SDK's Prepare_CM7):
//   1. BLK_CTRL_S_AONMIX->M7_CFG.INITVTOR = m7_vtor >> 7   (M7 boot-vector base)
//   2. SRC_GENERAL->SCR |= BT_RELEASE_M7                    (release the reset)
//   3. ... image is copied into the M7 TCM ...
//   4. M7_CFG.WAIT (CPUWAIT) cleared                        (second gate: GO)
//
// ⭐ BOTH GATES ARE REQUIRED. The SDK performs step 2 with the image NOT YET
// COPIED and CPUWAIT still high, then clears CPUWAIT once the image is in
// place. Modelling only one gate boots the M7 on garbage. Either write can be
// the one that completes the pair, so both handlers re-check.
//
// Register facts [brief] + [oracle]:
//   SRC_GENERAL    @ 0x44460000, SCR    @ 0x10, BT_RELEASE_M7 = 0x1
//   BLK_CTRL_S_AON @ 0x444F0000, M7_CFG @ 0x80, INITVTOR = [31:7],
//                                              WAIT (CPUWAIT) = bit 4, POR = 1
//
// ⚠ RESET BEHAVIOUR IS LOAD-BEARING (oracle commit 622319f30f): on reset the M7
// must be RE-PARKED. In QEMU a system_reset left the M7 running on a stale
// vector -> HardFault lockup. Reset() below restores WAIT=1 and drops the
// pending release, so the M33 must redo the full two-gate sequence.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // One class serves both windows; `isBlkCtrl` picks which register file this
    // instance exposes. They share the gate state, so the platform passes the
    // SRC instance to the BLK_CTRL instance.
    public class IMXRT1180_SRC : IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_SRC(IMachine machine, IMXRT1180_SRC gates = null,
                             ICPU secondaryCore = null, bool isBlkCtrl = false)
        {
            this.machine = machine;
            this.isBlkCtrl = isBlkCtrl;
            this.shared = gates ?? this;
            regs = new uint[Size / 4];
            if(secondaryCore != null)
            {
                shared.SecondaryCore = secondaryCore;
            }
            if(gates != null)
            {
                // The BLK_CTRL window registers itself with the gate holder, so
                // the two halves find each other without a monitor init step.
                gates.blkCtrlBacking = this;
            }
            Reset();
        }

        public long Size => 0x1000;

        // The M7 core, wired from the platform's init section.
        public ICPU SecondaryCore { get; set; }

        public void Reset()
        {
            for(var i = 0; i < regs.Length; i++)
            {
                regs[i] = 0;
            }
            if(isBlkCtrl)
            {
                // CPUWAIT resets HIGH: on POR the M7 is held. [brief][oracle]
                regs[M7Cfg / 4] = M7CfgWait;
            }
            if(shared == this)
            {
                releasePending = false;
                m7Running = false;
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            return (offset + 4 > Size || offset < 0) ? 0u : regs[offset / 4];
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size || offset < 0)
            {
                return;
            }
            regs[offset / 4] = value;

            if(!isBlkCtrl && offset == SrcScr && (value & ScrBtReleaseM7) != 0)
            {
                // Gate 1. The reset is released but the core still WAITs until
                // CPUWAIT goes low -- remember it so a later WAIT-clear starts
                // the M7. Prepare_CM7 releases here with the image NOT yet
                // copied. [oracle]
                shared.releasePending = true;
                this.Log(LogLevel.Info, "SRC.SCR.BT_RELEASE_M7 set -- gate 1 open (M7 reset released, still held by CPUWAIT)");
                shared.MaybeStartM7();
            }
            else if(isBlkCtrl && offset == M7Cfg)
            {
                if((value & M7CfgWait) == 0)
                {
                    // Gate 2: MCMGR_StartCore clears CPUWAIT after the image is
                    // in place. [oracle]
                    this.Log(LogLevel.Info, "M7_CFG.WAIT cleared -- gate 2 open (INITVTOR=0x{0:X})", value & M7CfgInitVtorMask);
                }
                shared.MaybeStartM7();
            }
        }

        // Start the M7 iff BOTH gates are open.
        private void MaybeStartM7()
        {
            if(m7Running || !releasePending)
            {
                return;
            }
            var blk = blkCtrl;
            if(blk == null)
            {
                return;
            }
            var cfg = blk.regs[M7Cfg / 4];
            if((cfg & M7CfgWait) != 0)
            {
                return;   // still held by CPUWAIT
            }
            var core = SecondaryCore ?? blk.SecondaryCore;
            if(core == null)
            {
                this.Log(LogLevel.Warning, "Both M7 gates are open but no secondary core is wired to this peripheral");
                return;
            }

            // INITVTOR is the vector-table BASE in bits [31:7].
            var vtor = cfg & M7CfgInitVtorMask;
            m7Running = true;
            this.Log(LogLevel.Info, "BOTH GATES OPEN -- starting the Cortex-M7 at vector table 0x{0:X8}", vtor);
            StartCore(core, vtor);
        }

        private void StartCore(ICPU core, uint vtor)
        {
            // The M7 has no TrustZone-M: it resets NON-secure and fetches its
            // initial SP/PC from the non-secure vector base. [oracle]
            var cm = core as Antmicro.Renode.Peripherals.CPU.CortexM;
            if(cm != null)
            {
                cm.VectorTableOffset = vtor;
                // ⚠ READ IN THE M7's OWN ADDRESS CONTEXT. INITVTOR is a
                // LOCAL-view address: the M7 boots from its ITCM at 0x0, which
                // is a cpu1-scoped registration and is NOT visible on the
                // global bus. Reading without the context yields garbage --
                // the exact "advanced for the wrong reason" trap, one level up.
                var sysbus = machine.GetSystemBus(this);
                cm.SP = sysbus.ReadDoubleWord(vtor, cm);
                cm.PC = sysbus.ReadDoubleWord(vtor + 4, cm);
                this.Log(LogLevel.Info, "M7 initial SP=0x{0:X8} PC=0x{1:X8}", cm.SP, cm.PC);
            }
            // ⭐⭐ DEFER THE UNHALT. Setting IsHalted = false on the OTHER core from
            // inside a bus-write handler runs on the writing CPU's thread, in the
            // middle of that CPU's transaction -- and cpu1 does not resume.
            //
            // MEASURED: with the gates opening correctly and SP/PC set from the M7's
            // own context, cpu1's PC stayed at 0x303c0008 -- its entry, never a
            // single instruction -- for the whole run. Unhalting the SAME core from
            // the monitor instead, with the SAME PC/SP, advances it to 0x303c000e
            // and the M7 stamps 0xCAFEBABE into the shared flag. So the core, the
            // vector, the image and the memory were all correct; only the moment of
            // the unhalt was wrong.
            //
            // Handing it to the time source runs it after the current access
            // completes. This is the THIRD instance of one shape in this model set:
            // the LPADC's DMA re-assert (re-entered the eDMA mid-transaction), the
            // ELE's RNG write (resolved a CPU-local address with no context), and
            // now this. All three are "a peripheral acting on state outside its own
            // transaction, from inside one".
            var deferTarget = core;
            machine.LocalTimeSource.ExecuteInNearestSyncedState(_ =>
            {
                deferTarget.IsHalted = false;
                // ⚠ IsHalted = false ALONE IS NOT ENOUGH for a core that was halted
                // when the machine started. MEASURED: after the gates opened, cpu1
                // read IsHalted = False with the correct PC (0x303c0008) and still
                // executed ZERO instructions for the whole run. The same core,
                // unhalted from the monitor BEFORE the machine started, advances and
                // stamps its magic. So clearing the flag does not re-arm the core's
                // time sink; Resume() does.
                deferTarget.Resume();
            });
        }

        private IMXRT1180_SRC blkCtrl
        {
            get { return isBlkCtrl ? this : blkCtrlBacking; }
            set { blkCtrlBacking = value; }
        }

        private readonly IMachine machine;
        private readonly bool isBlkCtrl;
        private readonly IMXRT1180_SRC shared;
        private readonly uint[] regs;
        private IMXRT1180_SRC blkCtrlBacking;
        private bool releasePending;
        private bool m7Running;

        private const long SrcScr = 0x10;             // SRC_GENERAL->SCR
        private const long M7Cfg = 0x80;              // BLK_CTRL_S_AONMIX->M7_CFG
        private const uint ScrBtReleaseM7 = 0x1u;
        private const uint M7CfgInitVtorMask = 0xFFFFFF80u;
        private const uint M7CfgWait = 0x10u;         // CPUWAIT: 1 = held (POR), 0 = go
    }
}
