# renode-imxrt1180

A **Renode** platform model of the **NXP i.MX RT1180** crossover MCU (the fully-loaded
**MIMXRT1189** on the MIMXRT1180-EVK): a heterogeneous dual-core part pairing a secure
**Cortex-M33** boot core with a **Cortex-M7** application core, targeting real-time
**motor control** (eFlexPWM + quadrature encoder + LPADC) and **Gb TSN** industrial
networking.

- **Built on** Renode 1.17.0 — declarative `.repl` platforms plus self-contained C#
  peripherals in `peripherals/`, loaded at runtime (`i @file.cs`), no emulator rebuild.
- **Sibling of** [`qemu-imxrt1180`](https://github.com/kylefoxaustin/qemu-imxrt1180), the
  QEMU model of the same silicon. **That model is this one's reference oracle**: the same
  vendor firmware and the same external test corpus run against both, and every row is
  measured on both sides rather than quoted from one.
- **What sets it apart:** Renode's native multi-core and multi-node co-simulation, which
  is why the dual-core and cross-node axes are where this model is asked to win.
- **Maintainer:** @kylefoxaustin.
- **North star:** fidelity-first — a silently-wrong answer is the worst bug; a model is
  register-accurate and *flags* any gap rather than faking a result.

## Quickstart

```sh
# Renode 1.17.0 portable + a .NET runtime; no build step for the model itself.
# Run from the repo root. The stock SDK cm33 hello_world ELF is the default;
# point it elsewhere with -e '$bin=@/path/to/your.elf' before the include.
renode --console --disable-xwt --plain \
    -e 'include @scripts/rt1180_hello.resc' \
    -e 'emulation RunFor "0.5"' -e quit
# [INFO] lpuart1: ... hello world.

./scripts/run_m0.sh          # the same run, with the external oracle asserted
```

`scripts/load_peripherals.resc` compiles every C# peripheral at load time, so the
edit→run loop is seconds and needs no relink. **The one exception is the CPU core
itself** — Cortex-M defects live in `tlib`, below the scripting boundary, and those
needed a native rebuild (see *Patched core*).

## What runs today

Real NXP MCUXpresso SDK firmware runs against the **unmodified `fsl_*` drivers**, plus
the QEMU model's external test corpus — **the same binaries, not ports**.

| axis | result |
|---|---|
| **SDK console corpus vs QEMU** | **30 / 30 examples agree, differ 0** (31 rows) — both columns MEASURED on one box, same bytes handed to both emulators in the same run |
| **Oracle value tests** | **45 / 55 PASS, 0 FAIL, 0 unexplained** — the other 10 ship their own harness and are driven through it |
| **Zephyr console delta** | **70 / 76 scoreable** agree as measured (44 byte-identical + 26 differing only in durations/counters), over 90 targets. **73 / 76 after adjudication** — 3 further rows differ only in values that are architecturally undefined, randomised by design, or a measured elapsed time the test itself scores 100%; each is signed off in [`results/zephyr-delta-adjudications.tsv`](results/zephyr-delta-adjudications.tsv) and bound to a hash of the exact diff, so a regression cannot inherit the excuse. The **2 remaining differences are one mechanism**, not two: Renode's syscall/MPU guard denies where QEMU takes a bus fault. 14 targets are **unscoreable** — free-running samples that never terminate, whose line count measures the run guard, not the model; they are counted neither way |
| **Dual-core (the M2 rung)** | **8 / 8** |
| **Audio** | **5 / 5** — six SAI operating points byte-exact |
| **NETC switch** | **7 / 7** — including `netc-lab3`, run on a live multicast segment through the QEMU model's **own** `wire-check.py`, every assertion unchanged |
| **Both models, one wire** | **PASS** — 1180-renode and 1180-qemu on one segment simultaneously, each verifying the other's beacon body |

## Motor-control frontier: the M7 spins a virtual PMSM

`peripherals/IMXRT1180_Motor.cs` is a dq-frame PMSM plant (4 pole pairs, Rs 0.54 Ω,
Ld 335.6 µH, Lq 218 µH, Kt 0.0548 N·m/A, 24 V bus, 8000 cts/rev) driven by the modelled
eFlexPWM, read back through EQDC and LPADC. Goldens are **closed-form first principles**,
never read back from the model: Ohm's law for phase current, the Clarke transform, and
`θ = (J/k)·ln(1 + k·ω₀/B)` for a free coast-down. **A range is not a golden.**

## NETC switch: frames cross real wires ✅

The ENETC endpoint and SW0 L2 switch run the NTMP command ring (FDB + VLAN filter
tables), source-MAC learning, split-horizon forwarding, unknown-unicast/broadcast
flooding, and the IEEE-1588 timer as a DDS off virtual time.

Each wire port is an **independently connectable node**, so `netc-flood`'s assertion —
*flooded out 0 and 2, suppressed on ingress port 1* — is expressible at all. The oracle's
own `flood.py` and `portfwd.py` drive them **unchanged**, byte-exact, over point-to-point
UDP wires.

> ⚠️ Backend choice is **per test**, never global. A switched fabric on a reflective
> (shared/multicast) segment re-ingests its own flood and storms — a real loop, but not
> the topology under test, and it would read as a forwarding defect while being a backend
> artefact. Multicast is used **only** for the fleet's shared-segment lab node.

## Patched core: four Cortex-M defects fixed in `tlib`

Renode carried defects that no peripheral work could reach. The four `tlib` ones are
**mutation-proven** — the fix is switched off and on by swapping only the `.so`, and the
verdict flips with it — and ABI-verified (2564 symbols, 0 missing, 0 extra) before
install, with M0 as a positive control.

| patch | defect |
|---|---|
| `tlib-defect11-defect12.patch` | wrong EXC_RETURN SPSEL; thread-swap |
| `tlib-defect13.patch` | `CFSR.STKOF` never raised — ARMv8-M stack-limit protection was inert while every `MSR`/`MRS` to `MSPLIM`/`PSPLIM` succeeded |
| `tlib-defect14.patch` | **taking an exception does not clear `env->wfi`** — the core sleeps *inside its own interrupt handler*, with a correctly-formed stack frame, and nothing can wake it |

A fifth, **defect #16**, is in Renode's *managed* core rather than `tlib`:
semihosting **`SYS_TIME`/`SYS_CLOCK`** are declared in the `Operation` enum and
never implemented, so they return `-1` and any guest that timestamps reads
`0xFFFFFFFF`. Fixed the same way and held to the same standard — built from the
tree that ships, installed as a one-file assembly swap, and mutation-proven by
flipping only that file. `SYS_TIME` defers to Renode's own
`machine.RealTimeClockDateTime`, so semihosting agrees with the RTC on the same
machine and the `RealTimeClockMode` knob keeps working.

**Defect #14 affects every RTOS idle path on this core.** Its scope was settled by
measurement, not inference: a discriminating test that reaches the window (`entry wfi=1`,
witnessed by an instrumented core) hangs pre-fix on the M33 with a full frame pushed and
**zero handler instructions executed**, and passes post-fix. See
[`patches/README.md`](patches/README.md) for the build recipe and provenance chain.

## Validation

- `scripts/run_m0.sh` → stock SDK `hello_world` prints `hello world.` over LPUART1.
- `scripts/equivalency.sh` → **invokes `qemu-system-arm` itself** and runs the same
  bytes on both emulators in one pass, so neither column is quoted. Under fleet Law 1 a
  SOURCED-vs-MEASURED comparison may not carry a headline.
- `scripts/run_value_sweep.sh` → the oracle's bare-metal value tests, with a coverage
  assert (rows == binaries), a source fingerprint (models and the **CPU library** may not
  change mid-run), a corpus snapshot, and a **verdict-delta report** naming every row
  whose result moved.
- `scripts/run_sai_value.sh` → SAI streaming, byte-exact to a WAV at **two** operating
  points, verdict rendered **outside** the guest.
- `scripts/run_netc_wire.sh` → the oracle's `flood.py` / `portfwd.py` over real wires.
- `probes/m33-wfi-window/` → the discriminating test for defect #14, with the
  window-entry witness that makes its verdict meaningful.
- **[`results/SCORECARD.md`](results/SCORECARD.md)** → the tracked scorecard: every row
  MEASURED against an **external oracle** (a string the firmware itself prints, cited to
  SDK `file:line`), with honest classes — PASS / BANNER / XFAIL / NEEDS-OWN-HARNESS —
  never one collapsed headline.

## Required artifacts

This is an MCU, not a Linux applications processor — the "artifact" is **firmware**: a
bare-metal, Zephyr, or MCUXpresso image. The SDK's prebuilt `cm33/*.bin` demos work
directly; RAM/debug images link their vector table to the code TCM (`0x0FFE0000`), and a
**cm7** image (linking to the M7's local ITCM at `0x0`) is detected from its ELF entry
point and booted on the M7. XIP-linked images execute from the FlexSPI window, which is
backed by the NOR's own store — so *program the flash, then execute* is a real sequence.
_(Linux/DTB/rootfs rows: N/A — no Linux on this MCU.)_

## Building

Nothing to build for the model. Renode compiles `peripherals/*.cs` at load. The **core
patches** do need a native rebuild — built through Renode's `Cores` wrapper (a plain
`tlib` build omits the interop symbols), ABI-verified, then installed with the previous
library kept beside it under a name recording which defects it carries.

## Architecture

`platforms/mimxrt1189_cm33.repl` is the base map (cores, TCM/OCRAM, the peripheral
window); `..._m1.repl` and `..._m2.repl` layer on the peripheral set and the second core;
the `..._value.repl` / `..._wire3.repl` / `..._lab3.repl` platforms add per-harness
scaffolding. Renode's `using` inheritance means a derived platform *adds*, so the base
map stays the single source of truth.

Two ways the **M7** comes up:

- **M33-released** (the silicon path): the M33 releases the M7 through `SRC` /
  `BLK_CTRL_S_AONMIX` — the **two-gate** handshake, both gates modelled, with the M7 held
  at reset (`init: IsHalted true`) until both open.
- **Direct cm7 boot**: a cm7-linked image is detected from its ELF entry point and loaded
  into the M7's own view. This mirrors a convenience the QEMU machine provides, and is
  labelled as such on both sides rather than claimed as silicon behaviour.

## Repository tour

- `platforms/*.repl` — the memory map and peripheral set (base, dual-core, per-harness)
- `peripherals/IMXRT1180_{Anadig,CCM}.cs` — clock tree (PLLs incl. AUDIO PLL, roots, gates)
- `peripherals/IMXRT1180_{PWM,EQDC,LPADC,Motor,XBAR}.cs` — the motor-control frontier
- `peripherals/IMXRT1180_{SAI,ASRC}.cs`, `WM8962.cs` — audio (file-only WAV sink, never a host device)
- `peripherals/IMXRT1180_{LPIT,QTMR,GPT,TPM,LPTMR}.cs` — the timer blocks
- `peripherals/IMXRT1180_{LPSPI,LPI2C,FlexCAN,USDHC}.cs` — serial + storage
- `peripherals/IMXRT1180_{eDMA4,DMARequestRouter}.cs` — eDMA3/4 and the request MUX
- `peripherals/IMXRT1180_NETC.cs` — NETC: ENETC endpoint + SW0 switch + per-wire ports
- `peripherals/{UdpWire,EchoWire}.cs` — **harness backends, not devices** (wires, not silicon)
- `peripherals/IMXRT1180_{S3MU,FlexSPI,SRC,TRDC,MECC,Xcache}.cs` — ELE MU, FlexSPI, M7-release, TRDC, ECC, caches
- `patches/` — the `tlib` core patches, build recipe, and library provenance
- `probes/` — targeted experiments (the defect-#14 window test and its negative controls)
- `scripts/` — runners and the measured-vs-measured equivalency harness
- `results/` — scorecards, the delta study, and the raw per-row consoles

## How this model is validated

Two independent implementations run **one corpus**, and each one's failures bound the
truth from a different side. That is a stronger claim than cross-checking, and it is the
reason this repo exists alongside the QEMU one.

Rules this model is held to, all of them paid for:

- **Green is not fidelity; red is not accuracy; self-blame is not verification.** Every
  divergence's direction is decided against the reference manual, whichever way the prior
  leans.
- **A side effect observed is not the act performed.** A counter cannot distinguish
  *forwarded* from *accounted for*; at least one test must consume the artifact.
- **A parameter that never varies collapses two behaviours into one observation.** Five
  defects here were invisible at a single operating point.
- **Model what the driver waits on, not what the test checks.** A model built to exactly
  what a test polls passes the test and hangs the SDK.
- **Every number carries its provenance, and the flag must survive the copy.**

## Known limitations

- **SAI RX** is modelled here and absent in the reference — but **the observable
  difference is none**. MEASURED with the stock SDK `sai/edma_record_playback`: both
  models print the same two lines and stall at the same point, because no sample source
  feeds the RX path on either side. A register that nothing drives is not a capability.
  Closing it needs a source and a payload-consuming test on *both* sides.
- **LPSPI chip-select** drops on `CONT`-clear here; the reference does not route CS to
  runtime-attached slaves, so its flash answers `RDID` once per boot. Measured on both
  sides; the reference side is the permissive one.
- **ASRC taps are not NXP's.** The resampler is a real polyphase windowed-sinc FIR proven
  against DSP first principles (unity DC gain, Nyquist rejection); NXP's taps are
  unpublished, so values will not bit-match silicon. Flagged, not faked.
- **uSDHC capabilities** are a best-effort word carried **with the reference's own
  caveat** attached, not promoted to fact by being copied.
- **XIP execution** does not run the AHB read sequence on the fetch path, so a
  misprogrammed LUT cannot break execution as it would on silicon.
- **This model requires a patched Renode**, not a stock one: four `tlib` defects and
  one managed-core defect (#16). `patches/` carries all five with the build recipe and
  the provenance of each installed binary. On a stock 1.17.0, `netc-lab3` fails at the
  guest clock and the Cortex-M defects remain live.
- Blocks the corpus does not reach are absent rather than stubbed, and say so.

## Roadmap

1. ~~**Holobench**~~ — **done.** Both models run on **one multicast segment at the
   same time**, each content-verifying the other's frames:

   ```
   node A (QEMU,   0x88B6): ENET-LAB3 PASS -- 0x88b9 VERIFIED, 0x88b5 VERIFIED
   node B (Renode, 0x88B9): ENET-LAB3 PASS -- 0x88b6 VERIFIED, 0x88b5 VERIFIED
   ```

   Each node's *required* peer set names the other model's EtherType, so neither
   can reach PASS without the other. Mutation-proven: isolate the Renode node on
   its own group and the run goes red. `scripts/run_holobench_sidebyside.sh`.
2. **Upstream the `tlib` patches** — four mutation-proven Cortex-M defects, none
   RT1180-specific.
3. **Close the shared SAI RX gap on both sides**, with a test that consumes received
   samples — so the direction is proven, not asserted.

## License

Model sources follow the licensing of the components they derive from; the `tlib`
patches are GPL-2.0-or-later, matching Renode's core. See individual file headers.
