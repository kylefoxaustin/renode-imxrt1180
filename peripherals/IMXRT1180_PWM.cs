//
// NXP i.MX RT1180 eFlexPWM — enhanced FlexPWM (motor-control PWM).
//
// The RT1180's headline motor-control block. Each PWM module has four submodules;
// each is a 16-bit up-counter running INIT -> VAL1, reloading, driving a
// complementary PWMA/PWMB pair whose edges are set by VAL2..VAL5. Reproduces what
// a field-oriented-control loop depends on:
//
//   - INIT and VAL0..VAL5 are DOUBLE-BUFFERED: writes land in a shadow and commit
//     to the live registers on MCTRL.LDOK (matching PWM_SetPwmLdok), so a control
//     loop updates all six compare values atomically.
//   - MCTRL.RUN[sm] starts/stops each submodule's counter, ticking at the
//     submodule reload rate = modulo / (pwm_clk / prescaler).
//   - Each reload sets STS.RF and, with INTEN.RIE, raises the submodule IRQ — the
//     periodic interrupt that clocks the FOC current loop. STS is W1C.
//   - The PWMA duty cycle is computed from the live compare values and kept in
//     Duty[] (per-mille) as the model's honest observable output.
//
// ⭐ PORTED FROM THE ORACLE: hw/misc/imxrt1180_pwm.c, register-accurate against
// the MIMXRT1189 CMSIS PERI_PWM.h. MEASURED here: the stock SDK pmsm_enc_cm7 image
// touches PWM1 across ~40 distinct 16-bit offsets in 0x42650000-0x42650190.
//
// FIDELITY NOTE, carried over and not quietly dropped: there is no motor/plant
// behind the outputs. The duty cycle is computed and observable, but nothing
// consumes it, so a closed FOC loop will not spin a virtual rotor here yet.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_PWM : IWordPeripheral, IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public IMXRT1180_PWM(IMachine machine)
        {
            this.machine = machine;
            regs = new ushort[Size / 2];
            bufInit = new ushort[NumSubmodules];
            bufVal = new ushort[NumSubmodules, 6];
            duty = new ushort[NumSubmodules];
            timer = new LimitTimer[NumSubmodules];

            var connections = new System.Collections.Generic.Dictionary<int, IGPIO>();
            // 0..3   per-submodule compare/reload IRQ
            // 4..7   per-submodule output trigger (-> XBAR -> ADC)
            // 8..11  per-submodule value-DMA request
            for(var i = 0; i < 3 * NumSubmodules; i++)
            {
                connections[i] = new GPIO();
            }
            Connections = connections;

            for(var sm = 0; sm < NumSubmodules; sm++)
            {
                var idx = sm;
                // Placeholder frequency only; StartSm() sets the real one from the
                // CCM before the timer is ever enabled.
                timer[sm] = new LimitTimer(machine.ClockSource, 1000000, this,
                    $"pwm_sm{sm}", limit: 1, eventEnabled: true, autoUpdate: true);
                timer[sm].LimitReached += () => OnReload(idx);
            }
            Reset();
        }

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);
            Array.Clear(bufInit, 0, bufInit.Length);
            Array.Clear(bufVal, 0, bufVal.Length);
            Array.Clear(duty, 0, duty.Length);

            // ⭐⭐⭐ THE DEAD TIME DOES NOT RESET TO ZERO.
            // DTCNT0/DTCNT1 are the Deadtime Count registers. Silicon resets them to
            // 0x07FF — 2047 counts of dead time. A clear-to-zero says: NO DEAD TIME.
            //
            // Dead time is the interval that keeps the high-side and low-side
            // transistors of an inverter leg from conducting at the same instant.
            // Zero dead time is a SHOOT-THROUGH: a direct short across the DC bus
            // through both transistors of a leg. Firmware that programs duty cycles
            // and trusts the hardware's reset dead time — which is exactly what a
            // reset value is FOR — would run perfectly here and destroy the inverter
            // on a real board.
            //
            // CTRL resets to 0x0400 (FULL): reload at the full cycle. Zero would
            // claim no reload point was selected at all.
            for(var sm = 0; sm < NumSubmodules; sm++)
            {
                SetSm(sm, RegCtrl, 0x0400);     // FULL: full-cycle reload
                SetSm(sm, RegDtcnt0, 0x07FF);   // DEAD TIME -- not zero
                SetSm(sm, RegDtcnt1, 0x07FF);
            }

            for(var sm = 0; sm < NumSubmodules; sm++)
            {
                timer[sm].Enabled = false;
                Connections[IrqBase + sm].Set(false);
                Connections[DmaBase + sm].Set(false);
            }
            startLogged = false;
        }

        public ushort ReadWord(long offset)
        {
            if(offset + 2 > Size)
            {
                this.Log(LogLevel.Warning, "OOB read at 0x{0:X}", offset);
                return 0;
            }

            // Submodule CNT: report the live up-counter position.
            if(offset < NumSubmodules * SmStride && (offset % SmStride) == RegCnt)
            {
                var sm = (int)(offset / SmStride);
                var init = GetSm(sm, RegInit);
                var modulo = Modulo(sm);

                // ⭐ A STOPPED COUNTER IS AT ITS INIT VALUE, NOT ONE PAST IT.
                // At reset INIT = VAL1 = 0, so Modulo() is 1; synthesising a position
                // unconditionally returns init + (1 - 0) = 1, i.e. CNT reads 1 out of
                // reset where silicon reads 0 — on a counter whose whole job is to say
                // where in the PWM period you are. (The oracle hit exactly this, and
                // its reset-value gate could not see it: CNT is 16-bit and the gate
                // kept only 32-bit registers.)
                if(!timer[sm].Enabled || modulo == 0)
                {
                    return init;            // not running: sitting at INIT
                }
                var down = (ushort)timer[sm].Value;
                return (ushort)(init + (modulo - down));
            }
            return regs[offset / 2];
        }

        public void WriteWord(long offset, ushort value)
        {
            if(offset + 2 > Size)
            {
                this.Log(LogLevel.Warning, "OOB write at 0x{0:X}", offset);
                return;
            }

            // ---- Submodule register writes ----
            if(offset < NumSubmodules * SmStride)
            {
                var sm = (int)(offset / SmStride);
                var reg = offset % SmStride;

                if(reg == RegInit)
                {
                    bufInit[sm] = value;                    // buffered until LDOK
                    return;
                }
                for(var i = 0; i < 6; i++)
                {
                    if(reg == ValOffsets[i])
                    {
                        bufVal[sm, i] = value;              // buffered until LDOK
                        // The value-DMA request (asserted at reload) is satisfied the
                        // moment a VALx word is written — the eDMA's own minor-loop
                        // write lowers the line it was serving, one loop per reload.
                        Connections[DmaBase + sm].Set(false);
                        return;
                    }
                }
                if(reg == RegSts)
                {
                    SetSm(sm, RegSts, (ushort)(GetSm(sm, RegSts) & ~value));   // W1C
                    UpdateIrq(sm);
                    return;
                }
                if(reg == RegCnt)
                {
                    return;                                 // CNT is read-only
                }
                regs[offset / 2] = value;
                if(reg == RegInten)
                {
                    UpdateIrq(sm);
                }
                return;
            }

            // ---- Module-level registers ----
            if(offset == RegMctrl)
            {
                var runOld = (ushort)(regs[RegMctrl / 2] & MctrlRun);
                var cldok = (ushort)((value & MctrlCldok) >> 4);
                var ldok = (ushort)(value & MctrlLdok);
                var runNew = (ushort)(value & MctrlRun);

                for(var sm = 0; sm < NumSubmodules; sm++)
                {
                    if((ldok & (1 << sm)) != 0 && (cldok & (1 << sm)) == 0)
                    {
                        Commit(sm);
                        if((runNew & (1 << (sm + MctrlRunShift))) != 0)
                        {
                            StartSm(sm);                    // live update of a running SM
                        }
                    }
                }

                // ⭐ STORE MCTRL WITH *BOTH* LDOK AND CLDOK CLEARED.
                // Both are write-only request bits that read back 0 on silicon.
                // Persisting CLDOK breaks the SDK's read-modify-write commit:
                // fsl/mc_pmsm writes CLDOK(0xF) then `MCTRL = (MCTRL & ~LDOK) | LDOK(0xF)`,
                // and a stored CLDOK comes back in that second RMW so the write carries
                // CLDOK|LDOK together — which the guard above reads as "clear requested,
                // do not commit". The VAL/INIT buffers then never load: period 0, no output.
                regs[RegMctrl / 2] = (ushort)(value & ~(MctrlLdok | MctrlCldok));

                for(var sm = 0; sm < NumSubmodules; sm++)
                {
                    var bit = 1 << (sm + MctrlRunShift);
                    if((runNew & bit) != 0 && (runOld & bit) == 0)
                    {
                        StartSm(sm);
                        if(duty[sm] == 0)
                        {
                            ComputeDuty(sm);
                        }
                        if(!startLogged)
                        {
                            startLogged = true;
                            // ⭐ THIS MESSAGE USED TO ASSERT SOMETHING THIS MODEL CANNOT KNOW.
                            // It read "...but NO motor plant or encoder responds to the outputs",
                            // which was true when it was written and became FALSE the moment a
                            // plant was wired in — and it kept printing, because the eFlexPWM has
                            // no reference to a plant and never could. A peripheral may report
                            // what it owns; a claim about what is attached to it belongs to
                            // whoever does the attaching. (Same class as the defects this project
                            // keeps finding: state that is internally consistent and externally
                            // wrong.) The plant announces itself; this reports the duty.
                            this.Log(LogLevel.Info,
                                "SM{0} running, PWMA duty {1}.{2}% (computed from the live compare " +
                                "values). Whether anything consumes it depends on what is wired to " +
                                "the outputs.", sm, duty[sm] / 10, duty[sm] % 10);
                        }
                    }
                    else if((runNew & bit) == 0 && (runOld & bit) != 0)
                    {
                        timer[sm].Enabled = false;
                    }
                }
                return;
            }

            if(offset == RegFsts)
            {
                // ⭐ FFLAG IS W1C AND HARDWARE-SET — A PLAIN STORE FABRICATES A FAULT.
                // FFLAG[3:0] are W1C fault flags set by hardware on a fault-input edge;
                // FFPIN[11:8] reflect the live filtered fault-pin state. No fault inputs
                // are routed here, so both must stay clear — but the driver CLEARS FFLAG
                // by writing 1s (`FSTS = (FSTS & ~FFLAG) | FFLAG(0xF)`), and a plain
                // store latches those 1s, which mcdrv_pwm3ph_pwma's FltGet then reads
                // back as a spurious OVER-CURRENT.
                var old = regs[offset / 2];
                var fflag = (ushort)(old & FstsFflag & ~value);      // W1C; HW never sets it
                regs[offset / 2] = (ushort)((value & ~(FstsFflag | FstsFfpin)) | fflag);
                return;
            }

            regs[offset / 2] = value;
        }

        public uint ReadDoubleWord(long offset)
        {
            return (uint)(ReadWord(offset) | (ReadWord(offset + 2) << 16));
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            WriteWord(offset, (ushort)(value & 0xFFFF));
            WriteWord(offset + 2, (ushort)(value >> 16));
        }

        // ---- Plant interface ----
        public ushort GetDuty(int sm) => (sm >= 0 && sm < NumSubmodules) ? duty[sm] : (ushort)0;
        public bool IsRunning(int sm) =>
            sm >= 0 && sm < NumSubmodules && (regs[RegMctrl / 2] & (1 << (sm + MctrlRunShift))) != 0;

        public long Size => 0x200;
        public System.Collections.Generic.IReadOnlyDictionary<int, IGPIO> Connections { get; }

        private void OnReload(int sm)
        {
            SetSm(sm, RegSts, (ushort)(GetSm(sm, RegSts) | StsRf));     // reload flag
            ComputeDuty(sm);
            UpdateIrq(sm);

            // Value-DMA request: the reload IS the trigger (there is no fill level),
            // so assert a real LEVEL here and let a VALx write lower it. A pulse would
            // be gone before the DMA ran.
            if((GetSm(sm, RegDmaen) & DmaenValde) != 0)
            {
                Connections[DmaBase + sm].Set(true);
            }

            // Output trigger -> XBAR -> ADC, one pulse per PWM period. The exact
            // intra-period compare instant is approximated by the reload boundary.
            if((GetSm(sm, RegTctrl) & TctrlOutTrigEn) != 0)
            {
                Connections[TrigBase + sm].Set(true);
                Connections[TrigBase + sm].Set(false);
            }
        }

        private ushort Modulo(int sm)
        {
            var init = GetSm(sm, RegInit);
            var val1 = GetSm(sm, RegVal1);
            return (ushort)((ushort)(val1 - init) + 1);      // period in counter ticks
        }

        private void ComputeDuty(int sm)
        {
            var modulo = Modulo(sm);
            var on = (ushort)(GetSm(sm, RegVal3) - GetSm(sm, RegVal2));
            duty[sm] = modulo != 0 ? (ushort)((uint)on * 1000u / modulo) : (ushort)0;
        }

        private void Commit(int sm)
        {
            SetSm(sm, RegInit, bufInit[sm]);
            for(var i = 0; i < 6; i++)
            {
                SetSm(sm, ValOffsets[i], bufVal[sm, i]);
            }
            ComputeDuty(sm);
        }

        private void UpdateIrq(int sm)
        {
            var sts = GetSm(sm, RegSts);
            var inten = GetSm(sm, RegInten);
            var active = (((sts & StsRf) != 0) && ((inten & IntenRie) != 0))
                      || ((sts & inten & StsCmpf) != 0);
            Connections[IrqBase + sm].Set(active);
        }

        private void StartSm(int sm)
        {
            var modulo = Modulo(sm);
            var ctrl = GetSm(sm, RegCtrl);
            var prescale = 1u << ((ctrl & CtrlPrscMask) >> CtrlPrscShift);

            if(modulo == 0)
            {
                return;
            }

            // ⭐⭐ THE CLOCK IS NOT A CONSTANT — READ IT FROM THE CCM AT THE POINT
            // OF USE. This model originally carried `clockFrequency = 200000000` as
            // a constructor default, which is EXACTLY the anti-pattern the oracle
            // documents having removed from their own PWM: "This block used to hold
            // a hardcoded default behind `if (!clk) clk = DEFAULT;`, which made the
            // missing wiring invisible and ran the timer at the wrong rate."
            // I ported the model and not the lesson.
            //
            // MEASURED by their tests/imxrt1180-pwm, which SWEEPS THE ROOT DIVIDER
            // precisely to catch a model that ignores the clock tree (such a model
            // reports the same period at every divider):
            //   prescale 64, modulo 1000: measured 76800 cycles, expected 145454
            //   prescale 64, modulo  500: measured 38399 cycles, expected  72727
            // The guest programs CLOCK_ROOT[Bus_Wakeup] = SYS_PLL2 (528 MHz) / div;
            // a fixed 200 MHz cannot follow it.
            //
            // NO CLOCK MEANS NO CARRIER. If the CCM reports 0 the submodule does not
            // run and says so -- a plausible fallback here is invisible to a golden
            // that checks phase-current AMPLITUDE and fatal to one that checks PERIOD.
            var hz = CCM != null ? CCM.GetClockRootHz(ClkRoot) : 0;
            if(hz == 0)
            {
                timer[sm].Enabled = false;
                if(!noClockLogged)
                {
                    noClockLogged = true;
                    this.Log(LogLevel.Warning,
                        "SM{0} was started but CLOCK_ROOT[{1}] reports 0 Hz (gated, unmodelled, or no " +
                        "CCM wired). The submodule does NOT run: a PWM carrier invented from a default " +
                        "would be wrong at every root divider and silent about it.", sm, ClkRoot);
                }
                return;
            }

            timer[sm].Enabled = false;
            timer[sm].Frequency = (ulong)hz / prescale;
            timer[sm].Limit = modulo;
            timer[sm].Enabled = true;
        }

        private ushort GetSm(int sm, long off) => regs[(sm * SmStride + off) / 2];
        private void SetSm(int sm, long off, ushort v) => regs[(sm * SmStride + off) / 2] = v;

        // Wired from the platform, as the oracle's SoC does
        // (object_property_set_link "ccm" + "clk-root" = CLKROOT_BUS_WAKEUP).
        public IMXRT1180_CCM CCM { get; set; }
        public int ClkRoot { get; set; } = 4;    // kCLOCK_Root_Bus_Wakeup

        private readonly IMachine machine;
        private bool noClockLogged;
        private readonly ushort[] regs;
        private readonly ushort[] bufInit;
        private readonly ushort[,] bufVal;
        private readonly ushort[] duty;
        private readonly LimitTimer[] timer;
        private bool startLogged;

        private const int NumSubmodules = 4;
        private const long SmStride = 0x60;
        private const int IrqBase = 0;
        private const int TrigBase = 4;
        private const int DmaBase = 8;

        // Submodule register offsets (within the 0x60 stride) [oracle/PERI_PWM.h]
        private const long RegCnt = 0x00;       // Counter (RO)
        private const long RegInit = 0x02;      // Initial count (buffered)
        private const long RegCtrl2 = 0x04;
        private const long RegCtrl = 0x06;
        private const long RegVal0 = 0x0A;
        private const long RegVal1 = 0x0E;
        private const long RegVal2 = 0x12;
        private const long RegVal3 = 0x16;
        private const long RegVal4 = 0x1A;
        private const long RegVal5 = 0x1E;
        private const long RegSts = 0x24;
        private const long RegInten = 0x26;
        private const long RegDmaen = 0x28;
        private const long RegTctrl = 0x2A;
        private const long RegDtcnt0 = 0x30;
        private const long RegDtcnt1 = 0x32;

        private static readonly long[] ValOffsets = { RegVal0, RegVal1, RegVal2, RegVal3, RegVal4, RegVal5 };

        // Module-level
        private const long RegOuten = 0x180;
        private const long RegMctrl = 0x188;
        private const long RegFsts = 0x18E;

        private const ushort DmaenValde = 0x0200;
        private const ushort TctrlOutTrigEn = 0x003F;
        private const ushort FstsFflag = 0x000F;
        private const ushort FstsFfpin = 0x0F00;
        private const ushort CtrlLdmod = 0x0004;
        private const ushort CtrlPrscMask = 0x0070;
        private const int CtrlPrscShift = 4;
        private const ushort StsCmpf = 0x003F;
        private const ushort StsRf = 0x1000;
        private const ushort IntenCmpie = 0x003F;
        private const ushort IntenRie = 0x1000;
        private const ushort MctrlLdok = 0x000F;
        private const ushort MctrlCldok = 0x00F0;
        private const ushort MctrlRun = 0x0F00;
        private const int MctrlRunShift = 8;
    }
}
