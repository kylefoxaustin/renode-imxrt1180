//
// i.MX RT1180 CCM (Clock Controller Module) — M0-scope gate/root model.
// PORTED FROM THE QEMU ORACLE: hw/misc/imxrt1180_ccm.c. Not re-derived.
// All offsets, counts, steps and reset values below are copied from that model
// (which verified them against the MIMXRT1189 CMSIS PERI_CCM.h).
//
// WHAT IS MODELLED: register storage, the SET/CLR/TOG alias groups, and the
// poll-completion behaviour firmware actually waits on —
//   LPCG[n].STATUS0.ON mirrors LPCG[n].DIRECT.ON, so CLOCK_ControlGate's poll
//   completes. (This is the M0 hang: BOARD init spun on LPCG[34].STATUS0 at
//   0x444588A0 = CCM 0x8000 + 34*0x40 + 0x20.)
//
// ⚠ WHAT IS **NOT** MODELLED — stated so it cannot become a silent wrong:
// this class does NOT compute clock frequencies. The oracle's OBSERVE
// FREQ_CURRENT path returns a COMPUTED frequency; here that read is declined
// loudly rather than answered with a fabricated 0. Nothing on the M0 path
// reads it; if a later rung does, port ccm_obs_hz() properly.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_CCM : IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_CCM(IMachine machine, IMXRT1180_Anadig anadig = null)
        {
            this.anadig = anadig;
            regs = new uint[Size / 4];
            Reset();
        }

        public long Size => 0x10000;   // IMXRT1180_CCM_SIZE [oracle]


        // ── RM reset values this model was missing ──────────────────────────────
        //
        // Exactly the 18 CCM registers that scripts/reset_values_check.sh MEASURED as
        // reading 0 where the Reference Manual specifies non-zero. Values SOURCED from
        // rm-golden.json (IMXRT1180RM.pdf + CMSIS), machine-generated into this table
        // rather than transcribed -- the adc2 IRQ in m1.repl records what a hand-typed
        // constant costs.
        //
        // ⚠️ DELIBERATELY NARROW. The golden carries 868 CCM registers with a non-zero
        // reset, and this model ALREADY returns the right value for 850 of them -- several
        // through computed reads (LPCG STATUS0 mirrors DIRECT.ON so CLOCK_ControlGate's
        // poll completes). Seeding all 868 would override 850 values that are already
        // correct and could displace those computed paths, for no gain. Fix what was
        // measured wrong, not everything the golden mentions.
        //
        // ⭐ THE *_MIN REGISTERS ARE THE INTERESTING ONES, AND THEIR RESET IS LOAD-BEARING.
        //
        // OBSERVE{0,1}_FREQUENCY_MIN / PERIOD_MIN / HIGH_MIN / LOW_MIN reset to ALL-ONES.
        // That is not decoration: a "minimum seen so far" register must start at its
        // MAXIMUM so the first real measurement replaces it. Reset it to 0 -- which is
        // what this model did -- and the minimum is 0 forever, because no measurement can
        // ever go lower. The register then reports a perfectly plausible value that is
        // permanently wrong, and a driver computing a clock from it gets nonsense with no
        // error raised anywhere.
        //
        // Same class as RTWDOG CS resetting to 0 instead of 0x900, and as LPUART FIFO
        // asserting RXUF at reset: A ZERO RESET VALUE IS NOT THE ABSENCE OF A CLAIM, IT IS
        // A CLAIM -- here, "the minimum frequency ever observed is zero".
        private static readonly System.Collections.Generic.Dictionary<long, uint> RmResetValues =
            new System.Collections.Generic.Dictionary<long, uint>
        {
            { 0x04430, 0xFFFF0000 },  // OBSERVE0_AUTHEN
            { 0x04434, 0xFFFF0000 },  // OBSERVE0_AUTHEN_SET
            { 0x04438, 0xFFFF0000 },  // OBSERVE0_AUTHEN_CLR
            { 0x0443C, 0xFFFF0000 },  // OBSERVE0_AUTHEN_TOG
            { 0x04444, 0xFFFFFFC0 },  // OBSERVE0_FREQUENCY_MIN
            { 0x04454, 0xFFFFFFFF },  // OBSERVE0_PERIOD_MIN
            { 0x04464, 0xFFFFFFFF },  // OBSERVE0_HIGH_MIN
            { 0x04474, 0xFFFFFFFF },  // OBSERVE0_LOW_MIN
            { 0x044B0, 0xFFFF0000 },  // OBSERVE1_AUTHEN
            { 0x044B4, 0xFFFF0000 },  // OBSERVE1_AUTHEN_SET
            { 0x044B8, 0xFFFF0000 },  // OBSERVE1_AUTHEN_CLR
            { 0x044BC, 0xFFFF0000 },  // OBSERVE1_AUTHEN_TOG
            { 0x044C4, 0xFFFFFFC0 },  // OBSERVE1_FREQUENCY_MIN
            { 0x044D4, 0xFFFFFFFF },  // OBSERVE1_PERIOD_MIN
            { 0x044E4, 0xFFFFFFFF },  // OBSERVE1_HIGH_MIN
            { 0x044F4, 0xFFFFFFFF },  // OBSERVE1_LOW_MIN
            { 0x04A10, 0xFF000100 },  // GPR_SHARED_STATUS4
            { 0x04A14, 0x00000007 },  // GPR_SHARED_STATUS5
        };

        public void Reset()
        {
            for(var i = 0; i < regs.Length; i++)
            {
                regs[i] = 0;
            }
            foreach(var kv in RmResetValues)
            {
                regs[kv.Key / 4] = kv.Value;
            }
            for(var n = 0; n < RootCount; n++)
            {
                regs[(RootBase + n * RootStep + RootAuthen) / 4] = 0xFFFF0000;
            }
            for(var n = 0; n < OscPllCount; n++)
            {
                var b = OscPllBase + n * OscPllStep;
                regs[(b + Direct) / 4] = 0x00000001;    // enabled out of reset
                regs[(b + Status0) / 4] = 0x00000001;
                regs[(b + Status1) / 4] = 0x0000FFFF;
                regs[(b + Authen) / 4] = 0xFFFF0000;
            }
            for(var n = 0; n < LpcgCount; n++)
            {
                var b = LpcgBase + n * LpcgStep;
                regs[(b + Direct) / 4] = 0x00000001;    // gate ON out of reset
                regs[(b + Status0) / 4] = 0x00000001;
                regs[(b + Status1) / 4] = 0x0000FFFF;
                regs[(b + Authen) / 4] = 0xFFFF0000;
            }
            for(var n = 0; n < GprPrivCount; n++)
            {
                var b = GprPrivBase + n * GprPrivStep + GprPrivAuthen;
                regs[(b + 0x0) / 4] = 0xFFFF0000;
                regs[(b + 0x4) / 4] = 0xFFFF0000;
                regs[(b + 0x8) / 4] = 0xFFFF0000;
                regs[(b + 0xC) / 4] = 0xFFFF0000;
            }
            // GPR_SHARED[16].AUTHEN @0x4810, step 0x20. The SET/CLR/TOG siblings
            // are ALIASES and read the base, so they need no reset value of
            // their own — which is why the RM prints the same 0xFFFF0000 for
            // all four. [oracle]
            for(var n = 0; n < 16; n++)
            {
                regs[(0x4810 + n * 0x20) / 4] = 0xFFFF0000;
            }
        }

        // ───────────────────────────────────────────────────────────────────────
        // ⭐ THE CLOCK TREE. Real frequencies computed from the GUEST'S OWN
        // REGISTERS -- never a plausible constant.
        //
        // Ported from the QEMU oracle's imxrt1180_ccm.c (ccm_root_hz / ccm_src_hz /
        // ccm_audio_pll_hz / ccm_pfd_hz). Added because the SAI is the first block
        // in this platform that needs a NUMBER out of the CCM rather than an
        // acknowledgement: its sample rate is
        //     BCLK = MCLK / (2*(TCR2[DIV]+1)); fs = BCLK / ((FRSZ+1)*(W0W+1))
        // and MCLK is CLOCK_ROOT[65] (SAI1).
        //
        // ⚠ 0 MEANS 0, AND IT IS NOT A FALLBACK -- the caller must not tick.
        // The oracle's CCM once returned a FABRICATED 6 MHz from
        // OBSERVE.FREQUENCY_CURRENT straight into the guest's baud-rate arithmetic,
        // and its only "flag" was a C comment the firmware could not read. So an
        // unmodelled or ungated root returns 0 AND LOGS AT RUNTIME -- never a
        // comment, never a default. ("A ?: is not a safety net -- it is a place for
        // a bug to live where no test will look.")
        public uint GetClockRootHz(int root)
        {
            if(root < 0 || root >= RootCount)
            {
                return 0;
            }
            var ctrl = regs[(RootBase + root * RootStep) / 4];
            if((ctrl & RootOff) != 0)
            {
                return 0;                                  // the guest gated this root off
            }
            var mux = (int)((ctrl >> RootMuxShift) & RootMuxMask);
            var div = (ctrl & RootDivMask) + 1;            // the field holds DIV-1
            var srcHz = GetSourceHz(root, mux);
            if(srcHz == 0)
            {
                // ⭐ SAID ONCE PER (root, mux), NOT ONCE PER CALL. MEASURED: the
                // virtual-motor plant queries this root at its 50 kHz step rate, and
                // an un-rate-limited warning produced 51,676 identical lines in a
                // 2-second run -- 99.7% of the log. A warning that buries every other
                // line is not more informative than one that fires once; it is less,
                // because it hides the ones that matter. Re-armed on reset, so a
                // later re-mux still reports.
                var key = (root << 8) | (mux & 0xFF);
                if(!deadSourceLogged.Contains(key))
                {
                    deadSourceLogged.Add(key);
                    this.Log(LogLevel.Warning, "Clock root {0} (mux {1}) yields NO frequency -- the "
                        + "source is gated or not modelled. The consuming block has no clock and "
                        + "must not run. This is deliberate: a plausible default here is how six "
                        + "timers ran at the wrong rate undetected. (Reported once per root+mux.)",
                        root, mux);
                }
                return 0;
            }
            return srcHz / div;
        }

        // Only the roots this platform actually needs are wired. Anything else
        // returns 0 and says so -- an unmodelled root must never masquerade as a
        // real frequency.
        private uint GetSourceHz(int root, int mux)
        {
            switch(root)
            {
                case RootSai1:
                case RootSai2:
                case RootSai3:
                case RootSai4:
                case RootAsrc:
                    // { OSC_RC_24M, OSC_RC_400M, AUDIO_PLL, SYS_PLL3_PFD2 }
                    switch(mux)
                    {
                        case 0: return OscRc24M;
                        case 1: return OscRc400M;
                        case 2: return AudioPllHz();
                        default: return PfdHz(AnadigSysPll3Pfd, SysPll3_480Hz, 2);
                    }
                case RootBusWakeup:
                    // ⭐ ADDED FOR THE cm7 FOC ROW. The EQDC's QD-timer clock is this
                    // root, and the mc_pmsm speed driver derives its scaling constant
                    // from it -- so a wrong value here silently rescales every rotor
                    // speed the control loop reads, with nothing in the console to say
                    // so. The mux table is TAKEN FROM THE ORACLE's ccm root table
                    // (hw/misc/imxrt1180_ccm.c, row 4 BUS_WAKEUP), not inferred:
                    //   { OSC_RC_24M, OSC_RC_400M, SYS_PLL2, SYS_PLL3_PFD1 }
                    switch(mux)
                    {
                        case 0: return OscRc24M;
                        case 1: return OscRc400M;
                        case 2: return SysPll2_528Hz;
                        default: return PfdHz(AnadigSysPll3Pfd, SysPll3_480Hz, 1);
                    }
                default:
                    if(!unmodelledRootLogged.Contains(root))
                    {
                        unmodelledRootLogged.Add(root);
                        this.Log(LogLevel.Warning, "Clock root {0} is not modelled in this platform; "
                            + "reporting 0 Hz rather than guessing.", root);
                    }
                    return 0;
            }
        }

        // CLOCK_GetPllFreq(kCLOCK_PllAudio): XTAL*(DIV_SELECT + NUMER/DENOM) >> POST_DIV.
        // Unconfigured => 0, not a guess.
        private uint AudioPllHz()
        {
            if(anadig == null)
            {
                this.Log(LogLevel.Warning, "AUDIO PLL frequency requested but no ANADIG is wired "
                    + "to this CCM -- reporting 0 Hz rather than guessing.");
                return 0;
            }
            var ctrl0 = anadig.PeekRaw(AnadigAudioPllCtrl0);
            var numer = anadig.PeekRaw(AnadigAudioPllNumer);
            var denom = anadig.PeekRaw(AnadigAudioPllDenom);
            var div   = ctrl0 & AudioPllDivSelectMask;
            var post  = (int)((ctrl0 >> AudioPllPostDivShift) & AudioPllPostDivMask);
            if(div == 0 || denom == 0)
            {
                return 0;                                  // PLL not programmed
            }
            var hz = (ulong)XtalHz * div + (ulong)XtalHz * numer / denom;
            return (uint)(hz >> post);
        }

        // CLOCK_GetPfdFreq(): pllHz * 18 / frac. frac == 0 means the PFD is off.
        private uint PfdHz(long pfdRegister, uint pllHz, int pfd)
        {
            if(anadig == null)
            {
                return 0;
            }
            var frac = (anadig.PeekRaw(pfdRegister) >> (8 * pfd)) & PfdFracMask;
            return frac != 0 ? (uint)((ulong)pllHz * 18u / frac) : 0u;
        }

        private readonly IMXRT1180_Anadig anadig;

        private const int RootSai1 = 65;
        private const int RootSai2 = 66;
        private const int RootSai3 = 67;
        private const int RootSai4 = 68;
        private const int RootAsrc = 70;

        private const uint RootDivMask  = 0xFF;
        private const int  RootMuxShift = 8;
        private const uint RootMuxMask  = 0x3;
        private const uint RootOff      = 1u << 24;   // CONTROL.OFF

        private const uint XtalHz       = 24000000;
        private const int RootBusWakeup = 4;              // kCLOCK_Root_Bus_Wakeup
        private const uint SysPll2_528Hz = XtalHz * 22;   // PLL_SYS2_528_MFI = 22 [oracle]
        private readonly System.Collections.Generic.HashSet<int> deadSourceLogged =
            new System.Collections.Generic.HashSet<int>();
        private readonly System.Collections.Generic.HashSet<int> unmodelledRootLogged =
            new System.Collections.Generic.HashSet<int>();

        private const uint OscRc24M     = 24000000;
        private const uint OscRc400M    = 400000000;
        private const uint SysPll3_480Hz = XtalHz * 20;   // PLL_SYS3_480_MFI = 20

        private const long AnadigSysPll3Pfd      = 0x4030;
        private const long AnadigAudioPllCtrl0   = 0x4280;
        private const long AnadigAudioPllNumer   = 0x42A0;
        private const long AnadigAudioPllDenom   = 0x42B0;
        private const uint AudioPllDivSelectMask = 0x7F;
        private const int  AudioPllPostDivShift  = 25;
        private const uint AudioPllPostDivMask   = 0x7;
        private const uint PfdFracMask           = 0x3F;

        public uint ReadDoubleWord(long offset)
        {
            if(offset + 4 > Size || offset < 0)
            {
                this.Log(LogLevel.Warning, "OOB read @0x{0:X}", offset);
                return 0;
            }

            // LPCG STATUS0.ON mirrors DIRECT.ON so CLOCK_ControlGate's poll
            // completes. [oracle]
            if(offset >= LpcgBase + Status0 && offset < LpcgBase + LpcgCount * LpcgStep
               && ((offset - LpcgBase - Status0) % LpcgStep) == 0)
            {
                var gate = (offset - LpcgBase - Status0) / LpcgStep;
                var direct = regs[(LpcgBase + gate * LpcgStep) / 4];
                return (direct & LpcgOn) != 0 ? LpcgOn : 0u;
            }

            // OBSERVE[n].FREQ_CURRENT: the oracle COMPUTES this. We do not, and
            // we will not fabricate it.
            if(offset == ObsFreqCurrent || offset == ObsFreqCurrent + ObsStep)
            {
                this.Log(LogLevel.Error,
                    "CCM OBSERVE FREQ_CURRENT read @0x{0:X}, but this model does NOT compute clock " +
                    "frequencies. Returning 0 would be a fabricated measurement. Port ccm_obs_hz() " +
                    "from the QEMU oracle if a rung needs this.", offset);
                return 0;
            }

            long aliasBase;
            if(TryAlias(offset, out aliasBase))
            {
                return regs[aliasBase / 4];     // an alias reads its BASE
            }
            return regs[offset / 4];
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size || offset < 0)
            {
                this.Log(LogLevel.Warning, "OOB write @0x{0:X}", offset);
                return;
            }
            long aliasBase;
            if(TryAlias(offset, out aliasBase))
            {
                switch(offset - aliasBase)
                {
                    case 0x4: regs[aliasBase / 4] |= value; break;   // SET
                    case 0x8: regs[aliasBase / 4] &= ~value; break;  // CLR
                    case 0xC: regs[aliasBase / 4] ^= value; break;   // TOG
                }
                return;
            }
            regs[offset / 4] = value;
        }

        // Grouped registers expose RW at +0, SET/CLR/TOG at +4/+8/+0xC. [oracle]
        private static bool TryAlias(long offset, out long baseOffset)
        {
            for(var g = 0; g < Groups.GetLength(0); g++)
            {
                long first = Groups[g, 0], count = Groups[g, 1], step = Groups[g, 2];
                for(var n = 0; n < count; n++)
                {
                    var b = first + n * step;
                    if(offset > b && offset <= b + 0xC)
                    {
                        baseOffset = b;
                        return true;
                    }
                }
            }
            baseOffset = 0;
            return false;
        }

        private readonly uint[] regs;

        private const long RootBase = 0x0000;   // CLOCK_ROOT[74], step 0x80
        private const int RootCount = 74;
        private const long RootStep = 0x80;
        private const long RootAuthen = 0x30;

        private const long ObsFreqCurrent = 0x4440;
        private const long ObsStep = 0x80;

        private const long GprPrivBase = 0x4C00;
        private const int GprPrivCount = 4;
        private const long GprPrivStep = 0x20;
        private const long GprPrivAuthen = 0x10;

        private const long OscPllBase = 0x5000; // OSCPLL[25], step 0x40
        private const int OscPllCount = 25;
        private const long OscPllStep = 0x40;

        private const long LpcgBase = 0x8000;   // LPCG[149], step 0x40
        private const int LpcgCount = 149;
        private const long LpcgStep = 0x40;

        private const long Direct = 0x00;
        private const long Status0 = 0x20;
        private const long Status1 = 0x24;
        private const long Authen = 0x30;
        private const uint LpcgOn = 0x1u;

        private static readonly long[,] Groups = {
            { 0x0000, RootCount, RootStep },       // CLOCK_ROOT[n].CONTROL
            { 0x4400, 2, ObsStep },                // OBSERVE[n].CONTROL
            { 0x4430, 2, ObsStep },                // OBSERVE[n].AUTHEN
            { 0x4800, 16, 0x20 },                  // GPR_SHARED[n]
            { 0x4810, 16, 0x20 },                  // GPR_SHARED[n].AUTHEN
            { 0x4C00, GprPrivCount, GprPrivStep }, // GPR_PRIVATE[n]
            { 0x4C10, GprPrivCount, GprPrivStep }, // GPR_PRIVATE[n].AUTHEN
        };
    }
}
