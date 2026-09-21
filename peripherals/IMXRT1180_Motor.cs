//
// i.MX RT1180 virtual-motor plant (behavioural, NOT silicon).
//
// Closes the motor-control loop in emulation: reads the eFlexPWM duty cycles, runs
// a permanent-magnet-synchronous-motor model (Clarke/Park -> torque -> integrate
// velocity and angle), then drives the EQDC position counter and injects the
// resulting phase currents into the LPADC channels. With this in place an FOC loop
// running on the guest actually spins a (virtual) rotor and senses it back.
//
// ⭐ THIS IS NOT A DEVICE ON THE SoC. It is a simulation object wired to the PWM,
// EQDC and ADC models, and it is registered in the platform at a dummy address so
// Renode can instantiate it. A running emulator with no plant leaves those
// peripherals in their honest "no motor attached" state, and each of them says so.
//
// ⭐ PORTED FROM THE ORACLE: hw/misc/imxrt1180_motor.c. Every constant below comes
// from that model, which carries value-proven goldens on the QEMU side
// (tests/imxrt1180-motor, -motor-load, -motor-sat, -motor-thermal, -adc-ab).
// The scaling constants in particular are NOT derivable from the RM — they are
// inverses of what the mc_pmsm DRIVER does to the raw code, and getting them
// "reasonably" wrong produces a loop that runs and never spins (see M_CUR_FS and
// the DC-bus comment below, both of which cost the oracle a debugging cycle).
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Analog;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_Motor : IPeripheral
    {
        public IMXRT1180_Motor(IMachine machine, uint rateHz = 50000,
                               uint loadMilliNm = 0, uint loadFanMicroNms = 0,
                               uint initMilliRadS = 0, uint satIsatMilliA = 0,
                               bool thermal = false, uint thermRthMilliCW = 0,
                               uint thermTauMs = 0, uint thermAmbC = 25)
        {
            // ⭐ The winding-thermal model is OFF by default, exactly as the oracle
            // ships it (-global imxrt1180-motor.thermal=1 turns it on there). With
            // it off the plant runs at the cold datasheet Rs and every existing
            // golden holds; turning it on by default would silently move results
            // that other tests are pinned to.
            this.thermal = thermal;
            this.thermRthMilliCW = thermRthMilliCW;
            this.thermTauMs = thermTauMs;
            this.thermAmbC = thermAmbC;
            this.machine = machine;
            this.rateHz = rateHz;
            this.loadMilliNm = loadMilliNm;
            this.loadFanMicroNms = loadFanMicroNms;
            this.initMilliRadS = initMilliRadS;
            this.satIsatMilliA = satIsatMilliA;

            timer = new Antmicro.Renode.Peripherals.Timers.LimitTimer(
                machine.ClockSource, rateHz, this, "motor", limit: 1,
                eventEnabled: true, autoUpdate: true);
            timer.LimitReached += Step;
            Reset();
            // The plant announces itself, because it is the object that knows a motor
            // is attached — the PWM/EQDC/ADC models cannot know and must not claim it.
            this.Log(LogLevel.Info,
                "Virtual PMSM plant attached: {0} pole pairs, Rs {1} ohm, Ld {2} uH, Lq {3} uH, " +
                "Kt {4} N*m/A, J {5} kg*m^2, {6} V bus, {7}-line encoder ({8} cts/rev), stepping at {9} Hz. " +
                "This is a BEHAVIOURAL model, not silicon.",
                PolePairs, Rs, Ld * 1e6, Lq * 1e6, Kt, J, VBus, EncPulses, Cpr, rateHz);
        }

        // Wiring, set from the .repl.
        public IMXRT1180_PWM PWM { get; set; }
        public IMXRT1180_EQDC EQDC { get; set; }
        public IMXRT1180_LPADC AdcA { get; set; }       // phase A/B + DC bus
        public IMXRT1180_LPADC AdcC { get; set; }       // phase C
        // ⭐ THE QD-TIMER CLOCK IS READ FROM THE CCM AT THE POINT OF USE, NOT
        // CONFIGURED HERE. It is CLOCK_ROOT[Bus_Wakeup] >> EQDC.FILT[PRSC], and the
        // mc_pmsm driver derived its speed constant from that exact number — so a
        // guessed or hardcoded value silently rescales EVERY speed reading the FOC
        // sees, with nothing in the console to say so. The guest rewrites the roots
        // in BOARD_InitBootClocks() and some examples re-mux afterwards, which is
        // why this cannot be sampled once at construction.
        // If the CCM reports 0 (root gated or unmodelled) the plant drives POSITION
        // but NOT speed, and says so once — rather than presenting a speed derived
        // from a clock nothing is running at.
        public IMXRT1180_CCM CCM { get; set; }

        public void Reset()
        {
            theta = 0.0;
            omega = initMilliRadS / 1000.0;     // 0 normally; !=0 seeds a coast-down
            id = 0.0;
            iq = 0.0;
            tempC = thermAmbC;
            timer.Enabled = true;
        }

        private void Step()
        {
            var dt = 1.0 / rateHz;

            var run = PWM != null && (PWM.IsRunning(0) || PWM.IsRunning(1) || PWM.IsRunning(2));

            // Stay dormant until a motor is actually driven: while the PWM is idle
            // and the rotor at rest, do not touch EQDC/ADC at all, so peripherals a
            // non-motor workload uses are left exactly as firmware set them. Once
            // driven, the plant keeps updating while the rotor coasts down.
            if(!run && Math.Abs(omega) < 1e-4 && Math.Abs(id) < 1e-3 && Math.Abs(iq) < 1e-3)
            {
                return;
            }

            var thetaE = PolePairs * theta;
            var c = Math.Cos(thetaE);
            var sn = Math.Sin(thetaE);
            double idNow, iqNow, te;

            if(run)
            {
                // Phase voltages from the PWM duty (centred: 0.5 duty = 0 V).
                var va = (PWM.GetDuty(0) / 1000.0 - 0.5) * VBus;
                var vb = (PWM.GetDuty(1) / 1000.0 - 0.5) * VBus;
                var vc = (PWM.GetDuty(2) / 1000.0 - 0.5) * VBus;

                // Amplitude-invariant Clarke, then Park into the rotor (dq) frame.
                var valpha = (2.0 * va - vb - vc) / 3.0;
                var vbeta = (vb - vc) / (2.0 * Sqrt3Over2);
                var vd = valpha * c + vbeta * sn;
                var vq = -valpha * sn + vbeta * c;

                var rs = Rs;
                if(thermal)
                {
                    rs = Rs * (1.0 + AlphaCu * (tempC - thermAmbC));
                }

                // Magnetic saturation: incremental inductance falls as current rises
                // (the iron saturates), Ld_eff = Ld0/(1 + |id|/i_sat). Off by default.
                var ld = Ld;
                var lq = Lq;
                if(satIsatMilliA != 0)
                {
                    var isat = satIsatMilliA / 1000.0;
                    ld = Ld / (1.0 + Math.Abs(id) / isat);
                    lq = Lq / (1.0 + Math.Abs(iq) / isat);
                }

                // dq stator-current dynamics (cross-coupling + PM back-EMF):
                //   L_d did/dt = v_d - R i_d + w_e L_q i_q
                //   L_q diq/dt = v_q - R i_q - w_e L_d i_d - w_e psi_m
                var omegaE = PolePairs * omega;
                var did = (vd - rs * id + omegaE * lq * iq) / ld;
                var diq = (vq - rs * iq - omegaE * ld * id - omegaE * Psi) / lq;
                id += did * dt;
                iq += diq * dt;
                idNow = id;
                iqNow = iq;

                if(thermal && thermTauMs > 0)
                {
                    var rth = thermRthMilliCW / 1000.0;
                    var tau = thermTauMs / 1000.0;
                    var pLoss = 1.5 * (idNow * idNow + iqNow * iqNow) * rs;
                    var dtr = tempC - thermAmbC;
                    tempC += (pLoss * rth - dtr) / tau * dt;
                }

                // Electromagnetic torque (magnet + reluctance/saliency).
                te = 1.5 * PolePairs * (Psi * iqNow + (Ld - Lq) * idNow * iqNow);
            }
            else
            {
                // ⭐ PWM IDLE MEANS THE INVERTER IS TRISTATED, SO THE STATOR IS
                // OPEN-CIRCUIT: no phase current can flow and there is no
                // electromagnetic torque. The rotor coasts FREELY under the
                // mechanical load alone — a tristated inverter free-wheels, it does
                // NOT dynamically brake. Zeroing the currents here is what makes the
                // coast-down a clean mechanical problem with a closed form.
                id = 0.0;
                iq = 0.0;
                idNow = 0.0;
                iqNow = 0.0;
                te = 0.0;
            }

            // Mechanics. Load torque = constant term + a speed-SQUARED (fan / pump /
            // windage) term k*w*|w| — the physical shape of a rotating load, always
            // opposing motion. With the drive removed the total coast-down angle has
            // the closed form theta = (J/k) ln(1 + k*w0/B).
            var tLoad = loadMilliNm / 1000.0 + (loadFanMicroNms / 1.0e6) * omega * Math.Abs(omega);
            omega += (te - B * omega - tLoad) / J * dt;
            theta += omega * dt;

            // Inverse Park/Clarke -> phase currents for the ADC.
            var ialpha = idNow * c - iqNow * sn;
            var ibeta = idNow * sn + iqNow * c;
            var ia = ialpha;
            var ib = -0.5 * ialpha + Sqrt3Over2 * ibeta;
            var ic = -0.5 * ialpha - Sqrt3Over2 * ibeta;

            if(AdcA != null)
            {
                var codeIa = CurrentToCode(ia);
                var codeIb = CurrentToCode(ib);
                var codeUd = VoltageToCode(VBus);       // the bus the plant applies
                AdcA.SetChannelInput(ChPhaseA, SideA, codeIa);
                AdcA.SetChannelInput(ChPhaseB, SideA, codeIb);
                AdcA.SetChannelInput(ChUdcb, SideA, codeUd);
                // The stock mc_pmsm cm7 demo reads Ia/Ib as the A/B sides of ONE
                // channel (ADC1 CMD1 = DualSingleEndBothSide on ch5: A5=Ia -> FIFO0,
                // B5=Ib -> FIFO1) and UDCB on B4. Drive those B-side muxes too.
                AdcA.SetChannelInput(ChPhaseA, SideB, codeIb);
                AdcA.SetChannelInput(ChUdcb, SideB, codeUd);
            }
            if(AdcC != null)
            {
                // mc_pmsm: ADC2 CMD1 = DualSingleEndBothSide on ch2, A2 = Ic -> FIFO0.
                AdcC.SetChannelInput(ChPhaseC, SideA, CurrentToCode(ic));
            }

            if(EQDC != null)
            {
                var rounds = theta / TwoPi;
                var total = (long)Math.Round(rounds * Cpr);
                var rev = total / Cpr;
                var pos = total % Cpr;
                if(pos < 0)                         // keep pos in [0, CPR)
                {
                    pos += Cpr;
                    rev -= 1;
                }
                EQDC.SetPosition((uint)pos, (ushort)rev);

                // Present the EQDC's HARDWARE speed measurement. The mc_pmsm qdc2
                // driver reads speed as POSDH / POSDPERH = counts-per-QD-clock, then
                // scales by (2*pi*QDTimerFreq)/(4*pulses) — so to report the shaft's
                // real velocity we set POSD = vel_counts_per_sec / QDTimerFreq *
                // POSDPER for a fixed period window.
                var rootHz = CCM != null ? CCM.GetClockRootHz(ClkRootBusWakeup) : 0;
                var qdHz = rootHz >> EQDC.FiltPrescaler;
                if(qdHz == 0 && !noQdClockLogged)
                {
                    noQdClockLogged = true;
                    this.Log(LogLevel.Warning,
                        "EQDC QD-timer clock is 0 (CLOCK_ROOT[Bus_Wakeup] gated, unmodelled, or no " +
                        "CCM wired). Driving rotor POSITION but NOT the hardware speed measurement: " +
                        "a speed presented against a clock nothing runs at is worse than none.");
                }
                if(qdHz > 0)
                {
                    var velCntS = omega * Cpr / TwoPi;      // signed mech counts/s
                    const ushort posdper = 2048;            // measurement window
                    var posd = velCntS / qdHz * posdper;
                    posd = Math.Max(-32768.0, Math.Min(32767.0, posd));
                    // LASTEDGE = QD clocks between single-count edges (low-speed
                    // path); 0xFFFF = no edge seen (shaft stopped).
                    ushort lastedge = 0xFFFF;
                    var avel = Math.Abs(velCntS);
                    if(avel > 1.0)
                    {
                        var le = qdHz / avel;
                        lastedge = le < 65535.0 ? (ushort)le : (ushort)0xFFFF;
                    }
                    EQDC.SetSpeed((short)Math.Round(posd), posdper, lastedge);
                }
            }
        }

        private static ushort CurrentToCode(double i)
        {
            var code = AdcMid + i * CurFs;
            return (ushort)Math.Max(0.0, Math.Min(65535.0, code));
        }

        private static ushort VoltageToCode(double v)
        {
            // ⭐⭐ THE DC-BUS CHANNEL MUST BE DRIVEN, AND THE SCALING IS THE DRIVER'S
            // INVERSE, NOT A 16-BIT FRACTION.
            // mcdrv_adc_imxrt118x.c treats the LPADC result as a Q15 frac16 and
            // applies the EVK's VIN_HW/VIN_MAX = 11/12 board compensation before
            // scaling: U_dcb = (raw * 12/11 / 32768) * 60.8 V. So the code the
            // converter must PRESENT for a bus voltage v is raw = v/60.8 * 32768 *
            // 11/12 — NOT v/60.8 * 0xFFFF, which is 2.18x too high for that decode
            // and reads 24 V as ~52 V, tripping the stock FOC's 30 V over-voltage
            // lockout so the demo never leaves AppStop.
            //
            // And left at the ADC's neutral mid-scale placeholder this channel reads
            // 0x8000 -> 30.4 V: a *plausible* bus voltage, and just over the demo's
            // 30.0 V trip. That is the dangerous kind of fake — an FOC loop
            // normalises its duty cycles by U_DCB and runs its under/over-voltage
            // protection off it, so a fabricated value has the loop regulating
            // against, and protecting against, a number nothing measured.
            var code = (v / UdcbFs) * 32768.0 * UdcbComp;
            return (ushort)Math.Max(0.0, Math.Min(65535.0, code));
        }

        // Observables for a value harness (no console string exists for this row).
        public double Theta => theta;
        public double Omega => omega;
        public double RpmMechanical => omega * 60.0 / TwoPi;
        public double Id => id;
        public double Iq => iq;
        public double WindingTempC => tempC;

        private readonly IMachine machine;
        private readonly Antmicro.Renode.Peripherals.Timers.LimitTimer timer;
        private readonly uint rateHz;
        private readonly uint loadMilliNm;
        private readonly uint loadFanMicroNms;
        private readonly uint initMilliRadS;
        private readonly uint satIsatMilliA;

        private double theta, omega, id, iq, tempC;
        private bool noQdClockLogged;

        // Optional winding-thermal model, off by default so the cold-Rs goldens hold.
        private readonly bool thermal;
        private readonly uint thermRthMilliCW;
        private readonly uint thermTauMs;
        private readonly uint thermAmbC;

        // ---- M1 motor parameters (MCUXpresso mc_pmsm m1_pmsm_appconfig.h / MCAT) ----
        private const double PolePairs = 4;
        private const double Rs = 0.54;             // phase resistance (ohm)
        private const double Ld = 0.0003356;        // d-axis inductance (H)
        private const double Lq = 0.000218;         // q-axis inductance (H)
        private const double Kt = 0.05477461;       // torque constant (N*m/A)
        private const double Psi = Kt / (1.5 * PolePairs);   // PM flux linkage (Wb)
        private const double J = 0.00001;           // rotor inertia (kg*m^2)
        private const double B = 0.0001;            // viscous damping (N*m*s)
        private const double VBus = 24.0;           // DC-bus voltage (V)
        private const double IMax = 8.25;           // rated peak current (A)
        private const double AlphaCu = 0.00393;     // copper tempco (per degC)

        // ⭐ ENCODER CPR IS LOAD-BEARING. The mc_pmsm encoder driver configures the
        // EQDC modulus LMOD = 4*M1_POSPE_ENC_PULSES - 1 and scales ALL its
        // position/speed gains for 4x the line count — for the EVK's 2000-line
        // quadrature encoder that is 8000 cts/rev. A mismatched CPR makes the FOC
        // read the rotor angle at the wrong rate, so its dq frame diverges from the
        // plant's (torque current lands in the d-axis) and the closed loop CANNOT
        // SUSTAIN A SPIN — with nothing in the console to say why.
        private const int EncPulses = 2000;
        private const int Cpr = 4 * EncPulses;      // 8000 quadrature counts/rev
        private const double AdcMid = 0x8000;

        // ⭐ ADC code span per amp is the INVERSE OF THE DRIVER'S DECODE, not a
        // full-scale ratio. mc_pmsm reads a phase current as
        //   I = ((raw*12/11 - offset) << 1) / 32768 * M1_I_MAX
        // so the code for a current i is raw = MID + i * 32768/(2 * (12/11) * I_MAX)
        // = 1820 cts/A — NOT 0x7000/I_MAX = 3475. The "obvious" span reads ~1.9x
        // high through the decode and trips the over-current fault at startup.
        private const double CurFs = 32768.0 / (2.0 * (12.0 / 11.0) * IMax);

        private const double UdcbFs = 60.8;             // M1_U_DCB_MAX (V)
        private const double UdcbComp = 11.0 / 12.0;    // VIN_HW/VIN_MAX

        private const double TwoPi = 2.0 * Math.PI;
        private const double Sqrt3Over2 = 0.8660254037844386;

        // Channels the plant drives (mc_pmsm mapping).
        private const int ChPhaseA = 5;     // ADC1
        private const int ChPhaseB = 6;     // ADC1
        private const int ChPhaseC = 2;     // ADC2
        private const int ChUdcb = 4;       // ADC1 (M1_ADC1_UDCB)
        private const int SideA = 0;
        private const int SideB = 1;

        // kCLOCK_Root_Bus_Wakeup (fsl_clock.h) -- the EQDC QD-timer clock root.
        private const int ClkRootBusWakeup = 4;
    }
}
