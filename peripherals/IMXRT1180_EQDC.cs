//
// NXP i.MX RT1180 EQDC — Enhanced Quadrature Decoder.
//
// The rotor-position sensor of the motor-control subsystem. Reproduces the two
// behaviours the FOC encoder driver (fsl_eqdc) depends on:
//
//   - CTRL.LDOK is a SELF-CLEARING software load: firmware sets it and spins on
//     `while (CTRL & LDOK)`. The model loads the shadow and, if CTRL.SWIP is
//     set, preloads the position counter from UINIT:LINIT, then clears LDOK.
//   - COHERENT 32-bit position read: reading UPOS atomically snapshots LPOS,
//     REV, POSD (and the period/last-edge registers) into their hold registers,
//     so `EQDC_GetPosition()` — read UPOS, then LPOSH — sees a consistent pair.
//     Reading POSD likewise refreshes POSDH.
//
// ⭐ PORTED FROM THE ORACLE: hw/misc/imxrt1180_eqdc.c, register-accurate against
// the MIMXRT1189 DFP PERI_EQDC.h. MEASURED here: the stock SDK pmsm_enc_cm7 image
// touches EQDC1 at 0x42710000/02/04/26 — 16-bit accesses, matching the layout.
//
// FIDELITY NOTE, carried over rather than dropped: no quadrature encoder is wired
// to the inputs and no motor plant drives them, so the position/revolution
// counters DO NOT ADVANCE on their own — they read back exactly what firmware
// (or a future plant) writes, and no index/compare/watchdog interrupt is
// generated. That is the honest "encoder present, shaft not turning" state, and
// the model says so once, loudly, the first time firmware enables it.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_EQDC : IWordPeripheral, IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public IMXRT1180_EQDC()
        {
            regs = new ushort[Size / 2];
            var connections = new System.Collections.Generic.Dictionary<int, IGPIO> { { 0, new GPIO() } };
            Connections = connections;
            Reset();
        }

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);

            // ⭐⭐⭐ ZERO IS NOT "NO SPEED" HERE — IT IS INFINITE SPEED.
            // POSDPER is the Position Difference PERIOD counter: the number of
            // clocks between successive encoder edges. A speed observer DIVIDES BY
            // IT. Silicon resets it to 0xFFFF — the maximum period, i.e. "no edge
            // seen, the shaft is not turning". A clear-to-zero says the exact
            // opposite: ZERO CLOCKS BETWEEN EDGES.
            //
            //   A FOC SPEED LOOP READING A ZERO RESET VALUE COMPUTES A DIVIDE-BY-ZERO,
            //   OR AN INFINITE ROTOR VELOCITY, BEFORE THE MOTOR HAS MOVED AT ALL.
            //
            // Same for the buffer/hold registers and LASTEDGE (timestamp of the
            // last edge; 0xFFFF = "none yet"). Offsets from PERI_EQDC.h, values
            // from the RM's reset column, both via the oracle.
            // All 16-bit — which on the oracle's side is exactly why a 32-bit-only
            // reset-value gate had never once looked at them.
            regs[0x06 / 2] = 0xFFFF;    // LASTEDGE   -- no edge seen yet
            regs[0x08 / 2] = 0xFFFF;    // POSDPER    -- maximum period
            regs[0x0A / 2] = 0xFFFF;    // POSDPERBFR
            regs[0x18 / 2] = 0xFFFF;    // LASTEDGEH
            regs[0x1A / 2] = 0xFFFF;    // POSDPERH
            regs[0x28 / 2] = 0x8000;    // UCOMP0
            regs[0x50 / 2] = 0x0001;    // UVERID
            regs[0x52 / 2] = 0x0001;    // LVERID

            noEncoderLogged = false;
            Connections[0].Set(false);
        }

        public ushort ReadWord(long offset)
        {
            if(offset + 2 > Size)
            {
                this.Log(LogLevel.Warning, "OOB read at 0x{0:X}", offset);
                return 0;
            }

            switch(offset)
            {
            case RegUpos:
                // Coherent read: latch the lower/rev/diff halves at this instant.
                Snapshot();
                return regs[RegUpos / 2];
            case RegPosd:
                // Reading POSD latches the difference, its period and the last-edge
                // time into the hold registers the speed driver then reads.
                regs[RegPosdh / 2] = regs[RegPosd / 2];
                regs[RegPosdperh / 2] = regs[RegPosdper / 2];
                regs[RegLastedgeh / 2] = regs[RegLastedge / 2];
                return regs[RegPosd / 2];
            default:
                return regs[offset / 2];
            }
        }

        public void WriteWord(long offset, ushort value)
        {
            if(offset + 2 > Size)
            {
                this.Log(LogLevel.Warning, "OOB write at 0x{0:X}", offset);
                return;
            }

            switch(offset)
            {
            case RegCtrl:
                // LDOK triggers a software load of the modulus/compare/init shadow
                // and SELF-CLEARS when done. With SWIP set, the position counter is
                // preloaded from UINIT:LINIT. Store CTRL with LDOK cleared, or the
                // driver's `while (CTRL & LDOK)` never returns.
                if((value & CtrlLdok) != 0 && (value & CtrlSwip) != 0)
                {
                    regs[RegUpos / 2] = regs[RegUinit / 2];
                    regs[RegLpos / 2] = regs[RegLinit / 2];
                }
                regs[RegCtrl / 2] = (ushort)(value & ~CtrlLdok);
                if((value & CtrlSwip) != 0 && !noEncoderLogged)
                {
                    noEncoderLogged = true;
                    this.Log(LogLevel.Warning,
                        "Enabled, but NO quadrature encoder or motor plant drives the inputs — " +
                        "position stays exactly as written and never advances. Any control loop " +
                        "reading it back is running on its own last write, not on a shaft angle.");
                }
                return;
            case RegUverid:
            case RegLverid:
                return;                     // version registers are read-only
            default:
                regs[offset / 2] = value;
                return;
            }
        }

        // The driver uses 16-bit accesses; accept 32-bit (two registers) as the
        // oracle's MemoryRegionOps does, rather than faulting.
        public uint ReadDoubleWord(long offset)
        {
            return (uint)(ReadWord(offset) | (ReadWord(offset + 2) << 16));
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            WriteWord(offset, (ushort)(value & 0xFFFF));
            WriteWord(offset + 2, (ushort)(value >> 16));
        }

        // ---- Plant interface (a virtual motor drives these) ----

        public void SetPosition(uint pos, ushort rev)
        {
            regs[RegLpos / 2] = (ushort)(pos & 0xFFFF);
            regs[RegUpos / 2] = (ushort)(pos >> 16);
            regs[RegRev / 2] = rev;
            // The mc_pmsm qdc2 encoder driver reads POSITION from the HOLD registers
            // (UPOSH:LPOSH), not the live counters, and its fast loop does so WITHOUT
            // a preceding snapshot read — on silicon a hardware position-hold trigger
            // synced to the PWM refreshes them every control cycle. Keep the hold
            // equal to the live position so that read always sees the current shaft
            // angle; without this it reads the reset 0 and the FOC has no feedback.
            regs[RegLposh / 2] = regs[RegLpos / 2];
            regs[RegUposh / 2] = regs[RegUpos / 2];
            regs[RegRevh / 2] = regs[RegRev / 2];
        }

        // Hardware speed measurement. The driver computes rotor speed from POSDH
        // (the position CHANGE) over POSDPERH (the QD-timer clocks that change took);
        // LASTEDGE (clocks since the last encoder edge) drives the low-speed estimate.
        // Signed POSD carries the direction.
        public void SetSpeed(short posd, ushort posdper, ushort lastedge)
        {
            regs[RegPosd / 2] = (ushort)posd;
            regs[RegPosdper / 2] = posdper;
            regs[RegLastedge / 2] = lastedge;
        }

        // FILT[PRSC] (bits 14:12): QD-timer clock = bus clock / 2^PRSC.
        public int FiltPrescaler => (regs[RegFilt / 2] >> 12) & 0x7;

        // ---- Observability affordance for the value harness ----
        // ⚠ THIS IS NOT DEVICE BEHAVIOUR. It reads no register the guest can see
        // differently and changes nothing; it exists because the cm7 FOC row has no
        // console oracle and its proof has to come from the rotor state, and because
        // Renode's console monitor -- the only sink that prints `sysbus ReadWord`
        // results -- crashes several minutes into a long run with stdin redirected
        // (ConsoleIOSource.HandleInput, SemaphoreFullException, observed twice).
        // Running with `-P -1` avoids that path but discards command output, so the
        // state has to reach the LOG instead. Deliberately reports the HOLD
        // registers, which is what the control loop reads, and takes NO snapshot --
        // a probe that perturbs the thing it measures is not a probe.
        public void LogState()
        {
            var pos = ((uint)regs[RegUposh / 2] << 16) | regs[RegLposh / 2];
            var posd = (short)regs[RegPosdh / 2];
            this.Log(LogLevel.Info,
                "EQDCSTATE revh={0} pos={1} total={2} posdh={3} posdperh={4} lastedgeh={5}",
                regs[RegRevh / 2], pos, (long)regs[RegRevh / 2] * 8000 + pos,
                posd, regs[RegPosdperh / 2], regs[RegLastedgeh / 2]);
        }

        public long Size => 0x100;
        public System.Collections.Generic.IReadOnlyDictionary<int, IGPIO> Connections { get; }

        private void Snapshot()
        {
            regs[RegUposh / 2] = regs[RegUpos / 2];
            regs[RegLposh / 2] = regs[RegLpos / 2];
            regs[RegRevh / 2] = regs[RegRev / 2];
            regs[RegPosdh / 2] = regs[RegPosd / 2];
            regs[RegPosdperh / 2] = regs[RegPosdper / 2];
            regs[RegLastedgeh / 2] = regs[RegLastedge / 2];
        }

        private readonly ushort[] regs;
        private bool noEncoderLogged;

        // Register offsets (all 16-bit) [oracle: hw/misc/imxrt1180_eqdc.c]
        private const long RegCtrl = 0x00;
        private const long RegCtrl2 = 0x02;
        private const long RegFilt = 0x04;
        private const long RegLastedge = 0x06;
        private const long RegPosdper = 0x08;
        private const long RegUpos = 0x0C;
        private const long RegLpos = 0x0E;
        private const long RegPosd = 0x10;
        private const long RegPosdh = 0x12;
        private const long RegUposh = 0x14;
        private const long RegLposh = 0x16;
        private const long RegLastedgeh = 0x18;
        private const long RegPosdperh = 0x1A;
        private const long RegRevh = 0x1C;
        private const long RegRev = 0x1E;
        private const long RegUinit = 0x20;
        private const long RegLinit = 0x22;
        private const long RegUverid = 0x50;
        private const long RegLverid = 0x52;

        private const ushort CtrlLdok = 0x0001;
        private const ushort CtrlSwip = 0x0800;
    }
}
