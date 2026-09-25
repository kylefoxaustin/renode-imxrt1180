# EXPERIMENT.md — Renode vs QEMU on the i.MX RT1180

The deliverable is the *comparison*, not the model. Per rung: result, effort, what
fought us, what each tool gave for free.

**Provenance discipline (fleet Law 1) applies to the EFFORT column specifically.**
Effort is the easiest number in the world to launder — a remembered "that took an
afternoon" is SOURCED, not MEASURED. Tags used here:
- **MEASURED** — wall-clock or command count I can point at in this session's log.
- **SOURCED** — recollection (mine or @rt1180emulator's), or vendor/doc claim.
- **DERIVED** — computed from the above; carries both factors' conditions.

A SOURCED effort figure is **never** placed in the same column as a MEASURED one.

---

# 📊 FINDINGS — where each tool wins

*This section is the deliverable. Everything below it is the working log that
produced it. Written after all three rungs closed.*

## Verdict in one line
**Renode ran the unmodified vendor firmware on every rung of the ladder and
reproduced all six oracles — but needed a patch to its native CPU core to do it,
and that patch is the one class of fix its authoring model cannot reach.**

## Axis 1 — Fidelity: does the unmodified NXP SDK firmware run the same?

| rung | oracle | Renode | QEMU |
|---|---|---|---|
| M0 `hello_world` | `hello world.` | ✅ | ✅ |
| M1 `edma4/memory_to_memory` | `EDMA memory to memory example finish` | ✅ | ✅ |
| M1 `s3mu` | `End of Example with SUCCESS!!` | ✅ | ✅ |
| M1 `edma4/scatter_gather` | `EDMA scatter gather transfer example finish` | ✅ | ✅ |
| M2 `multicore_manager` | `The secondary core application has been started.` | ✅ | ✅ |
| M2 `rpmsg_lite_pingpong` | `Message: Size=4, DATA = 101` | ✅ | ✅ |

**6 of 6 agree. Full fidelity across the ladder.**
Every oracle is the string the firmware itself prints, taken from the QEMU
scorecard so both tools face the identical criterion. Each verified by script
(exit 0/1) and reproduced on a second pass.

Getting the sixth took three attempts and a correction from @rt1180emulator —
see *"The DONE-signalling problem"* below. The earlier claim in this document
that *"no configuration of my model reproduces all six at once"* was **wrong**,
and is retracted there: it was a statement about the two configurations I had
tried, not about Renode.

⚠️ **The honest caveat on Renode's column: it is not stock Renode.** M2 and M1
require a 4-line patch to tlib's ARMv7-M MPU. Without it the Cortex-M7
HardFaults the instant it enables its MPU, and M2 is unreachable. "Renode runs
the vendor image" is true *of a patched Renode*.

## Axis 2 — The dual-core / multi-node axis (Renode's home turf)

**This is where Renode genuinely wins, and the win is structural.**

| need | Renode | QEMU |
|---|---|---|
| second core | `CPU.CortexM` + second NVIC, ~6 lines of `.repl` | ARMv7M container per core |
| per-core memory views | `BusPointRegistration { cpu: … }` — declarative | `cpu_mem[i]` address-space containers, C |
| TZ-M secure aliasing | second registration at `+0x1000_0000` | `memory_region_init_alias` |
| **inter-core MU** | **`IMXRT700_MessagingUnit` reused with ZERO changes** | hand-written `imxrt1180_mu.c` |
| determinism | free; virtual time identical across runs | needed `-icount` |

The MU is the headline: all 15 RT1180 offsets matched an existing Renode model,
**including the GP doorbell semantics RPMsg rides**, and it needed no code at
all — only addresses in a `.repl`. @rt1180emulator wrote theirs by hand.

**But the axis also produced Renode's two worst traps, both AMP-specific:**
1. **A second CPU runs by default.** Until explicitly parked, the M7 executed the
   M33's image off the shared bus — observed as eDMA writes tagged `[cpu1: …]`.
2. **A globally-registered memory silently defeats per-core views.** My M33 DTCM
   had no `cpu:` restriction, so it won for *both* cores and the M7's
   `MCMGR_eventTable` writes landed in the M33's table — which then called an
   M7 address and aborted. @rt1180emulator confirmed from their side that these
   are separate backing stores in separate per-core containers: **two emulators,
   same per-core-TCM requirement, arrived at independently.**

Net: Renode makes the dual-core *plumbing* dramatically cheaper, and makes a
specific new *class of error* — silent view collapse — easy to commit.

## Axis 3 — Authoring effort

**Renode's advantage is real, large, and has a hard floor.**

`i @file.cs` compiles and loads a C# peripheral **at runtime** — no build, no
relink, no emulator rebuild between iterations. M0 was six debug iterations in
ten minutes because of it. QEMU's equivalent loop is a C rebuild each time.

**The floor is the tlib boundary.** The MPU defect lives in the native core.
Fixing it required: pinning the exact tlib submodule commit for the shipped
release, discovering that tlib must be built *through* Renode's `Cores` wrapper
for ABI completeness (a plain `cmake` build omits **75** `renode_external_attach__*`
interop symbols), and a full native rebuild. In QEMU the same fix is an ordinary
in-tree edit to `target/arm/ptw.c` plus a rebuild — the same tree and toolchain
as every device model.

> **The asymmetry, stated precisely:** Renode's fast-iteration authoring
> advantage stops at the tlib boundary, and CPU-core fidelity gaps fall below
> it. Below that line the cost is comparable to QEMU's ordinary loop — *not*
> impossible. (I initially wrote "platform-unfixable"; that was too strong and
> is retracted.)

### Effort ledger
⚠️ **Wall-clock is reported for Renode only and is NOT compared across tools.**
@rt1180emulator has no reliable wall-clock ledger and said so plainly. Comparing
my MEASURED durations against their recollection would be exactly the mixed-tier
comparison Law 1 forbids. The cross-tool columns are blocker- and
iteration-counts, which both sides have.

| rung | Renode (MEASURED) | QEMU (MEASURED, from git) |
|---|---|---|
| M0 | ~10 min, 6 iterations, 5 peripherals, 987 lines | "effectively free once the skeleton existed" — the skeleton *is* the cost |
| M1 | ~25 min, incl. grouped-IRQ wiring + TCM DMA aliases | incremental, no single hard blocker |
| M2 | the rest of the session: eDMA deferral, TRDC, DCDC, WAKEUPMIX, SRC two-gate, MU (reused), **a tlib patch**, a memory-scoping fix, a timing fix | ~10–11 commits, **9 distinct blockers** |

**Code actually authored: 1,906 lines** (8 peripherals + 2 `.repl` + 4 `.resc` +
4 test scripts). A further 1,321 lines sit in `IMXRT1180_eDMA4.cs`, which is
**Renode's own model carried into the project with a 1-statement patch** — it is
counted separately rather than folded into "lines I wrote," because doing so
would inflate the authoring figure by 40%.

**Both tools agree M2 dominates.** Different specific blockers, same shape.

## The four cross-tool agreements (both sides, independently)
1. **The eDMA software-START hazard is tool-independent.** Two emulators, two
   authors, two languages, the *same* wrong simplification — DONE set inside the
   START write. Predicted in Renode before running, confirmed at the exact
   function, and fixed the same way.
2. **rpmsg was first-try, zero model change.** The AMP *boot chain* is the entire
   cost; the transport is free once the MU doorbell, shared OCRAM and both
   cores' IRQ routing are right.
3. **M2 is the only expensive rung, and it dominates.**
4. **MCMGR's event table must be per-core TCM.** Collapsing the two into one
   memory makes one core call the other's function pointers.

## What each tool gave for free
| | Renode | QEMU |
|---|---|---|
| **free** | both CPU cores, NVIC, register-accurate LPUART, **the entire MU**, per-core bus views, TZ-M aliasing, determinism without a flag, runtime C# compilation | full control of every device; CPU and devices are one codebase and one build |
| **cost** | 8 hand-written peripherals (+1 that is Renode's own eDMA carried with a 1-statement patch); a native-core patch that the fast-iteration model cannot reach; two AMP footguns (auto-running second core, silent view collapse) | every device model written from scratch in C; `-icount` for determinism; a C rebuild per iteration |

## Defects found in Renode (upstream-worthy)
1. **ARMv7-M MPU applies region checks to the Private Peripheral Bus.**
   `get_phys_addr_mpu()` lacks the PPB short-circuit QEMU has at
   `ptw.c:2588/2646`. Any M-profile firmware calling `ARM_MPU_Enable()` HardFaults
   on the next `SCB->SHCSR` access. **Patched locally; fix verified.**
2. **`MPU_CTRL.PRIVDEFENA` is never propagated** to tlib's `c1_sys[17]`, so every
   access outside an enabled region background-faults. Broader than defect 1.
3. **`DMA.NXP_eDMA` hard-caps at 32 channels**; RT1180's eDMA4 has 64
   (`MP_INT`/`MP_HRS` are single 32-bit registers). Concrete risk, because
   eDMA4's grouped IRQs make the stock handler probe channels 32–63 on every
   interrupt.
4. **`UART.NXP_LPUART` WATER fields are too narrow for this SoC** — it matches
   the MCX A287/A577 variant. Benign for the stock driver; wrong vs silicon.
5. ~~**ARMv8-M escalates an ENABLED UsageFault to HardFault, then locks up.**~~
   **RETRACTED — this was my platform misconfiguration** (`enableTrustZone`
   unset on a core running secure firmware), not a Renode defect. See the
   control-scope section. Defect count stands at **4**.
   *(original text kept below for the record)*
   With `SHCSR.USGFAULTENA` set, a `udf` produces `CFSR.UNDEFINSTR` +
   `HFSR.FORCED` + lockup at `0xEFFFFFFE`, where the same binary on Renode's
   Cortex-M7 passes and a from-scratch QEMU Cortex-M33 delivers the fault to the
   guest handler. Breaks Zephyr's `arm_interrupt` and `arm_hardfault_validation`
   on CM33. See the evidence block above.

*(A fifth was reported and then withdrawn: an "ESG reload drops 16-bit fields"
finding that was an artifact of probing word registers with a doubleword read.
Renode's ESG decode is correct. See the retraction below.)*

## Method notes that changed outcomes
- **Pre-registering the eDMA prediction *and its falsifier*** — including "an
  accidental quantum-boundary pass must not be scored as a win" — is what made
  the result trustworthy rather than a story told afterwards.
- **Two instruments produced confident wrong answers.** A monitor
  context-qualified memory probe reported per-core separation that did not exist;
  a stuck PC looked like a hang that was actually an 8×-slow delay loop. Both
  were caught by switching to an instrument that *could* have disagreed — the
  system alias with one backing store, and a drain-rate measurement.
- **Reading the oracle's C models instead of the RM** is why M0 took ten minutes.
  Every hang was a status bit the driver polls; the RM does not say which.


---

## Rung 0 — Environment (prerequisite, not a rung)

| item | value | provenance |
|---|---|---|
| Renode | v1.17.0, build `1.17.0+20260907gitf1dd1b4af`, .NET 8.0.12 | MEASURED (`renode --version`, 2026-09-16) |
| install | portable tarball, **no root**, `~/.cache/renode/renode_1.17.0-portable` | MEASURED |
| tarball | `renode-1.17.0.linux-portable.tar.gz`, 63459129 B, sha256 `4ba7c68b59e2447f188ef4b4b112fcccd2802582c460beedceb63b84e5605a5f` | MEASURED (size matches GitHub release metadata) |
| C# sources | `~/.cache/renode-infrastructure` (shallow clone, 29 MB) — the portable tarball ships **no** peripheral source | MEASURED |
| host | Linux 6.8.0-136-generic, mono 6.x present but unused (portable bundles .NET) | MEASURED |

### Positive control — the install actually emulates
`--version` proves nothing about the emulator. Control run: bundled
`scripts/single-node/stm32f4_discovery.resc`, headless
(`--console --disable-xwt --plain`), `emulation RunFor "0.5"`.

Result: real Cortex-M execution on tlib, Contiki 3.x booted and printed to `uart4`
("Contiki 3.x started.", "* Ethernet process started", "ETH_BSP_Config done!",
"DHCP IP config started"). Exit 0, machine disposed, **no processes left behind.**
→ Renode is functional, headless, and scriptable on this box. MEASURED.

---

## The reuse survey (done BEFORE writing any `.repl` — "reuse beats rewrite")

The brief's rule 4 is *do not fabricate offsets*; the corollary for Renode is
**do not trust a model's name — verify its register map against the QEMU facts.**
So every claim below was read out of the C# source, not inferred from the class name.

### ✅ LPUART — `UART.NXP_LPUART` (M0)
Already used by `platforms/cpus/imxrt1064.repl` (`lpuart1..8`, e.g. `@ sysbus 0x40184000`,
`IRQ -> nvic@20`). Offsets are computed: `CommonRegistersOffset = 0x10` (when
`hasGlobalRegisters`), `FifoRegistersOffset = 0x18 + 0x10 = 0x28`.

| reg | RT1180 brief | Renode NXP_LPUART | verdict |
|---|---|---|---|
| VERID | 0x00 (reset `0x04010003`) | enum only — **NOT in registersMap** | ⚠ gap |
| PARAM | 0x04 | enum only — **NOT in registersMap** | ⚠ gap |
| GLOBAL | 0x08 | 0x08 (RST bit 1) | ✓ |
| PINCFG | 0x0C | enum only — **NOT in registersMap** | ⚠ gap |
| BAUD | 0x10 (reset `0x0F000004`) | 0x10, **reset 0** (no reset value given) | ⚠ reset mismatch |
| STAT | 0x14 | 0x14 | ✓ |
| CTRL | 0x18 | 0x18 | ✓ |
| DATA | 0x1C | 0x1C | ✓ |
| FIFO | 0x28 | 0x28 | ✓ |
| WATER | 0x2C | 0x2C | ✓ |

**The three bits the driver polls — all present and correctly placed:**
- `STAT.RDRF` **bit 21** ✓ (`BufferState == Ready || Full`)
- `STAT.TC` **bit 22** ✓ (`txQueue.Count == 0`)
- `STAT.TDRE` **bit 23** ✓ (backed flag)

**Two real discrepancies to carry into M0 (neither fatal, both silent-wrong class):**
1. **`WATER.RXCOUNT` is 3 bits `[26:24]`, clipped at 7** — the brief says RT1180 is
   **5 bits `[28:24]`**. Benign for `hello_world` (the echo loop reads 0 either way,
   and 0 is 0 in both encodings) but a genuine width mismatch if anything ever fills
   the RX FIFO past 7. The Renode source itself flags this as guesswork: *"This value
   is not backed by the manual… adjust if proven not to work."*
2. **VERID / PARAM / PINCFG unimplemented** — reads will fall through to the
   unimplemented-register path. Must confirm whether `LPUART_Init` in the stock
   `fsl_lpuart.c` reads PARAM/FIFO for FIFO depth. **Open question, not yet answered.**
3. **BAUD resets to 0, not `0x0F000004`.** Harmless if the driver writes BAUD whole;
   a silent-wrong if it read-modify-writes. To check against the QEMU model.

### ⭐ MU — `Miscellaneous.IMXRT700_MessagingUnit` (M2) — the surprise
Used by `platforms/cpus/mimxrt798s.repl`. **Every one of the 15 offsets in the brief
matches, exactly:**

VER `0x000` ✓ · PAR `0x004` ✓ · CR `0x008` ✓ · SR `0x00C` ✓ · FCR `0x100` ✓ ·
FSR `0x104` ✓ · **GIER `0x110`** ✓ · **GCR `0x114`** ✓ · **GSR `0x118`** ✓ ·
TCR `0x120` ✓ · TSR `0x124` ✓ · RCR `0x128` ✓ · RSR `0x12C` ✓ ·
**TR[0..3] `0x200`** ✓ · **RR[0..3] `0x280`** ✓

**The doorbell RPMsg rides is implemented with the brief's exact semantics:**
`GCR[n] = 1` → `otherSide.GSR[n] = true` → `otherIRQ.Set()`, gated on the *other*
side's GIER. Cross-wiring is structural: the model defines two register collections,
`(mySide: A, otherSide: B)` and `(mySide: B, otherSide: A)`.

And the RT700 platform already demonstrates the **exact registration shape RT1180 needs** —
per-CPU bus regions *plus* the TrustZone-M secure alias:
```
mu1: Miscellaneous.IMXRT700_MessagingUnit @ {
    sysbus new Bus.BusMultiRegistration { address: 0x40202000; size: 0x1000; cpu: cpu0; region: "aInstance" };
    sysbus new Bus.BusMultiRegistration { address: 0x40203000; size: 0x1000; cpu: cpu1; region: "bInstance" };
    sysbus new Bus.BusMultiRegistration { address: 0x50202000; ... }   # +0x1000_0000 secure alias
    ... }
AInstanceIRQ -> nvic0@30
BInstanceIRQ -> nvic1@26
```
RT1180 differs only in the numbers: MUA `0x44220000` (cm33) / MUB `0x44230000` (cm7),
IRQ **21** on each core's NVIC, secure alias at `+0x1000_0000`.

**⚠ One PLAUSIBLE (not confirmed) defect to test, not to trust:** the GP doorbell calls
`otherIRQ.Set()` directly, but `UpdateInterrupts()` recomputes
`Set(ReceiveFullPending | TransmitEmptyPending)` — which does **not** include GP status.
A later `UpdateInterrupts()` could therefore *clear* a pending GP doorbell IRQ. If RPMsg
misbehaves at M2, look here first. **Unverified — do not report as a finding until reproduced.**

**MU reset values are RT700's, not RT1180's:** `VersionID = 0x0309000F`,
`Parameter = 0x03040404`. The brief does not give RT1180's. Resolve from the QEMU model
or the CMSIS `MIMXRT1189_COMMON.h` before relying on them. **TBD — do not guess.**

### What Renode does NOT ship (must be authored)
No model found for any of: **MU is covered**, but the dual-core boot chain is not —
`M7_CFG` / BLK_CTRL_S_AONMIX `@0x444F0080` (INITVTOR + WAIT bit 4), `SRC.SCR @0x44460010`
(BT_RELEASE_M7), and the **S3MU/ELE `@0x47540000`** handshake
(`TR[0]=0x17d20106` → `RR[0]=0xE1D20206`, `RR[1]=0xD6`). Also no RT1180 board/cpu `.repl`.
Nearest NXP i.MX RT platforms shipped: `imxrt1064`, `imxrt500`, `mimxrt798s`, `nxp-mimx8ml`.

---

## Early read on the three axes (provisional — no rung passed yet)

**Authoring effort:** the two peripherals I expected to cost the most, LPUART and MU,
are *already written and register-accurate*. That is the single biggest surprise so far.
The MU match in particular is 15/15 offsets plus correct doorbell semantics — I expected
to write that block by hand for M2.

**Dual-core axis:** `BusMultiRegistration { cpu: ... }` gives per-core memory views
declaratively. RT1180 needs exactly this twice over — the M7's local view
(ITCM@0x0 + DTCM@0x20000000) vs its system view (`0x303C0000`), and the TZ-M
NS/secure aliasing. This is the thing QEMU made expensive.

**The #1 risk is now ANSWERED — see the eDMA finding below.** The `InitCM7DMA` race is
intrinsic to the firmware; `-icount` was only ever about determinism. Renode ships an
eDMA model with the identical instant-completion defect QEMU had, so M2 will hit the same
hang. Prediction and falsifier are pre-registered below.

---

---

## ⭐ THE eDMA FINDING — both tools made the SAME mistake, independently

This is the strongest comparison result so far, and it arrived before M0 was written.

### The oracle's verdict (@rt1180emulator, bus 2026-09-16 12:13)
**The `InitCM7DMA` race is INTRINSIC to the firmware. The only QEMU artifact was
instant completion.** Their words, paraphrased tightly:

- Firmware idiom, true on any emulator *and on silicon*: software-START the eDMA to
  zero the M7 TCM → W1C-clear `CH_CSR[DONE]` (drop a **stale** flag) → poll DONE for
  **this** transfer.
- On silicon this is correct *because the transfer takes real bus cycles* — it is in
  flight across the clear, so the clear drops the stale flag and DONE sets later.
- QEMU's bug: the software-START minor loop ran **synchronously in the MMIO write**, so
  DONE was set *before* the W1C; the clear wiped the fresh DONE; the poll hung forever.
- **`-icount`'s role is ONLY determinism, not correctness.** The load-bearing requirement
  is: *the eDMA software-START must not complete synchronously with the START write.*

Verified independently rather than taken on trust (they invited it — "read these, don't
take my word"). All four cited commits exist in `~/Documents/GitHub/rt1180emulator`,
dates and messages match the claims, and the pinning test is real:

| commit | date | subject | checked |
|---|---|---|---|
| `55aef3f0dd` | 2026-08-03 | eDMA software START completes in virtual time, not instantly | ✓ + `tests/imxrt1180-edma-swstart-order` exists |
| `31a8c444c4` | 2026-08-03 | gate the M7 release on CPUWAIT (M7_CFG.WAIT), the AMP two-gate boot | ✓ |
| `2dd07ed189` | 2026-08-04 | ELE kick-CM7 (0xd2) answers SUCCESS — MCMGR_StartCore completes | ✓ |
| `622319f30f` | 2026-08-19 | re-park the M7 held on every reset — fixes a reboot crash | ✓ |

### Renode ships `DMA.NXP_eDMA` — and it has the identical defect
Read out of `NXP_eDMA_Channels.cs` (not inferred):

```csharp
Registers.TCDControlAndStatus.Define(wRegisters, name: "TCDn_CSR")
    .WithFlag(0, out tcdInMemory.START, writeCallback: (_, val) => {
        if(val) {
            ExecuteTransfer();            // <-- runs to completion INSIDE the MMIO write
            tcdInMemory.START.Value = false;
        }
    }, name: "START")
```
`ExecuteTransfer()` sets `channelDone.Value = true` before returning, and `channelDone`
is `CH_CSR` **bit 30, `WriteOneToClear | Read`** — i.e. the very DONE bit the firmware
polls. The source even states the model's intent out loud:

> *"Hardware clears START flag after the channel begins execution, what is immediate
> during emulation so software always reads 0 from these fields."*

**So Renode's eDMA completes instantly, exactly as QEMU's did before `55aef3f0dd`.**

### Why this matters more than a bug report
Two emulators, two independent authors, two different languages — **the same wrong
simplification.** That is strong corroboration that the hazard is a property of the
*firmware idiom*, not of either tool. It also means this rung's cost will NOT differ
between the tools for the reason one might have guessed (QEMU's `-icount` vs Renode's
quantum); both start from the same broken position. The comparison question narrows to:
*which tool makes deferring DONE by one time unit cheaper to express?*

### PRE-REGISTERED PREDICTION + FALSIFIER (written BEFORE running M2)
Registered now so the result cannot be rationalised afterwards.

- **Prediction:** running the stock `multicore_manager` against a Renode platform that
  uses stock `DMA.NXP_eDMA` will **hang** in `InitCM7DMA` / `Prepare_CM7`, with the M7
  never leaving reset — the same hang QEMU hit, from the same cause.
- **Falsifier:** it boots and prints `The secondary core application has been started.`
  anyway. That would mean either (a) Renode's quantum boundary happens to land between
  the START write and the W1C — making it *accidentally* right and fragile, or (b) the
  `--config debug` image's `Prepare_CM7` does not take the eDMA path I assume. Either
  outcome is reportable; (a) in particular must not be scored as a Renode win.
- **Instrument check:** before concluding "it hung in the eDMA poll", I must confirm the
  PC is actually in the DONE poll loop — an absence of output is not, by itself, evidence
  of *this* cause. A hang elsewhere would be a different finding wearing the same symptom.

### Consequence for the platform
Do **not** shortcut the M7 TCM zero+copy as a bare memcpy. The firmware writes eDMA
registers and polls `CH_CSR[DONE]` regardless of what the host does underneath, so the
DONE bit must be modelled either way. The fix shape, if the prediction holds: defer
`channelDone` by at least one time unit rather than setting it in the START callback.

---

---

## ✅ M0 — `hello_world` over LPUART1: **PASS**

Stock NXP SDK `examples/demo_apps/hello_world`, `-Dcore_id=cm33 --config debug`,
built from the same `~/.cache/rt1180-sdk` tree the QEMU scorecard uses. **The
firmware is unmodified.** Console output:

```
MCUX SDK version: 2026.06.00
hello world.
```

**Oracle met:** the console contains `hello world.` — the same EXTERNAL oracle
string the QEMU scorecard uses. Verified by `scripts/run_m0.sh` (exit 0 = PASS),
not by eyeballing a log. Run twice: PASS both times, and **virtual time was
identical across runs (1.4 ms) while host time varied** — Renode's determinism
visible for free.

### Effort — MEASURED
| metric | value | how measured |
|---|---|---|
| bring-up wall-clock | **~10 min** (first boot attempt 12:18:40 → oracle string 12:28:38, 2026-09-16) | log timestamps |
| bring-up iterations | **6** runs | count of `m0run*.log` |
| peripherals authored | **5** (ANADIG, S3MU, CCM, XCACHE, FlexSPI-stub) | files in `peripherals/` |
| lines authored | **987** total: 855 C# + 112 `.repl` + 20 `.resc` | `wc -l` |
| peripherals REUSED from Renode | **2** — `UART.NXP_LPUART`, plus `CPU.CortexM`/`IRQControllers.NVIC` | — |

⚠️ **This wall-clock figure is MEASURED but it is NOT comparable to anything on
the QEMU side**, because @rt1180emulator has no wall-clock ledger (they said so
plainly). It is recorded for the Renode column only. The cross-tool comparison
uses iterations and blockers, which both sides have.

### The bring-up loop, and what each iteration cost
Every hang was found the same way: run → read the final PC → disassemble that PC
in the ELF → identify the address in the CMSIS header → **read the QEMU oracle's
C model for that block** → port it. Not one register value was guessed.

| # | hang PC | polled address | identified as | fix |
|---|---|---|---|---|
| 1 | `0xffe3692` | `0x44484320` | `ANADIG_OSC->OSC_24M_CTRL` bit 30 `OSC_24M_STABLE`, in `BOARD_BootClockRUN` | port ANADIG |
| 2 | `0xffe4a1a` | `0x47540124` | S3MU `TSR` — the ELE handshake | port S3MU |
| 3 | `0xffe64da` | `0x425E00E0` | FlexSPI1 `STS0` ARBIDLE/SEQIDLE, in `BOARD_DeinitFlash` | FlexSPI idle stub |
| 4 | `0xffe6454` | `0x444588A0` | CCM `LPCG[34].STATUS0` (0x8000 + 34*0x40 + 0x20) | port CCM |
| 5 | `0xffe64d6` | `0x445E00E0` | FlexSPI**2** `STS0` — board deinits *both* instances | map second instance |
| 6 | — | — | — | **PASS** |

**Every single one was a status bit the driver polls.** That is the brief's
lesson #2 confirmed empirically, six times in ten minutes, and it is the
strongest argument for the oracle's value: the RM alone would not have told me
that `OSC_24M_STABLE` must read back set, and the CMSIS header does not either.

### ⭐ A hang I did NOT get, and fixed anyway
With XCACHE unmapped, reads returned 0, so `BOARD_DeinitFlash` saw
`CCR.ENCACHE == 0` and **skipped the entire cache-maintenance block.** Boot
advanced *for the wrong reason* — an accident of unmapped-reads-as-zero, not a
modelled behaviour. Had I stopped at "it boots", that would have shipped as a
silent wrong. XCACHE is modelled properly (transient command + GO bits
self-clear on write), so the block now executes and completes.

### Honesty carried over from the oracle, not just code
Three things in this platform are deliberately *not* fabricated:
1. **S3MU/ELE keeps the oracle's whitelist.** Only commands whose real-silicon
   OUTCOME the model reproduces answer `RESPONSE_SUCCESS`; everything else gets
   a well-formed NON-SUCCESS reply. The oracle records that a blanket-SUCCESS
   stub once made `ELE_RngGetRandom()` return `kStatus_Success` while handing
   its caller **un-initialised memory as cryptographic randomness.** The
   whitelist INFORMS without GATING — firmware never hangs, it just learns the
   truth. `GET_RNG_RANDOM` is genuinely implemented with real entropy, because
   entropy is a thing we *can* compute.
2. **FlexSPI is labelled a stub, in its own filename comment and in the repl.**
   It implements the idle/reset handshake and nothing else; an actual IP command
   logs an ERROR rather than returning a fabricated result. No fake NOR is
   mapped at the XIP window — `--config debug` images never fetch from flash, so
   a fake flash would add a lie, not fidelity.
3. **CCM declines to compute clock frequencies.** `OBSERVE.FREQ_CURRENT` logs an
   error instead of returning 0, because a 0 there is a fabricated measurement.
   Nothing on the M0 path reads it.

### Known-incomplete, recorded rather than hidden
Touched during boot but **did not block**, so left unmapped (reads return 0 and
are logged): `IOMUXC_AON` (`0x443C0024/0094/0098`) and `BLK_CTRL_WAKEUPMIX`
(`0x42420018..0x42420040`). These are pin-mux and wakeup-domain writes with no
poll behind them on this path. They are a **latent silent-wrong exactly like
XCACHE was** — benign today, not proven benign in general. Flagged for M1.


### ⚠ CONFIRMED fidelity defect in Renode's shipped `UART.NXP_LPUART` (for RT1180)
The survey flagged `WATER.RXCOUNT` as a width mismatch. It is worse than that,
and now has an authoritative citation and a mechanism.

RT1180's own header `devices/RT/RT1180/periph/PERI_LPUART.h` gives:

| field | RT1180 mask | bits | Renode `NXP_LPUART` | verdict |
|---|---|---|---|---|
| TXWATER | `0x0000000F` | [3:0] 4 | [1:0] 2 | too narrow |
| TXCOUNT | `0x00001F00` | [12:8] 5 | [10:8] 3 | too narrow |
| RXWATER | `0x000F0000` | [19:16] 4 | [17:16] 2 | too narrow |
| RXCOUNT | `0x1F000000` | [28:24] 5 | [26:24] 3, clipped at 7 | too narrow |

**Mechanism:** RT1180's LPUART FIFO is **16 deep**
(`FSL_FEATURE_LPUART_FIFO_SIZEn(x) == (16)`, `MIMXRT1189_cm33_features.h:757`),
so a fill level needs 5 bits. Renode's 3-bit field cannot report above 7.
Renode's model matches the **MCX A287/A577** LPUART variant instead — their
`PERI_LPUART.h` carries exactly Renode's narrow masks (`TXWATER 0x3`,
`RXCOUNT 0x7000000`). So the model is not wrong in general; it is wrong *for
this SoC*, which is precisely why "an existing model with the right name" had to
be verified rather than trusted.

**Impact: benign for M0 and for the stock driver.** `fsl_lpuart.c` sizes the
FIFO from the compile-time macro and never reads these fields back (0 runtime
reads of `base->VERID`/`base->PARAM`; the same grep finds 11 hits for
`base->FIFO`, so the instrument works). `hello_world`'s echo loop reads RXCOUNT
as 0, and 0 is 0 in either encoding. Recorded because it is a real difference
from silicon, not because it bites.

**Fixed what could be fixed:** `fifoSize: 16` is now set in the `.repl`
(grounded in the feature macro, not guessed) so `FIFO[RX/TXFIFOSIZE]` reports
the right encoded depth instead of Renode's 256-entry default. The field widths
cannot be fixed from the `.repl` — that needs a patched model. M0 re-verified
PASS after the change.

**Correction to my own earlier note:** when first tabulating this I wrote RT1180
masks I had not read (`0xFF`/`0xF00`-style). The authoritative masks above are
narrower than that. The conclusion is unchanged — all four fields are too narrow
in Renode — but the numbers I first put next to "RT1180" were guesses sitting in
a column that implied they were sourced.

### Comparison notes for this rung
- **Renode gave for free:** both CPU cores, NVIC, and a register-accurate
  `UART.NXP_LPUART` whose STAT poll bits (RDRF b21 / TC b22 / TDRE b23) are
  correctly placed. The three LPUART gaps found in the survey all turned out
  **benign on this path, and I verified that from the driver source rather than
  assuming it**: `LPUART_Init` never reads VERID/PARAM/PINCFG, and it masks off
  and rewrites both OSR and SBR — the only fields where BAUD's reset value
  differs — so the reset-value mismatch cannot bite here.
- **Renode cost:** everything else on the boot path. Note that this is the
  *same* work QEMU needed; it is not a Renode tax. @rt1180emulator's "M0 was
  effectively free once the skeleton existed" is exactly right — **and this rung
  measured the skeleton, which is what their sentence hides.** The skeleton is
  ANADIG + S3MU + CCM + XCACHE + FlexSPI.
- **Runtime C# compilation is a real authoring-effort win.** `i @file.cs` in a
  `.resc` compiles and loads a peripheral with no build step, no rebuild of the
  emulator, no recompile between iterations. Six iterations in ten minutes was
  possible because of that. QEMU's equivalent loop requires a C rebuild and
  relink each time.


---

## ⭐⭐ M2 — THE PRE-REGISTERED eDMA PREDICTION IS **CONFIRMED**, with a measured mechanism

The prediction was registered **before** the multicore firmware was ever run
(see the eDMA section above). It was not adjusted afterwards.

> **Prediction:** stock `multicore_manager` on a platform using stock
> `DMA.NXP_eDMA` will **hang** in `InitCM7DMA` / `Prepare_CM7`, with the M7
> never leaving reset.

### Result: HANG, at exactly the predicted site

Stock `multicore_examples/multicore_manager/primary`, sysbuild, `--config debug`,
`-Dcore_id=cm33`. No console output at all. Final PC **`0xffe1abc`**.

**Instrument check first** — I pre-committed to proving the PC is in the DONE
poll rather than inferring it from silence, because "no output" wears the same
symptom as every other hang. Disassembly of that address in the image:

```
 ffe1aa6:  strh  r2, [r3, #60]   ; 0x3c   TCD_CSR = 9  -> START (bit 0)
 ffe1ab4:  str   r3, [r2, #0]            CH_CSR  = 0x40000006 -> W1C-clear DONE
 ffe1ab8:  mov.w r3, #0x42000000  <-+     the poll loop
 ffe1abc:  add.w r3, r3, #0x10000    |    <-- FINAL PC IS HERE
 ffe1ac0:  ldr   r3, [r3, #0]        |    read CH_CSR
 ffe1ac2:  and.w r3, r3, #0x40000000 |    mask DONE (bit 30)
 ffe1ac6:  cmp   r3, #0              |
 ffe1ac8:  beq.n ffe1ab8           --+    loop while DONE == 0  (forever)
```
The enclosing symbol is **`InitCM7DMA`**. The W1C literal is `0x40000006`
(read out of `.text` at `0xffe1aec` as `06000040` little-endian) — bit 30 is
DONE. This is the firmware idiom @rt1180emulator described, verbatim:
**START → W1C-clear a stale DONE → poll DONE for this transfer.**

### The mechanism, MEASURED not inferred
Reading the live register through the monitor after the hang:

```
(monitor) sysbus ReadDoubleWord 0x42010000     # eDMA4 CH0_CSR
0x00000006
```

**DONE (bit 30) is CLEAR.** So the sequence played out exactly as predicted:
Renode's `ExecuteTransfer()` ran the whole minor loop *inside the START register
write* and set `channelDone` before the write returned; the firmware's very next
instruction W1C-cleared that freshly-set DONE; and the poll can now never
succeed. The transfer **did happen** — it is the *flag* that was consumed early,
which is precisely why this failure is invisible to any check that only asks
"was the memory copied?".

### Falsifier: not triggered
The registered falsifier was "it prints the oracle string anyway", which would
have meant either an accidentally-favourable quantum boundary or a wrong
assumption about `Prepare_CM7`'s path. Neither occurred. Nothing needs to be
discounted, and no accidental pass is being scored as a win.

### What this settles for the comparison
**The hazard is tool-independent.** Two emulators, two authors, two languages,
the same wrong simplification — and now the same confirmed hang, from the same
measured cause, at the same function. QEMU needed `55aef3f0dd` to fix it; Renode
needs the equivalent. The `-icount`-vs-deterministic-quantum axis I expected to
be the story is **not** the story: both tools start from instant completion, and
the fix in both is "do not set DONE inside the START write."

### ⚠ A SECOND, INDEPENDENT Renode limitation found on the way in
`DMA.NXP_eDMA` **hard-caps at 32 channels** (`MaximumNumberOfChannels = 32`).
RT1180's eDMA4 has **64**. This is structural, not a parameter: `MP_INT` and
`MP_HRS` are defined as single 32-bit registers with one flag per channel
(`.WithFlags(0, NumberOfChannels, ...)` then `.WithReservedBits(NumberOfChannels,
32 - NumberOfChannels)`), whereas eDMA4 needs `MP_INT_L`/`MP_INT_H` pairs.
Fixing it means editing the model.

**The M2 platform therefore declares 32 channels, and says so in the `.repl`.**
That is a LABELLED DEVIATION, valid only for this test because
`InitCM7DMA`/`Prepare_CM7` uses **channel 0**. Channels 32–63 do not exist in
this platform and anything touching them is silently absent. It must not ship
that way.

**One thing Renode got right for free here:** eDMA4's TCD stride is **0x8000**
(not the 0x1000 of RT700 and of Renode's default) — `channelSize` is a
constructor parameter settable from the `.repl`, so the right stride needed a
number, not a code change. Verified against the oracle's `edma_cfg[]` table,
which cites `PERI_DMA4.h`.

### Status
M2 is **blocked on deferring DONE**, which is the known-shape fix. Not yet
attempted, so nothing is claimed about how hard it is in Renode — that number is
the next data point, and it is the one the whole authoring-effort axis turns on.


---

## M2 progress after the eDMA fix — dual-core boot WORKS, M2 does **NOT** pass yet

### The eDMA fix: cost MEASURED
The pre-registered prediction having been confirmed, the fix was applied and it
works: the M33 now runs **past** `InitCM7DMA` (PC moved from `0xffe1abc`, inside
the DONE poll, to the next blocker entirely).

**Shape of the fix:** Renode's own `NXP_eDMA` + `NXP_eDMA_Channels` copied into
the project, renamed, with the load-bearing change — `channelDone.Value = true`
moved out of the START `writeCallback` onto
`machine.ScheduleAction(1 µs, _ => channelDone.Value = true)`.

| metric | value |
|---|---|
| **functional diff vs upstream** | **1 statement moved** (+ a machine reference to schedule from) |
| total diff lines incl. comments | 32 |
| deferral used | 1 µs of virtual time; the VALUE is not load-bearing |
| files | 1 (`IMXRT1180_eDMA4.cs`) |

@rt1180emulator's guidance made this a one-shot rather than a search, and it was
**MEASURED from their model**, not remembered: their fix uses ONE device-level
timer, the byte-rate (`EDMA_SW_NS_PER_BYTE=1`) is *"NOT asserted by any test —
only the ORDERING property is"*, and their 1000 ns floor is wall-clock
robustness that a deterministic-time emulator does not need. So the honest
comparison is: **their fix carried non-icount robustness mine does not need, and
mine is correspondingly smaller.** Not a tool win — a requirements difference,
and it would be dishonest to score it otherwise.

⚠ **One Renode friction point worth recording:** `i @file.cs` compiles each file
as its **own assembly**, so two mutually-referential classes cannot be split
across files. `NXP_eDMA` and `NXP_eDMA_Channels` had to be merged into a single
`.cs`. Cost: minutes, not hours — but it is a real constraint on the
runtime-compilation workflow that otherwise makes iteration fast.

### What now works — the dual-core boot chain
```
Hello World from the Primary Core!
Copy Secondary core image to address: 0x303c0000, size: 13372
Starting Secondary core.
```
and, from the platform's own logging:
```
src:               SRC.SCR.BT_RELEASE_M7 set -- gate 1 open (M7 reset released, still held by CPUWAIT)
blkctrl_s_aonmix:  M7_CFG.WAIT cleared     -- gate 2 open (INITVTOR=0x0)
src:               BOTH GATES OPEN -- starting the Cortex-M7 at vector table 0x00000000
src:               M7 initial SP=0x20040000 PC=0x000004f8
```
The M7's initial SP is the top of its 256 KiB DTCM and its PC is inside its
ITCM — i.e. **the two-gate release, the per-core memory views, and the M33's
eDMA copy of the M7 image are all correct.** `INITVTOR=0x0` is right, not a bug:
the M7 boots from its *local* ITCM view at 0, not the `0x303C0000` system alias.

### ⛔ Where it stops: the M7 HardFaults during its own init
- M7 final PC `0x2b9e`, inside `DefaultISR`, which calls
  `MCMGR_TriggerEvent(..., 3)` — `kMCMGR_RemoteExceptionEvent` — then spins.
- **`XPSR = 0x01000003` → IPSR = 3 → HardFault.** Measured, not inferred.
- The M33 therefore waits forever in `MCMGR_StartCore` (PC `0xffe668e`), polling
  the shared status byte the M7 never sets.

**M2 is NOT passing.** The oracle string
`The secondary core application has been started.` does not appear, and nothing
here should be read as if it does. The boot *chain* works; the M7's *application
init* does not yet.

Not yet isolated: the faulting instruction. The M7's symbols are not loaded
(its image is `incbin`'d into the primary and copied by firmware, so Renode
never sees an M7 ELF), and `LogFunctionNames` produced nothing for `cpu1` even
after `LoadSymbolsFrom`. Manual exception-frame decoding from the M7 stack did
not yield a plausible frame either. **Recorded as unresolved rather than
guessed at.**

### M7 HardFault — hypothesis testing (FPU hypothesis FALSIFIED)
@rt1180emulator's primary hypothesis was that Renode had instantiated the M7
without an FPU, so the first FP op or FP exception-stacking would raise a
UsageFault escalating to HardFault. **Tested and falsified:**

| check | result |
|---|---|
| Is the M7 image hard-float? | **Yes** — `Tag_ABI_VFP_args: VFP registers`, `Tag_FP_arch: FPv5/FP-D16` |
| Does Renode enable the FPU? | **Yes** — `[DEBUG] nvic1: Enabling FPU.` (NVIC turns it on from the CPACR write in SystemInit) |
| FP instructions in the image? | **Zero** (`objdump` scan for vldr/vstr/vmov/vmrs/vcvt/vadd.f/vmul.f finds none in any function) |

So the FPU is present, enabled, and unused. Not the cause. They also eliminated
RTWDOG definitively — the M7's SystemInit watchdog-disable is fire-and-forget
and **polls nothing**, so unmapped RTWDOGs cannot hang or fault it — and ruled
out cache maintenance as a functional no-op.

### What IS established about the fault
- M7 PC trajectory, sampled: `0x0` (held) → `0x4f8` (`Reset_Handler`) →
  `0x56e` → `0x2b9e` (`DefaultISR`).
- `0x56e` is the back-edge of a stack/BSS zeroing loop
  (`0x2003FC00`..`0x20040000`), followed by `cpsie i` then `blx` to SystemInit.
- `XPSR = 0x01000003` → **IPSR = 3 = HardFault** (not an external IRQ, which
  would be IPSR ≥ 16).

⚠ **`0x56e` is NOT claimed as the fault site.** The sampling interval is ~20 µs
of virtual time — thousands of instructions — and the M7 is independently known
to reach much later code (RTWDOG writes at PC `0x11FE`+ appear in the access
log). `0x56e` is only the last PC *sampled* before `DefaultISR`. Concluding the
fault is there would be exactly the instrument error this project keeps
catching: a coarse instrument producing a confident, plausible, wrong answer.

Instruments that did NOT work, recorded so the next attempt skips them:
`cpu1 LogFunctionNames` (no symbols — the M7 image is `incbin`'d and
firmware-copied, so no M7 ELF is ever loaded into the machine),
`sysbus LoadSymbolsFrom <m7.elf> cpu1` (rejected),
`cpu1 CreateExecutionTracer` (syntax rejected), and manual exception-frame
decoding off the M7 stack (no plausible frame at the expected offsets).

**Next step: a differential trace.** @rt1180emulator's M7 boots CLEAN in their
model (their scorecard PASS requires the M7 to send app-ready over the MU, which
a faulting M7 cannot do), and they offered to trace its clean trajectory from
reset to app-ready. Diffing that against the faulting run locates the divergence
without either side guessing. Accepted.


#### Two further eliminations, and one open lead
**Eliminated — a missing peripheral is NOT the cause.** Collected every
unmapped address the M7 touches across a full run: it is *only* the five
RTWDOGs (`0x442D0000`, `0x442E0000`, `0x42490000`, `0x424A0000`, `0x424B0000`),
which @rt1180emulator eliminated definitively (the M7's SystemInit
watchdog-disable is fire-and-forget and polls nothing). The M7 touches **no
other MMIO at all** before faulting — notably it never reaches `BOARD_InitPins`
(no IOMUXC accesses from `cpu1`). So the fault is on a memory access or an
instruction, not a peripheral.

**Ablation attempted and inconclusive:** setting `numberOfMPURegions: 0` on the
M7 to test MPU involvement does not isolate anything — the firmware programs
MPU regions unconditionally, so the machine just aborts earlier on
"non-existent MPU region". Reverted to 16. Recorded because a failed ablation
is still a result, and the next person should not repeat it.

**Open lead (UNVERIFIED — not a claim):** Renode's `NVIC.cs` handles `MPU_CTRL`
by forwarding only bit 0 (ENABLE) to the CPU:
```csharp
case RegistersV7.Control:
    if((mpuControlRegister & 0x1) != (value & 0x1)) { this.cpu.MPUEnabled = (value & 0x1) != 0x0; }
    mpuControlRegister = value;          // PRIVDEFENA / HFNMIENA stored, not forwarded
```
The M7's `BOARD_ConfigMPU` ends with
`ARM_MPU_Enable(MPU_CTRL_PRIVDEFENA_Msk | MPU_CTRL_HFNMIENA_Msk)`. If
PRIVDEFENA (the privileged background region) is not honoured, a privileged
access outside every configured region would fault where hardware permits it —
which would escalate to the measured HardFault and is M7-specific, since the
M33 takes the ARMv8-M MPU path. **This is a reading of the source, not a
reproduction.** The M7's region 3 covers 0–2 GB with full access, which argues
*against* it. Listed as a lead for the differential trace to confirm or kill,
not as a finding.


### ⭐⭐⭐ M7 HardFault ROOT-CAUSED: Renode applies MPU checks to the Private Peripheral Bus

@rt1180emulator's div-by-zero hypothesis was **wrong**, but the *instrument* they
handed over — read `SCB->CFSR` (`0xE000ED28`), which needs no symbols — is what
cracked it. Their framing was right: "CFSR tells you the fault CLASS
definitively regardless of whether my bet is right."

#### Measured
| register | M7 (`cpu1`) | meaning |
|---|---|---|
| `CFSR` `0xE000ED28` | **`0x00000082`** | MemManage: bit 1 `DACCVIOL` (data access violation) + bit 7 `MMARVALID` |
| `MMFAR` `0xE000ED34` | **`0xE000ED24`** | the faulting address = **`SCB->SHCSR`** |
| `HFSR` `0xE000ED2C` | `0x40000000` | bit 30 `FORCED` — escalated to HardFault |
| `MPU_CTRL` `0xE000ED94` | `0x00000007` | `ENABLE` \| `HFNMIENA` \| `PRIVDEFENA` — the enable **succeeded** |

Not div-by-zero (and Renode in fact *filters* the `DIV_0_TRP` write outright:
`nvic1: Writing to CCR.DIV_0_TRP, but FilterCcrDiv0Write is set`, so that trap is
never armed here). It is an **MPU data-access violation on a PPB address.**

#### Mechanism
CMSIS `ARM_MPU_Enable()` is two accesses:
```c
MPU->CTRL = MPU_Control | MPU_CTRL_ENABLE_Msk;   // 0xE000ED94  <- succeeds (CTRL reads 0x7)
SCB->SHCSR |= SCB_SHCSR_MEMFAULTENA_Msk;         // 0xE000ED24  <- FAULTS
```
The MPU goes live on the first write; the *very next* access is a
read-modify-write of `SCB->SHCSR` at `0xE000ED24`, inside the Private
Peripheral Bus (`0xE0000000`–`0xE00FFFFF`). Renode subjects that access to MPU
region permissions and denies it.

**Per the ARMv7-M architecture the MPU is not applied to the PPB** — those
accesses always use the default system address map. So this is a Renode defect,
not a firmware or platform error.

There is a bleak elegance to it: **the access that faults is the very write that
would have enabled the MemManage handler.** With `MEMFAULTENA` still clear, the
MemManage fault escalates straight to HardFault (`HFSR.FORCED`), which is the
`IPSR = 3` measured earlier.

#### ⭐ Controlled differential — this is what isolates it to Renode
Both cores execute the **identical** `ARM_MPU_Enable` sequence, in the same run,
and both end up with `MPU_CTRL = 0x00000007`:

| | M33 (`cpu`, ARMv8-M MPU) | M7 (`cpu1`, ARMv7-M MPU) |
|---|---|---|
| `MPU_CTRL` | `0x00000007` | `0x00000007` |
| `SHCSR` | **`0x00010000`** — `MEMFAULTENA` set, write **succeeded** | faulted; `MMFAR` = `0xE000ED24` |
| `CFSR` | **`0x00000000`** — no fault | `0x00000082` |

Same instruction sequence, same MPU control value, same PPB target — **one core
completes it and the other faults.** That isolates the defect to Renode's
**ARMv7-M** MPU path specifically; the ARMv8-M path handles the PPB correctly.
It also explains cleanly why M0 (M33-only) was never affected.

#### Workaround: attempted, FAILED, recorded as such
Forcing `cpu1 MPUEnabled false` from the monitor does not help — the fault
occurs within the first scheduling window after the M7 is released, before any
injection can land, and re-injecting across 60 intervals changed nothing
(identical `CFSR`/`MMFAR`). The MPU check lives in tlib (native), so it is not
reachable from the runtime-compiled C# that made every other fix in this project
cheap. **No platform-side workaround was found.** This is an upstream fix or a
tlib patch.

#### Confirmed in tlib source — TWO independent defects, either alone sufficient
tlib (Antmicro's QEMU-TCG fork) cloned and read. The chain is now confirmed
end-to-end from both the producing and consuming side:

1. Firmware: `ARM_MPU_Enable(PRIVDEFENA | HFNMIENA)` → `MPU_CTRL = 0x7`.
2. Renode `NVIC.cs` forwards **only bit 0** to the CPU
   (`cpu.MPUEnabled = (value & 0x1) != 0`); `PRIVDEFENA` is dropped.
3. tlib `arch/arm/arch_exports.c:418` — the MPU-enable export toggles **only
   bit 0** of `c1_sys`:
   ```c
   if(!!enabled != (cpu->cp15.c1_sys & 1)) { cpu->cp15.c1_sys ^= 1; }
   ```
   so `c1_sys` bit 17 (**BR**, Background Region — the PRIVDEFENA equivalent) is
   **never set**.
4. Next access is `SCB->SHCSR` @ `0xE000ED24`, in the PPB. No enabled MPU region
   covers it.
5. tlib `arch/arm/helper.c:2825` `get_phys_addr_mpu()` scans the regions, finds
   none, and takes the background path at `:2890`:
   ```c
   if(is_user || !(env->cp15.c1_sys & (1 << 17 /* BR, Background Region */))) {
       background_result = MPU_BACKGROUND_FAULT;     // <- taken: BR never set
   } else {
       background_result = pmsav7_check_default_mapping(address, prot, access_type);
   }
   ```
6. → MemManage `DACCVIOL`, `MMFAR = 0xE000ED24`, `MEMFAULTENA` still clear →
   escalate → HardFault.

**Defect A — missing PPB short-circuit.** `get_phys_addr_mpu()` has **no** PPB
guard before the region scan. QEMU does, in the equivalent function:
`target/arm/ptw.c:2588` `m_is_ppb_region()` and `:2646` inside
`get_phys_addr_pmsav7()`, which short-circuits to the default map *before* the
region loop (the ARMv8-M path has the same guard at `:2894`).

**Defect B — `PRIVDEFENA` never propagated.** Renode's NVIC drops it and tlib's
export cannot receive it, so `c1_sys[17]` is always 0 and *every* access outside
an enabled region background-faults — not just PPB ones.

Either fix alone resolves this case; both are small. Defect A is the
architecturally correct one (the PPB must bypass the MPU regardless of
PRIVDEFENA) and QEMU's `ptw.c` is a direct template.

**Why the M33 is unaffected, confirmed structurally:** ARMv8-M uses an entirely
different lookup (`pmsav8_get_region()`, `helper.c:3020`), not
`get_phys_addr_mpu()`. That is exactly the measured differential, now explained
rather than merely observed.

**My earlier "open lead (UNVERIFIED)" about `PRIVDEFENA` was correct.** It was
recorded as a lead rather than a finding because at the time I had only read the
*producing* side (`NVIC.cs`); reading the *consuming* side (tlib) is what turned
it into a confirmed defect. Holding it as a lead was the right call — the region-3
0–2 GB argument really did make it look unlikely, and it turns out region 3 is
irrelevant because the *background* path is what fires.


#### The asymmetry this exposes — sharpened
⚠️ **Correction to my own first framing.** I initially wrote that QEMU lacks this
bug because "the CPU and the devices are the same kind of code." That is not the
reason, and @rt1180emulator corrected it: QEMU lacks the bug because its ARMv7-M
MPU implementation explicitly special-cases the PPB (`ptw.c:2646`). The causes
are unrelated.

**The broader point stands, and is the real finding.** The defect lives in the
native CPU core. In QEMU it would be an ordinary in-tree edit to
`target/arm/ptw.c` plus a rebuild — same tree, same toolchain as every device
model. In Renode, the runtime-compiled C# platform layer that made ANADIG, S3MU,
CCM, XCACHE, FlexSPI, TRDC, DCDC, SRC *and* the eDMA DONE fix minutes-each
**cannot reach tlib at all**. Renode's fast-iteration authoring advantage has a
hard floor at the tlib boundary, and CPU-core fidelity gaps fall below it.
This is the first platform-unfixable defect in the comparison, and it cuts
against Renode.

#### Status
**M2 remains blocked — but with a source-confirmed cause and a concrete patch
shape, which is a result, not a failure.**
Everything up to and including the two-gate release, the per-core memory views,
the M33's eDMA image copy, and the M7's own SystemInit works. The M7 dies on the
first PPB access after enabling its MPU.


### Peripherals added for M2 (all ported from the oracle, none re-derived)
| block | base | why it was needed |
|---|---|---|
| eDMA4 (patched) | `0x42000000` / TCD `0x42010000` | the DONE-deferral fix; TCD stride **0x8000**, not 0x1000 |
| TRDC1/2/3 | `0x44270000`, `0x42460000`, `0x42810000` | `TRDC_SetProcessorDomainAssignment` asserts on zero master/domain counts |
| DCDC | `0x44520000` | `DCDC_SetVDD1P0BuckModeTargetVoltage` spins on `REG0[STS_DC_OK]` (bit 31) |
| BLK_CTRL_WAKEUPMIX | `0x42420000` | closes one of the two logged-and-unmapped gaps |
| SRC + BLK_CTRL_S_AONMIX | `0x44460000`, `0x444F0000` | **the two-gate M7 release** |
| MU1 (reused as-is) | MUA `0x44220000` / MUB `0x44230000` | inter-core mailbox — Renode's `IMXRT700_MessagingUnit`, zero changes |

### Findings from this stretch
1. **⭐ `DMA.NXP_eDMA` caps at 32 channels; eDMA4 has 64.** Structural
   (`MP_INT`/`MP_HRS` are single 32-bit registers). Platform declares 32 as a
   **labelled deviation**, valid only because `InitCM7DMA` uses channel 0.
2. **⭐ Renode's `CortexM` defaults to 8 MPU regions.** The SDK programs region
   12 on both cores, so the M7 aborted with *"Trying to use non-existent MPU
   region… faulting region number: 11"*. Set to 16 on both cores. This is
   **DERIVED and labelled as such**: the architecture allows only 8 or 16, the
   firmware uses 12, therefore 16. CMSIS gives `__MPU_PRESENT` but no count, so
   it cannot be SOURCED.
3. **A second CPU runs by default in Renode.** The M7 must be explicitly parked
   (`cpu1 IsHalted true`) or it executes the M33's image off the shared bus —
   observed directly as eDMA writes tagged `[cpu1: 0xFFE1AB4]`, i.e. the M7
   running `InitCM7DMA` concurrently with the M33. **This is the AMP analogue of
   the XCACHE "advanced for the wrong reason" trap**, and it is the kind of
   thing a "does it boot?" check never catches.
4. **Reading a secondary core's vector table needs its CPU context.** `INITVTOR`
   is a *local-view* address (`0x0`), and the M7's ITCM there is a `cpu1`-scoped
   registration invisible on the global bus.
   `sysbus.ReadDoubleWord(vtor, cm)` — without the context you silently read
   garbage and boot the core on it.
5. **A correction to the oracle, offered back:** @rt1180emulator said DCDC
   *"writes happen but don't gate on a status poll — a plain RW/catch-all
   sufficed."* Their own tree in fact contains
   `imxrt1180_add_rdy_bit(s, "dcdc", 0x44520000, 0x1000, 0, 0x80000000)`, a
   ready-bit responder for exactly this poll. On the multicore path it **does**
   gate: `DCDC_SetVDD1P0BuckModeTargetVoltage` spins on `REG0[STS_DC_OK]`.


---

## ⭐⭐⭐ tlib PATCHED — the PPB defect is FIXED, and the M7 now runs its application

The M2 blocker was root-caused to two tlib defects. **Defect A (the missing PPB
short-circuit) has been patched, built, and installed, and it works.**

### The patch
`arch/arm/helper.c`, in `get_phys_addr_mpu()`, **before** the region scan:
```c
#ifdef TARGET_PROTO_ARM_M
    /* M-profile: the MPU is NOT applied to the PPB (0xE0000000-0xE00FFFFF). */
    if(address >= 0xE0000000 && address <= 0xE00FFFFF) {
        *prot = PAGE_READ | PAGE_WRITE;   /* privileged RW, never executable */
        return !(*prot & (1 << access_type));
    }
#endif
```
Guarded by `TARGET_PROTO_ARM_M` (set by `TARGET_ARCH=arm-m`), so it cannot
affect the A-profile ARM build. Functional change: **one 4-line guard.**

### Provenance discipline applied to the patch itself
- **The analysis was re-verified at the SHIPPED commit, not at HEAD.** Renode
  1.17.0 pins tlib `167decf9129758829762e582939128c0694e90d6` (found via
  renode → `src/Infrastructure` `066a7f13c052` → `src/Emulator/Cores/tlib`).
  My original reading was done on tlib master; both defects were re-confirmed
  at the pinned commit (`helper.c:2843` BR bit 17, `arch_exports.c:415-416`
  bit-0-only enable) before patching. A finding read off the wrong commit is a
  finding about the wrong binary.
- **The build is ABI-verified, not assumed.** A plain `cmake` build of tlib
  alone is **not** sufficient — it omits 75 `renode_external_attach__*` interop
  symbols that Renode's `src/Emulator/Cores` layer contributes. Building through
  `Cores/CMakeLists.txt` (which does `add_subdirectory(tlib)` and adds
  `renode/*.c`) produces `translate-arm-m-le.so` with an **exact symbol match**
  against the shipped binary: **0 missing, 0 extra.**
- **M0 was used as a positive control.** After installing the patched `.so`, M0
  still passes. That separates "my patch is wrong" from "my rebuild is
  incompatible" — the rebuild is sound.
- Original shipped library preserved at
  `platform-lib/linux-x64/translate-arm-m-le.so.orig-1.17.0`.

### Result: the M7 is transformed
| | before patch | after patch |
|---|---|---|
| MPU enable | MemManage `DACCVIOL` at `0xE000ED24` → HardFault | passes |
| `BOARD_ConfigMPU` | dies inside it | completes |
| I/D cache enable | never reached | reached (`nvic1` cache-maintenance writes) |
| `BOARD_InitPins` | never reached | **reached** — IOMUXC_AON writes at `0x443C0020/0024/0094/0098` tagged `[cpu1: 0x17F8/0x182A]` |
| MU1 | never reached | **reached** — MCMGR is exchanging messages |

The M7 went from "faults on the first PPB access after enabling its MPU" to
"runs board init, pins, and talks to the inter-core mailbox." **The oracle's
patch-shape prediction (`ptw.c:2588`/`:2646` as a template) was exactly right.**

### New blocker (M2 still not passing)
```
[WARNING] mu1: Reading the word from aInstance, but there is no transmit in progress
[WARNING] sysbus: [cpu: 0x2672] ReadByte from non existing peripheral at 0x2672.
[ERROR]   cpu: CPU abort [PC=0x2672]: Trying to execute code outside RAM or ROM at 0x00002672.
```
**The M33 branches to `0x2672`** — an address in the *M7's* image
(`MCMGR_FeedStartupDataEventHandler` in the cm7 ELF), which is unmapped from the
M33's view. So after the MU exchange, the M33 calls a function pointer that is
valid only in the M7's address space.

Leading suspicion, **unverified**: the MCMGR event/handler dispatch across the
MU. Note the MU model warns twice about reading `aInstance` with no transmit in
progress, which suggests the M33 is reading TR/RR content the model is not
supplying as real hardware would. This may be the PLAUSIBLE defect flagged in
the original survey — `IMXRT700_MessagingUnit` raises the GP doorbell IRQ
directly via `otherIRQ.Set()` while `UpdateInterrupts()` recomputes the line
from `ReceiveFullPending | TransmitEmptyPending` only, which does not include GP
status. **Not reproduced; recorded as the next thing to test.**

### Comparison note — this changes the tlib-boundary finding
The earlier finding stands *as a cost*, but it is now bounded: the tlib boundary
is **not impassable**, it is **more expensive**. Patching it required pinning
the exact submodule commit, discovering that tlib must be built through
Renode's `Cores` wrapper for ABI completeness, and a full native rebuild — none
of which the `.repl`/runtime-C# workflow needs. So the honest statement is:
*Renode's fast-iteration authoring advantage stops at the tlib boundary, and
below it the cost is comparable to QEMU's ordinary edit-and-rebuild loop* — not
"impossible", which is what "platform-unfixable" implied. **Correcting my own
earlier wording.**


---

# 🎉 M2 COMPLETE — BOTH ORACLES PASS

The headline rung of the experiment. Stock, unmodified NXP SDK images.

| example | oracle | result |
|---|---|---|
| `multicore_examples/multicore_manager` | `The secondary core application has been started.` | ✅ **PASS** |
| `multicore_examples/rpmsg_lite_pingpong` | `Message: Size=4, DATA = 101` | ✅ **PASS** |

Verified by `scripts/run_m2.sh` and `scripts/run_rpmsg.sh` (exit 0/1), each run
twice. `run_rpmsg.sh` deliberately matches the **terminal data value**, not the
`RPMsg demo ends` banner — the brief records that this example's *failure* form
also contains that banner, so banner-matching would produce a false PASS.

rpmsg ran the full ping-pong, 51 messages, 1 → 101, then `RPMsg demo ends`.

**Determinism:** `multicore_manager` reached its oracle at **virtual t = 4.01 s
on both runs** while host time varied. rpmsg likewise (14.1 / 14.4 ms).

## ⭐ The oracle's key prediction, independently reproduced
@rt1180emulator's most useful planning sentence was: *"rpmsg was FIRST-TRY, zero
model change — the boot chain was the whole cost; the transport was free on top
of it."*

**Confirmed exactly.** `rpmsg_lite_pingpong` was built and run with **no platform
change whatsoever** after `multicore_manager` passed — same `.repl`, same
peripherals, only a different ELF. It worked on the first attempt. Two
independent emulators, same finding: on this SoC the AMP *boot chain* is the
cost and the *transport* is free once the MU doorbell, shared OCRAM and both
cores' IRQ routing are correct.

## The three fixes that took M2 from blocked to passing

**1. The tlib PPB patch** (detailed above) — 4-line guard; without it the M7
HardFaults the instant it enables its MPU.

**2. ⚠ M33 DTCM was globally registered, so the M7 wrote into it.**
Both cores' images place `MCMGR_eventTable` at `0x200001a0` — in *their own*
TCM on silicon. My `dtcm` was registered on `sysbus` with no CPU restriction, so
it won for **both** cores and the M7's `MCMGR_Init` stores landed in the M33's
DTCM. Measured: the M33's table held the M7's handler addresses
(`0x260D`/`0x2673`) while the M7's own DTCM read back all zeros — and the M33
then *called* `0x2672`, an address unmapped in its own view, and aborted.
Fixed by scoping `dtcm` to `cpu` via `BusPointRegistration { cpu: cpu }`.

> **⚠ INSTRUMENT FAILURE WORTH RECORDING.** My first test of this wrote
> `0xAAAAAAAA` via `cpu` context and `0xBBBBBBBB` via `cpu1` context through the
> monitor, read both back, and got the two distinct values — "views correctly
> separated." **That was a FALSE NEGATIVE.** The monitor's context-qualified
> access does not resolve the same way as a CPU's actual execution-time access.
> I nearly concluded the platform was correct on the strength of it. What
> settled it was comparing the M33's table against the M7 DTCM's *system alias*
> (`0x30400000`), an address with only one possible backing store — an
> instrument that could not produce the wrong answer. **A probe that agrees with
> your hypothesis is not evidence until you know it could have disagreed.**

**3. CPU performance was ~8x too slow in virtual time.** Renode's default
(~100 MIPS) made the firmware's real `SDK_DelayAtLeastUs(1000000, 798000000)` —
a genuine 1-second delay, executed twice — consume >32 s of virtual time, which
looked exactly like a hang. Distinguished from a hang by **measuring the drain
rate of the countdown register** (`r0`: 517.0 M → 450.4 M → 383.9 M across
t = 0.5/2.5/4.5 s) rather than by staring at a stuck PC. Set
`PerformanceInMips` to 240 (M33) / 798 (M7).
*Labelled APPROXIMATION:* MIPS is instructions/second and this equates it with
core MHz, i.e. assumes IPC = 1. Values grounded in the SDK's
`DEFAULT_SYSTEM_CLOCK` (240000000 cm33 / 792000000 cm7) and the literal the M7
firmware itself passes (`0x2FAF0800` = 798000000). It affects only how fast
virtual time advances, not functional behaviour.


---

## ✅ M1 — self-checking driver examples: **PASS**

The brief asks for *one* deterministic, self-verifying example that prints a PASS
string. Examples were chosen **from the QEMU scorecard's corpus**
(`docs/validation/corpus.tsv`) rather than picked freely, so both tools are
judged by the identical external criterion — the string the firmware itself
emits, cited to SDK file:line.

*(The brief suggested a timer/compare example; the QEMU corpus contains no timer
example with an oracle, so one was not available for an apples-to-apples
comparison. eDMA and S3MU were chosen instead — see below for why they are
better choices here anyway.)*

| example | oracle | Renode | QEMU |
|---|---|---|---|
| `driver_examples/edma4/memory_to_memory` | `EDMA memory to memory example finish` | ✅ **PASS** | pass |
| `driver_examples/s3mu` | `End of Example with SUCCESS!!` | ✅ **PASS** | pass |
| `driver_examples/edma4/scatter_gather` | `EDMA scatter gather transfer example finish` | ✅ **PASS** (after the v5 eDMA signalling fix — it failed at the time this section was first written) | pass |

Verified by `scripts/run_m1.sh <elf> <oracle>` (exit 0/1).

### Why these two, specifically
They are not arbitrary: each **validates a block this project hand-built**, against
an external oracle rather than against my own expectations.
- `edma4/memory_to_memory` exercises the **patched eDMA** end-to-end via the
  interrupt-driven `EDMA_SubmitTransfer` path. It confirms the DONE-deferral fix
  did not break normal operation: destination buffer goes `0000` → `1234`.
- `s3mu` exercises the **S3MU/ELE port including its whitelist**. It passes, and
  **nothing was declined** — the example uses only `PING` (0x01), which is a
  whitelisted truthful command. Had it used an unmodelled crypto command, the
  model would have returned NON-SUCCESS and the example would have failed
  honestly rather than fabricating a result.

### Also wired on this rung
**eDMA4 grouped interrupts.** Verified against the QEMU oracle: *"eDMA4 (64
channels, grouped IRQ: channels 2k/2k+1/2k+32/2k+33 share IRQ 128+k)"* — 16 NVIC
lines, 128..143. Renode's `.repl` rejects both `[0,1] -> nvic@128` and
`[0-1] -> nvic@128` (range-to-single is not a valid connection); the working form
is one plain line per channel, `0 -> nvic@128`.

**TCM DMA-master aliases.** From the oracle
(`IMXRT1180_CODE_TCM_DMA_ALIAS` / `IMXRT1180_SYS_TCM_DMA_ALIAS`): a bus master
sees the CM33 TCMs at *different* addresses than the core does —
`CTCM 0x0FFE0000 → 0x201E0000`, `STCM 0x20000000 → 0x20200000`. Registered
globally (no `cpu:`), precisely because they exist for masters, not the core.
The oracle records this as the reason stock `edma4/scatter_gather` failed on
their side while every other eDMA example passed — it is the only one that puts
its TCD pool in `AT_QUICKACCESS_SECTION` (TCM). ⚠️ **CORRECTION to my first reading of this.** I initially wrote that the TCD
pool lands in OCRAM so the alias "is not what this build needs." **That was
wrong.** `tcdMemoryPoolPtr` is an *array* in `.data` at `0x20000080` — i.e. in
**DTCM** — and I had misread the first word of TCD[0] (`SADDR = 0x204C0000`,
which points at `srcAddr` in OCRAM) as the pool's own address. Measured: the
channel's `TCD[0].DLAST_SGA = 0x202000A0`, i.e. **the SDK converts the
scatter-gather pointer to the DMA alias**, exactly as the alias exists for. The
alias is load-bearing for this example, and without it the ESG fetch would read
nothing at all.

### `edma4/scatter_gather` — failed here at first; SOLVED (see "The DONE-signalling problem")
> **This subsection is kept as the working record of a live investigation.** Its
> conclusion — a Renode fidelity divergence — was **superseded**: the failure was
> my own eDMA patch, and v5 fixes it. Read it for the method, not the verdict.

The first **fidelity divergence** between the two tools in this experiment, and
it is recorded rather than quietly dropped.

**What is established (measured):**
- The example prints its banner and the pre-transfer buffer, then never finishes.
  Not a timeout — identical at 5 s and 20 s of virtual time.
- **The DMA transfer DOES happen.** Reading the destination buffer after the
  stall: `destAddr[0] = 0x00000001`, i.e. the data moved. What is lost is the
  **completion signalling**, not the transfer.
- `EDMA_DriverIRQHandler` is entered, and is observed reading
  `0x42118008` = eDMA4 **channel 33**'s register block. That is the grouped-IRQ
  scan doing exactly what the oracle documents (IRQ 128 covers channels
  0, 1, **32, 33**) — and channels 32-63 **do not exist** in this platform
  because of the labelled 32-channel deviation.

**What is NOT established:** that the 32-channel cap is the *cause*. Renode
returns 0 for the absent channels, which the handler should simply skip, so the
scan reaching channel 33 is suspicious but not demonstrated to be fatal. It may
instead be in the model's scatter-gather (ESG / `DLAST_SGA` next-TCD) handling,
possibly interacting with the DONE deferral. **Not diagnosed — recorded with its
evidence and left open.**

### ⭐ The DONE-signalling problem — diagnosed, then SOLVED (6/6)

**The tension.** Two pieces of stock NXP firmware impose contradictory
requirements on one bit:
- **`InitCM7DMA` (M2)** needs `CH_CSR[DONE]` **not yet set** when the START write
  returns — it W1C-clears a stale DONE, then polls for the real one.
- **`EDMA_HandleIRQ` (M1 eDMA examples)** needs `CH_CSR[DONE]` **already set**
  when the completion interrupt is taken: it samples
  `transfer_done = channelBase->CH_CSR & DMA_CH_CSR_DONE_MASK`
  (`fsl_edma.c:2604`) and passes it to the user callback, which sets its
  completion flag only `if (transferDone)`.

**Diagnosed by double-dissociation**, which is also what proved `scatter_gather`
was never a Renode defect:

| eDMA DONE handling | M2 `InitCM7DMA` | M1 `scatter_gather` |
|---|---|---|
| v1 — defer `channelDone` only | ✅ | ⛔ |
| stock Renode — fully synchronous | ⛔ | ✅ |
| v2/v3 — defer the whole `ExecuteTransfer` | ✅ | ⛔ (and breaks `memory_to_memory`) |
| **v5 — defer the SIGNALLING PAIR** | ✅ | ✅ |

**The solution — deferral GRANULARITY, not deferral amount.** Keep the transfer
synchronous (so Renode's `DmaEngine` initiator/master-ID capture is unaffected —
that is what v2/v3 broke), and move **both signalling actions into one scheduled
action, DONE first**:

```csharp
var raiseInterrupt = tcd.EnableInterruptIfMajorCounterComplete;
channels.machine.ScheduleAction(DoneDeferral, _ =>
{
    channelDone.Value = true;          // DONE first...
    if(raiseInterrupt)
    {
        interruptRequest.Value = true; // ...then CH_INT and the NVIC line
        UpdateInterrupts();
    }
});
```

This yields both properties at once: DONE is not set when the START write
returns, *and* DONE is set before the completion IRQ is delivered.

**Credit where it is due.** The structure was @rt1180emulator's suggestion —
their QEMU model does exactly this (`edma_sw_complete` → move data, set DONE,
set INT, raise the line, all in one deferred callback) — and they explicitly
challenged my "no configuration passes all six" claim as *"probably a statement
about the two configs tried, not about Renode."* They were right.

**One correction I contributed back:** their proposal gated the deferred block on
INTMAJOR. That loses M2 — `InitCM7DMA` polls DONE on a transfer with **no**
interrupt enabled. Measured: v4 did exactly that and M2 failed. **DONE must be
deferred unconditionally; only the interrupt half is conditional.** v5 fixes it.

**Result: all six oracles pass, reproduced on a second full pass.** No trade, no
caveat, no rung sacrificed.

### scatter_gather — ⚠️ MY "ESG reload drops fields" FINDING WAS WRONG (retracted)

I reported that Renode's ESG TCD reload preserved 32-bit fields and dropped all
16-bit ones (SOFF, ATTR, DOFF, CITER, CSR, BITER), built a field-by-field diff
table around it, and sent it to @rt1180emulator as a located Renode defect.

**It was an instrument artifact, and the defect does not exist.**

The eDMA TCD's 16-bit registers live in a `WordRegisterCollection`, separate from
the channel's `DoubleWordRegisterCollection`. I probed them with
`sysbus ReadDoubleWord`, which does not reach that collection and returns 0.
Re-read with `sysbus ReadWord`, the reloaded descriptor is **correct**:

| field | expected (source TCD[1]) | `ReadDoubleWord` (wrong) | `ReadWord` (true) |
|---|---|---|---|
| SOFF | `0x0004` | 0 | ✅ `0x0004` |
| CITER | `0x0001` | 0 | ✅ `0x0001` |
| CSR | `0x000A` | 0 | ✅ `0x000A` |
| BITER | `0x0001` | 0 | ✅ `0x0001` |

`CSR = 0x000A` is INTMAJOR set with ESG clear — the correct final descriptor —
and it matches @rt1180emulator's traced value from their own model
(`new tcd_csr=0x000a ... citer=1`) **exactly**. **The ESG reload is correct in
Renode**, the DMA alias is coherent, and the descriptor chain is intact.

**Independently confirmed from source.** @rt1180emulator supplied the exact TCD
byte layout (`SADDR u32@0x00, SOFF u16@0x04, ATTR u16@0x06, NBYTES u32@0x08,
SLAST u32@0x0C, DADDR u32@0x10, DOFF u16@0x14, CITER u16@0x16, DLAST u32@0x18,
CSR u16@0x1C, BITER u16@0x1E`) to audit Renode's packet struct against. Audited:
`doubleWords:5 bits 0-15` = DOFF, `bits 16-31` = CITER (split into its ELINK
sub-fields, correct for eDMA); `doubleWords:6` = DLAST_SGA u32;
`doubleWords:7 bits 0-15` = CSR as individual flags, `bits 16-31` = BITER.
**Renode's struct matches the hardware layout exactly — no mis-sizing, no
shifted tail.** So the retraction is confirmed twice over: by corrected
measurement and by source audit. There is no defect in the ESG decode.

> **⭐ The shared lesson, credited to @rt1180emulator**, who caught it in their own
> reasoning: *"My trace was right; my confirmation logic was wrong — I quoted your
> measurement back as if it were my ground truth."* They confirmed "defect #5" using
> a number I had supplied and never independently verified. **A cross-check is only
> independent if neither side has seen the other's number.** Two parties built a
> mutually-reinforcing loop out of one bad read, and only a different-width probe
> broke it.
>
> **⚠️ This is the third instrument failure of the session and the worst one.**
> The first two (a context-qualified memory probe that reported per-core
> separation that did not exist; a stuck PC that was an 8×-slow delay loop) were
> caught before they reached a conclusion. This one I built a diff table on and
> *published* to the oracle as a defect. What should have triggered suspicion:
> the "signature" was implausibly tidy — *every* 32-bit field surviving and
> *every* 16-bit field zeroed is far more likely to describe **the width of my
> probe** than the behaviour of a decoder. **A result that partitions perfectly
> along the axis of your own instrument is a fact about the instrument.**

### What remains unexplained
With the reload exonerated, the open question returns to the **first**
completion: TCD[0] carries INTMAJOR, `EDMA_DriverIRQHandler` is observed running,
and the channel is correctly reloaded and armed for TCD[1] (CITER=1) — yet
`g_Transfer_Done` never becomes true, so the example never issues its second
`EDMA_TriggerChannelStart` and TCD[1] never executes (`destAddr[4] = 0`).

That points at the **driver-visible completion bookkeeping** between the raised
interrupt and the user callback — `fsl_edma`'s scatter-gather handle state
(`tcdUsed` / `tcdIndex` / tail tracking) — rather than at the descriptor fetch.
**Not investigated. Stated as the open question, not as a finding.**

Eliminated so far, all by measurement: the 32-channel cap, completion
atomicity (v2 regressed and was reverted), INTMAJOR ordering (Renode matches
the oracle), DMA-alias coherence, and the ESG reload itself.

### scatter_gather — @rt1180emulator's atomicity hypothesis: TESTED, REJECTED
Their reading of my patch was **correct**: v1 deferred only `channelDone` while
`ExecuteTransfer()` — which also performs the ESG next-TCD reload *and* raises
INTMAJOR — still ran synchronously in the START write. Verified in my own source
(ESG reload at `IMXRT1180_eDMA4.cs:340`, `interruptRequest.Value = true` at
`:306/:445/:505`). Their model completes the whole minor loop atomically after
its timer fires, so the split cannot occur there. The hypothesis was well-formed
and the mechanism plausible.

**Their suggested fix — schedule `ExecuteTransfer()` itself, restoring
atomicity — was implemented and it made things worse.**

| | patch v1 (deferred flag) | patch v2 (deferred whole loop) |
|---|---|---|
| M0 `hello_world` | ✅ | ✅ |
| M1 `edma4/memory_to_memory` | ✅ | ⛔ **regressed** |
| M1 `s3mu` | ✅ | ✅ |
| M1 `edma4/scatter_gather` | ⛔ | ⛔ (still) |
| M2 `multicore_manager` | ✅ | ✅ |

v2 also produced a new warning — `edma4: Master ID for captured initiator not
available` — because running the transfer from a `ScheduleAction` callback loses
the initiating CPU context that Renode's `DmaEngine` attributes the access to.
The transfer still moved data (`destAddr[0] = 1`), but `memory_to_memory` stopped
completing.

**v2 was reverted; the tree is back on v1 and all five passing runs re-verified.**
A fix that trades a passing example for a still-failing one is not an
improvement, and shipping it because it came from a good hypothesis would be
exactly the wrong instinct.

**What this adds:** the completion-atomicity split is *not* the whole story, or
not the story at all — restoring atomicity does not fix `scatter_gather`. The
remaining suspects are Renode's `DmaEngine` initiator/master-ID handling and the
ESG reload path itself. @rt1180emulator offered to run their model with eDMA
tracing and hand over the exact DONE/INTMAJOR/ESG-reload sequence; that is now
the decisive next artifact and has been requested.


This does, however, turn the 32-channel cap from a theoretical note into a
**concrete risk**: RT1180's eDMA4 grouped IRQs mean the stock driver's shared
handler *always* probes channels 32-63, on every eDMA interrupt, in every
example.


## Rung log

| rung | Renode result | Renode effort | QEMU result | QEMU effort |
|---|---|---|---|---|
| env | ✅ v1.17.0 headless, Contiki positive control | MEASURED: ~4 min; 1 download + 1 extract + 1 smoke run | — | — |
| survey | ✅ LPUART + MU + eDMA models found & register-verified | MEASURED: ~15 min reading C# source | — | — |
| M0 `hello_world` | ✅ **PASS** — `hello world.` | **MEASURED:** ~10 min, 6 iterations, 5 peripherals, 987 lines | PASS, `hello world.` | **MEASURED (git):** effectively FREE once the SoC skeleton existed — no dedicated debug cycles findable in git. *The cost was the skeleton (cores+map+LPUART), not hello_world.* |
| M1 self-checking | ✅ **PASS** — `edma4/memory_to_memory`, `s3mu` **and** `edma4/scatter_gather` (3/3 after the v5 eDMA signalling fix) | MEASURED: ~25 min incl. eDMA grouped-IRQ wiring + TCM DMA aliases | PASS | **MEASURED (git):** incremental, no single hard blocker |
| M2 multicore | ✅ **PASS — both oracles.** `multicore_manager` → `The secondary core application has been started.`; `rpmsg_lite_pingpong` → `Message: Size=4, DATA = 101`. Required: a 4-line tlib patch, per-core DTCM scoping, and realistic CPU MIPS | MEASURED: prediction confirmed pre-run at PC `0xffe1abc`; 6 further peripherals ported; blocker now on the M7 side | PASS both oracles | **MEASURED (git):** ~10–11 mechanism commits and **9 distinct blockers** (below). Dominates total project cost. |

**@rt1180emulator's 9 M2 blockers, each found by chasing the next hang** (their MEASURED
list, from git; reproduced because it is my best available map of where M2 hurts):
1. M33-releases-M7 + inter-core MU model
2. CM7 per-core view (ITCM@0 / DTCM@0x20000000 / SoC-bg) + boot
3. DCDC / FBB / LPADC-cal unblocks + real LPADC VERID `0x02002C1B`
4. peripheral IRQs routed to the boot core
5. LPADC RESFIFO overflow — needed `-icount` to rate-match, **not** a tuning bug
6. CPUWAIT two-gate release (SCR + M7_CFG.WAIT) — `31a8c444c4`
7. **eDMA software-START virtual-time — `55aef3f0dd`** ← the one Renode also has
8. ELE kick-CM7 — `2dd07ed189`
9. reboot re-park the M7 held — `622319f30f` (found only by a later audit)

> ⭐ **`rpmsg_lite_pingpong` was FIRST-TRY, zero model change.** By the time the boot chain
> was right, the MU doorbell + shared OCRAM + both cores' IRQ routing already were too.
> **The boot chain was the whole cost; the transport was free on top of it.** This sharpens
> M2 for me: the rung to budget for is `multicore_manager`, not rpmsg.

### Provenance note on the QEMU effort column
Tagged **MEASURED (git)** because it is commit- and blocker-counts I verified against
their repository myself, not a remembered duration. @rt1180emulator was explicit and
honest about the limit: *"I do NOT have reliable wall-clock per rung — this spanned
multiple context windows. 'I don't remember precisely' is the true answer; use the
blocker-count as the MEASURED proxy, not a smoothed hour figure."*

**That refusal to supply a smooth number is the more useful answer, and it constrains the
final report: there will be NO wall-clock hours column for QEMU.** My Renode side has real
wall-clock, so wall-clock can never be compared across the two tools here — only
blocker-counts and commit-counts, which exist on both sides. A MEASURED Renode duration
set against a SOURCED QEMU recollection is exactly the mixed-tier comparison Law 1 forbids.

---

# 🧪 M3 (unplanned) — ZEPHYR: the first ORACLE-UNGUIDED fidelity test

Every register value in this model came from the QEMU oracle, and that oracle was
itself built against the NXP SDK. So "the SDK runs" is a weaker claim than it
looks: **it cannot distinguish a model that is right from one that is merely
SDK-shaped.** Zephyr is a wholly independent firmware stack — different drivers,
different init order, different register views — and nothing in this model was
built with reference to it.

| target | core | oracle | result |
|---|---|---|---|
| Zephyr `samples/hello_world` | CM33 | `Hello World! mimxrt1180_evk` | ✅ **PASS** |
| Zephyr `samples/synchronization` | CM33 | `thread_a: Hello World` | ✅ **PASS** |
| Zephyr `samples/hello_world` | **CM7** | `Hello World! mimxrt1180_evk` | ✅ **PASS** |
| Zephyr `samples/synchronization` | **CM7** | `thread_a: Hello World` | ✅ **PASS** |

```
*** Booting Zephyr OS build v4.4.0-5250-gc2d0717c4697 ***
Hello World! mimxrt1180_evk/mimxrt1189/cm33
```
```
thread_a: Hello World from cpu 0 on mimxrt1180_evk!
thread_b: Hello World from cpu 0 on mimxrt1180_evk!
thread_a: ...
```
Stock upstream Zephyr **4.4.99 (main)**, stock board target
`mimxrt1180_evk/mimxrt1189/cm33`, unmodified samples. Verified by
`scripts/run_zephyr.sh` (exit 0/1), reproduced.

`synchronization` is the stronger of the two: alternating `thread_a`/`thread_b`
requires **SysTick, the scheduler and thread context switching** to work, not
just a UART.

## ⭐ The result: ONE model fix was needed
**`S3MU was missing its secure alias.`** The SDK images use the non-secure view
(`0x47540000`); Zephyr's cm33 build runs **secure** and polls S3MU TSR at
`0x57540124`. Every other block in the platform already carried its
`+0x1000_0000` alias; S3MU alone did not, because nothing in the SDK corpus ever
touched it that way.

**That is exactly the class of gap an oracle-guided bring-up cannot surface** —
the oracle was built against the SDK, so the SDK could never reveal it. One
independent firmware stack found it in a single run.

**Everything else worked unchanged:** ANADIG, CCM, TRDC, DCDC, XCACHE, SRC,
LPUART1, the memory map, the MPU (via the tlib patch), and the NVIC — all
exercised by completely different driver code, all correct first time.

## The CM7 target — a second independent core, first try

Zephyr's `mimxrt1180_evk/mimxrt1189/cm7` target was run on a **new standalone
CM7 platform** (`mimxrt1189_cm7.repl`), where the M7 is the only core so its
*local* view is the global one: ITCM @ `0x0`, DTCM @ `0x20000000` (measured: ELF
LOAD at both, `_vector_table` at `0x0`). This complements
`mimxrt1189_m2.repl`, where those same TCMs are cpu1-scoped aliases of the
system-view block at `0x303C0000`.

**It worked first try**, with one genuinely new peripheral:
**the CM7 console is `lpuart12`, not `lpuart1`** (`zephyr,console = &lpuart12`).
`LPUART12_BASE = 0x44580000`, `LPUART12_IRQn = 159` [cmsis] — and Renode's
`UART.NXP_LPUART` served it unchanged, exactly as it did LPUART1.

⚠️ **I predicted this would be a corpus-invisible gap on the QEMU side too. Wrong.**
@rt1180emulator instantiates **all twelve** LPUARTs, not just the console pair: a
prior session closed exactly that hole, when the other ten fell through to the
0-answering catch-all — *"an absent instance is not a free instance; it still
answers."* So a polled Zephyr-cm7 banner should work on their model unmodified.
Their one caveat is the general dual-core one, not a missing block: LPUART12's
IRQ 159 is wired to the M33 NVIC, so interrupt-driven M7 RX/TX would need the
line re-routed to the boot core. **Different mechanism from the S3MU-secure axis
— that was architecture, this is completeness — but the same lesson: the demo
corpus is not the coverage spec.**

What this validates independently of the SDK corpus: the **M7 core
configuration** (cortex-m7, 4 priority bits, 16 MPU regions), the **M7 local
memory view**, a **previously unmodelled UART instance**, and all nine ported
peripherals driven from the M7 side. The only unmapped accesses are the five
RTWDOGs — the fire-and-forget writes @rt1180emulator eliminated as non-blocking.

**Regression: 10/10 green** — six NXP SDK oracles + four Zephyr targets across
both cores.


## ⭐ Follow-on: blanket secure mirror vs per-peripheral aliasing — a robustness axis

@rt1180emulator checked their own S3MU empirically after my flag (QMP `xp` on a
paused machine) and found **no gap**: NS `0x47540000` and secure `0x57540000`
both read MU_VER `0x00000100`. The reason is architectural, not luck — their SoC
installs **one blanket secure mirror**:

```c
memory_region_init_alias(periph_secure, system_memory, 0x40000000, 0x10000000);
/* mapped at 0x50000000 -- every NS peripheral gets its +0x1000_0000 alias free */
```

**This is a genuine fidelity-robustness axis, and my model was on the wrong side
of it.** Per-peripheral aliasing is structurally prone to omitting the secure
alias of any block the corpus never touches secure-side — which is *exactly* how
S3MU slipped through: every NXP SDK example runs non-secure, so nothing could
reveal it until Zephyr ran the cm33 secure. A blanket mirror is immune **by
construction**, not by diligence.

**Adopted.** Renode has `sysbus Redirect from to size`, so the whole thing is one
line, replacing 13 per-peripheral secure registrations:

```
sysbus Redirect 0x50000000 0x40000000 0x10000000
```

Verified with the *same probe* @rt1180emulator used, so the two models are
directly comparable:

| register | QEMU | Renode (after the mirror) |
|---|---|---|
| S3MU `MU_VER` NS `0x47540000` | `0x00000100` | `0x00000100` |
| S3MU `MU_VER` secure `0x57540000` | `0x00000100` | `0x00000100` |
| LPUART1 `STAT` NS `0x44380014` | — | `0x00400000` |
| LPUART1 `STAT` secure `0x54380014` | — | `0x00400000` |

LPUART1 no longer has *any* explicit secure registration; it is reached purely
through the mirror. **Full regression re-run: 8/8 green.**

**For the write-up:** this is a case where the two tools' *structural* choices —
not their register tables — produced different robustness, and where the more
robust structure was cheaper to express in both. It is also the second time in
this project that the QEMU model's architecture, rather than its data, was the
thing worth copying (the first being the eDMA signalling-pair ordering).

⚠️ **Framed as a SHARED finding, at @rt1180emulator's correction.** My initial
write-up made this a one-way port — their architecture, my fix. They pushed back:
*"your S3MU-secure catch is what made me PROVE mine rather than assume it, so the
robustness axis is a shared finding, not a one-way port."* That is right and the
record should say so. The sequence was: my per-peripheral gap → found by Zephyr →
flagged to them → **they measured rather than assumed** (QMP `xp` on both aliases)
→ their blanket architecture surfaced → ported here. Neither half produces the
finding alone.


## Two HARNESS issues (not model defects), recorded so they are not confused
1. **Zephyr's XIP image puts a boot header before the vector table**
   (`CONFIG_IMAGE_VECTOR_TABLE_OFFSET = 0x1000`; `_vector_table` at
   `0x3800B000`, not at the `0x38000000` segment start). Renode's `LoadELF`
   guesses `VectorTableOffset` from the **lowest** LOAD address — here the RW
   data segment at `0x14000000` — so the core started at `PC = 0, SP = 0` and
   locked up at `0xEFFFFFFE`. `run_zephyr.sh` extracts VTOR/SP/PC from the ELF
   with `nm`/`objdump` and supplies them explicitly.
2. **Zephyr's default board target uses EXTERNAL memories** —
   `zephyr,sram = &hyperram0` (8 MiB @ `0x14000000`) and XIP from an external
   `w25q128jw` NOR (16 MiB @ `0x38000000`, the secure alias). Neither device is
   modelled. `mimxrt1189_zephyr.repl` maps the **storage** behind them so
   `LoadELF` can place the image, as a boot ROM would have. **This models memory,
   not the FlexSPI/HyperBus devices** — a real scope limit, stated in the file.

## Why this matters to the comparison
This is the strongest fidelity evidence in the project, and it is evidence the
ladder alone could not produce. M0–M2 show the model reproduces the oracle's
corpus; **Zephyr shows the model generalises past the corpus it was built
against.** A model that merely encoded SDK-specific behaviour would have failed
here in many places, not one.

It also lands on Renode's home turf: Zephyr CI runs on Renode upstream, and
`mimxrt1180_evk` has a Zephyr board but **no upstream Renode platform** — so this
`.repl` is a plausible upstream contribution.

**Zephyr-on-QEMU: confirmed untested.** @rt1180emulator's scorecard is entirely
NXP MCUXpresso SDK, zero Zephyr, and they declined to claim it —
*"Zephyr-on-QEMU is real follow-up work, not already in the bag — I won't claim
it until I've booted it."* So the Zephyr rows stay logged as a **Renode-only**
result and are **not** presented as a cross-tool comparison. Scoring a rung the
other tool was never asked to run would be trivially unfair, and the fact that
Renode got there first is a statement about what was attempted, not about
capability.


## 📊 Zephyr sweep — FINAL, on the TrustZone-corrected platform

**85 targets · 75 PASS · 4 FAIL · 6 build-fail.**
(`cm33`: 61 PASS / 4 FAIL — `cm7`: **14 PASS / 0 FAIL**.)

This supersedes two earlier tables, both measured on a defective harness or a
misconfigured core, and **neither may be cited**:
- the 12 s-budget run (46 PASS / 12 FAIL) — five "failures" were truncation;
- the "7-for-7 CM33 split" (51 PASS / 7 FAIL) — measured before
  `enableTrustZone: true`, which alone fixed `thread_apis`,
  `arm_hardfault_validation`, `arm_irq_advanced_features`,
  `mem_protect/protection`, `schedule_api` and `mutex_api`.

### The four remaining failures
| target | status | note |
|---|---|---|
| `tests/arch/arm/arm_mpu_wt` | **architectural, shared** | `test_wt_cache_invalidate` + `test_wt_dma_coherency` need a real write-through cache. Same class as QEMU's `driver_examples/cache` xfail — **a limit of coherent-memory emulation, not a defect in either tool.** |
| `tests/arch/arm/arm_interrupt` | **undiagnosed** | Now *runs*: Zephyr's fault handler works and reports `Wrong crash type got 16384 expected 3`. A fault **classification** question, not a delivery failure. Not named a defect. |
| `tests/arch/arm/arm_thread_swap` | **undiagnosed** | not investigated |
| `tests/subsys/debug/coredump` | **undiagnosed** | not investigated |

⭐ **Only one of the four is understood, and it is the one that is nobody's bug.**
The previous version of this section confidently described a "CM33 fault-delivery
defect" spanning five suites. Configuring the core correctly dissolved four of
them; what remains is two unexamined suites and one shared architectural limit.

### What 75 passing targets covers
Both cores, samples and ztest: `common`, `context`, `device`, `cache`,
`cleanup`, `early_sleep`, `sleep`, `condvar`, `events`, `fifo` (api+timeout),
`lifo`, `mbox`, `msgq`, `mem_slab`(×2), `k_heap`, `pipe`, `poll`, `queue`,
`semaphore`, `stack`, `thread_apis`, `thread_stack`, `timer_api`,
`timer_monotonic`, `workq`, `mutex_api`, `mutex_error_case`, `schedule_api`,
`mem_protect/protection`, `mem_protect/stackprot`, `cbprintf_package`,
`cbprintf_fp`, `cmsis_nn`, `cobs`, `fdtable`, `hash_function`, `hash_map`,
`heap`(×3), `json`, `linear_range`, `lockfree`, `mem_alloc`, `log_api`,
`arm_irq_vector_table`, `arm_custom_interrupt`, `arm_runtime_nmi`,
`arm_hardfault_validation`, `arm_irq_advanced_features`, plus `hello_world`,
`synchronization` and `philosophers` on both cores.

**A model whose peripherals were built entirely against the NXP SDK now runs an
independent RTOS's kernel test suite broadly, on both cores** — threads,
scheduling, timers, IPC, memory protection, faults and logging.

### Corpus hygiene, kept visible in the data
- **6 build-fails** are paths I wrote from memory that do not exist in Zephyr
  4.4.99. Kept as `build-fail` rather than deleted, so *"the model failed"* stays
  distinguishable from *"I asked for something that isn't there."*
- **Two oracle errors of mine**, both caught by reading the console rather than
  the corpus cell: `samples/philosophers` (prints `Demo Description`, not
  `Philosopher 0`) and `tests/lib/cbprintf_fp` (prints `Complete` — it is not a
  ztest suite). Both pass with correct oracles.
  **Assigning one oracle to a whole directory tree is unsafe.**


## ⭐ Sweep findings: an ARCHITECTURAL xfail both tools share, and a core-specific split

### `tests/arch/arm/arm_mpu_wt` — the same architectural limit QEMU documented
Fails on Renode with exactly two failing cases, and they name themselves:
```
START - test_wt_cache_invalidate    FAIL
START - test_wt_dma_coherency       FAIL
SUITE FAIL - 0.00% [arm_mpu_wt]: pass = 0, fail = 2
PROJECT EXECUTION FAILED
```
Both are **cache-coherency** tests: they require a real write-through data cache
so that stale data can be observed before a maintenance operation. Renode's
memory is coherent and cache maintenance is a correct no-op, so the tests'
premise never holds.

**This is the same class @rt1180emulator already documented as an architectural
xfail on QEMU**, for `driver_examples/cache`:
> *"ARCHITECTURAL: the demo requires a real write-back data cache to hide DMA
> data (checks memcmp DIFFERS before Invalidate/Clean). QEMU memory is coherent
> — the maintenance ops are correctly no-ops … so the demo's stale-cache premise
> never holds and it can't pass."*

**Two independently built emulators fail the same test class for the same
reason.** That is a genuine cross-tool agreement about where functional
emulation stops, and it is *not* a defect in either model — it is the boundary
of what a coherent-memory emulator can represent. It belongs in the comparison
as a shared limit, not as a score against either tool.

### A core-specific split: `arm_interrupt` PASSES on CM7, FAILS on CM33
| suite | CM33 | CM7 |
|---|---|---|
| `arm_interrupt` | ⛔ (stops at `test_arm_esf_collection`) | ✅ **PASS** |
| `arm_irq_vector_table` | ✅ | — |
| `arm_custom_interrupt` | ✅ | — |
| `arm_runtime_nmi` | ✅ | — |
| `arm_irq_advanced_features` | ⛔ | — |
| `arm_hardfault_validation` | ⛔ (stops at `test_arm_hardfault`) | — |
| `arm_mpu_wt` | ⛔ (architectural, above) | — |

The same suite passing on one core and failing on the other is a strong signal,
because it rules out everything shared: the peripheral models, the memory map,
the clock tree and the console are identical between the two runs. What differs
is the **core**: ARMv8-M with TrustZone (CM33, and my Zephyr CM33 image runs
from the *secure* XIP alias `0x38000000`) versus ARMv7-M without it (CM7, from
non-secure ITCM).

**Now diagnosed, and it is NOT the MPU patch.** Measured on the CM33
`arm_interrupt` run:

| register | value | meaning |
|---|---|---|
| `PC` | `0xEFFFFFFE` | **lockup** |
| `CFSR` | `0x00010000` | bit 16 **UNDEFINSTR** (UsageFault) |
| `HFSR` | `0x40000000` | `FORCED` — escalated |
| `MMFAR` | `0x00000000` | not a memory fault |

And the undefined instruction is **deliberate**. Zephyr's test executes
`udf #90` (`0xde5a`) on purpose to provoke a fault and then validates the
exception stack frame against a known register pattern —
`tests/arch/arm/arm_interrupt/src/arm_interrupt.c:82`:
```c
const uint16_t expected_fault_instruction = 0xde5a; /* udf #90 */
```

So UNDEFINSTR is the *expected* stimulus. **The defect is that Renode's
Cortex-M33 locks up instead of delivering the fault to Zephyr's handler** — and
the Cortex-M7 running the same suite handles it and passes. Since the peripheral
models, memory map, clock tree and console are identical between those two runs,
the difference is in the **core model**, not the platform.

Deliberately *not* attributed to the tlib MPU patch: `arm_irq_vector_table`,
`arm_custom_interrupt` and `arm_runtime_nmi` all pass on the same core with the
same patch, and the fault is UNDEFINSTR, not a MemManage violation.

⭐ **This is the single best candidate in the sweep for the QEMU delta study.**
It is a same-binary, same-architecture-invariant question — *does an ARMv8-M
model deliver a UsageFault from a deliberate `udf` to the guest handler, or lock
up?* — with a known-good control (the same suite passing on the M7). If QEMU's
CM33 passes it, the delta is located in Renode's core with no ambiguity.

⭐ **Why this matters to the QEMU comparison specifically:** @rt1180emulator
points out that QEMU codifies the PPB/MPU invariant my tlib patch adds, in the
shared TCG ancestor — `target/arm/ptw.c:2588` `m_is_ppb_region`, short-circuited
at `:2646` (pmsav7) and `:2894` (pmsav8). So if these ARM suites are ever run on
both models, they check the *same invariant* via a from-scratch patch on one
side and inherited upstream logic on the other — **two independent sources for
one property.** Those are their priority cm33 targets.


---

# 🧰 Test infrastructure — Robot suite, sweep harness, and the delta-study protocol

## `tests/rt1180.robot` — Renode's native test format
The whole ladder is now a Robot Framework suite run by `renode-test`
(`scripts/run_robot.sh`). **10 cases, 15.3 s for the lot**, against ~3 min for
the one-process-per-case shell runners — a single resident Renode serves every
case. Robot is also the format an upstream Renode contribution would want.

The shell runners are kept: they are the thing that can be pointed at a single
ELF during bring-up, and they were what produced every measured result above.

## `scripts/zephyr_sweep.sh` — coverage sweep with normalized capture
Per target: build → run → capture → record a row in
`results/zephyr-corpus.tsv` (deliberately the same shape as the QEMU model's
`docs/validation/corpus.tsv`). Resumable — rows already recorded are skipped.

**The captured console is normalized**: guest UART bytes only, emulator
timestamps stripped, and the Zephyr build hash masked to `<BUILD>` so a rebuild
cannot manufacture a spurious diff. That file is the unit of comparison.

## Artifact pinning — the precondition for the delta study
`~/.cache/rt1180-artifacts/` + `MANIFEST.sha256` holds every ELF both tools
should run. **If the two models run the same sha256, a console delta is a MODEL
delta; if they do not, the study measures nothing.** Handed to
@rt1180emulator with the three Zephyr harness gotchas.

## `scripts/compare_consoles.sh` — the study itself
Diffs normalized captures per target and emits `results/DELTA-STUDY.md`:
a verdict table (identical / N differing lines / one-tool-only) plus per-target
unified diffs. Zephyr-on-QEMU is not yet run, so every row currently reads
**renode only** — which is the honest state, not a placeholder to be quietly
filled in later.

## Three harness bugs found and fixed while building this
1. **The sweep silently stopped after the first entry and exited 0.** `west` and
   `renode` both read stdin, and inside `while read … done < file` they swallow
   the rest of the corpus. Now read on FD 3 with `</dev/null` on both. **A clean
   exit code hiding a truncated run is worse than a crash** — the first two
   attempts looked like completed sweeps.
2. **`samples/philosophers` was recorded FAIL when it runs perfectly.** My oracle
   string was invented (`Philosopher 0`); the sample actually prints
   `Demo Description` / `STARVING` with ANSI cursor positioning.
   **Oracle-wrong and model-failed must never share a cell in the corpus.**
3. **Every ztest suite failed for a platform reason, not a kernel one.** The
   Zephyr platform inherited only the CM33 base repl, which lacks TRDC, DCDC,
   SRC, WAKEUPMIX and GPC — blocks that live in the M1/M2 repls. `hello_world`
   never touches them; ztest init does. Added, and `tests/kernel/common` went
   from no-console-output to `PROJECT EXECUTION SUCCESSFUL`.
   *(Also: my first comment for `0x44470000` guessed "BLK_CTRL_NS_AONMIX"; the
   CMSIS header says **`GPC_CPU_CTRL`**. Corrected in the file.)*


---

# 🔍 The finding that outlives the RT1180

*Added at @rt1180emulator's suggestion, who called it "the truest thing in the
thread."*

**Scoreboard of the collaboration, stated plainly:**
- @rt1180emulator offered five hypotheses. **Four were wrong** (M7 FPU, div-by-zero
  trap, `CH_INT` status-bit split, DMA-alias incoherence) — each falsified here by
  a stated method. **The fifth, structural one was right**, and it closed the
  experiment at 6/6.
- I shipped **two wrong findings** that had to be retracted: an "ESG reload drops
  16-bit fields" defect that was a probe-width artifact, and "no configuration
  reproduces all six oracles," which was a claim about two configurations.
- **Three of my own instruments produced confident, plausible, wrong answers**: a
  context-qualified memory probe that reported per-core separation that did not
  exist; a stuck PC that was an 8×-slow delay loop; a doubleword read of
  word-backed registers.

**The hit rate on individual guesses was mediocre on both sides. The method is
what converged.**

What made the wrong answers cheap and the right one recognisable:
1. **Every hypothesis shipped with its own falsification route.** "Check whether
   the fault PC is an FP opcode." "Read CFSR — it gives the fault class whether or
   not my bet is right." A hypothesis you cannot kill in five minutes costs an
   afternoon.
2. **Pre-registration, including the falsifier.** The eDMA prediction was
   registered with "an accidental quantum-boundary pass must not be scored as a
   win" written down *before* the first run.
3. **An instrument that could have disagreed.** Every one of the three instrument
   failures was caught by switching to a probe that could produce the opposite
   answer — a system alias with one backing store, a drain-rate over an interval,
   a different-width read.
4. **A cross-check is only independent if neither side has seen the other's
   number.** We briefly built a mutually-reinforcing loop out of one bad
   measurement, each reading it back as confirmation. @rt1180emulator named it:
   *"My trace was right; my confirmation logic was wrong — I quoted your
   measurement back as if it were my ground truth."*
5. **Correcting in public, promptly, beat being right.** Six corrections were
   issued across the two sessions — three mine, three theirs. Every one of them
   shortened the path.

> **A result that partitions perfectly along the axis of your own instrument is a
> fact about the instrument.**

---

# 📒 Night-2 ledger — what is established, and at what scope

Agreed jointly with @rt1180emulator; each line states its own limit.

| result | evidence | scope |
|---|---|---|
| **cm7 Zephyr printf samples: two-tool byte agreement** | same sha256 binaries, normalized console diff | **samples only** — their ztest builds do not reach a verdict; scope banner emitted by the differ itself |
| **Renode defect #5 — ARMv8-M escalates an ENABLED UsageFault, then locks up** | (1) my same-binary M7-passes/M33-locks control, (2) my `SHCSR=0x0007000C` + `CFSR=UNDEFINSTR` + `HFSR.FORCED` read, (3) their from-scratch QEMU M33 bare-metal probe delivering the fault correctly | mechanism confirmed by two independent models; **not** a same-binary cm33 diff |
| **`arm_mpu_wt` ≡ their `driver_examples/cache` xfail** | both are cache-coherency premises that a coherent-memory emulator cannot satisfy | a **shared limit of functional emulation**, logged as a limit, never as a score |
| **Renode: 51/69 Zephyr targets to a verdict; all 7 failures CM33, zero CM7** | corrected 90 s sweep | Renode-only; **no QEMU column** |
| **Their cm7-ztest halt (0 bytes, 0 SysTicks, flat 40 s, `-icount`-invariant)** | their two-layer read: console *and* `-d int` exception trace | a real gap **on their side**, routed to Kyle; entry point = `svc`/SysTick on the M7 |

## Two rules this collaboration produced that outlive the RT1180

**1 — Never score a maturity gap as a capability gap.** Renode reaches a verdict
on 51 Zephyr targets and the QEMU model currently reaches none. That measures how
far each model's *Zephyr bring-up* has been taken — theirs is hours old and
harness-less, mine had a night and a dedicated rig — not what either tool can do.
The mirror image would be me claiming credit for the MU support Renode handed me
for free.

**2 — Different owners, different rules** (theirs, and better than mine).
They ran the bare-metal UsageFault probe because it **confirmed my defect**; they
declined the equally cheap `svc` probe because it would **diagnose their own gap**,
which was routed for a priority decision. I would have treated "it's cheap" as
sufficient justification. Cost is not the test — ownership is.

## ⚠️ A SIXTH failure — and it is NOT an instrument artifact. It is a control-scope error.

The five failures catalogued above all had one shape: *the tool partitioned on a
property of the measurement, and I read it as a property of the thing measured.*
**Defect #5's retraction is a different animal, and worth separating.**

**What happened.** Five CM33 Zephyr suites died at `fault.c:1051`,
`"ESF could not be retrieved successfully"` — Zephyr's `get_esf()` returning
NULL because `EXC_RETURN` bit 0 (ES, Exception Secure) was clear under
`CONFIG_ARM_SECURE_FIRMWARE=y`. **Cause: my `.repl` never set
`enableTrustZone`, and Renode's `CortexM` defaults it to false.** The RT1180's
M33 has TrustZone — the NS/secure `+0x1000_0000` alias scheme this whole
platform is built on *is* TrustZone — so on a core without it, every fault path
in secure firmware asserts before it begins. Setting `enableTrustZone: true`
removes the assert entirely and the full suite still passes 10/10.

**The reasoning error, stated precisely.** I had a control I was pleased with:
the *same binary* passes on the M7 and fails on the M33, with identical
peripherals, memory map, clock tree and console. That control is sound, and it
does exclude the platform. **But I read "the defect is in the core model" where
"the defect is in the core's configuration" was equally consistent** — and the
M7 has no TrustZone to misconfigure, so the control could never have separated
them.

> ⭐ **A control that localizes a fault to a component does NOT distinguish that
> component's implementation from its parameters.** It narrows the *place*; it
> says nothing about which *layer* at that place is wrong. Ask "did I configure
> this thing correctly?" before "is this thing broken?" — the prior on the former
> is much higher, and it costs one grep.

**@rt1180emulator drew the same inference from the same evidence**, and logged it
on their side: *"I was careful about same-binary-vs-independent, but NOT about
implementation-vs-configuration… two-source agreement on a diagnosis is only as
good as the diagnosis, and neither of us interrogated the config axis."*
**Two independent parties agreeing does not test an axis that neither of them
questioned.** That is the sharpest version of the lesson and it belongs next to
the "a cross-check is only independent if neither side has seen the other's
number" rule — they are the same failure at different scopes.

**Cost.** They wrote a bare-metal QEMU probe to second-source a defect that did
not exist. The probe's *result* stands (a QEMU CM33 does deliver the UsageFault);
it has been reframed in their tree as a standalone QEMU capability check, with
the false cross-tool attribution retracted in the file's own comments and in a
retraction commit — **unpushed, pending Kyle's gate, so nothing false left the
machine.** They also note the lesson transfers to their own open gap: they will
check the M7/secure-state *configuration* before attributing their ztest halt to
a QEMU core bug.

**What is unaffected:** the cm7 printf-samples byte agreement; `arm_mpu_wt` as a
shared architectural limit; the four surviving Renode defects. **What is void:**
the "7-for-7 CM33 split", measured on a misconfigured core, and defect #5 itself.

## The through-line: five instrument failures, one shape
Every one partitioned on a property of **the measurement** and was read as a
property of **the thing measured**:
1. a monitor probe reporting per-core memory separation that did not exist;
2. a stuck PC that was an 8×-slow delay loop;
3. a doubleword read of word-backed registers, "proving" a decoder dropped fields;
4. a whole-file console diff measuring **run duration** as content;
5. a 12 s sweep budget recording **truncation** as model failure.

Each was caught the same way: **by reading a different layer than the one that
produced the number** — the system alias instead of the context-qualified probe,
a drain rate instead of a snapshot, a word read instead of a doubleword, the
common prefix instead of the whole file, the console instead of the corpus cell.

⭐ And the counterintuitive one worth keeping: **correcting #5 made the result
stronger.** Removing the truncation noise deleted an entire imaginary failure
family and left a single coherent story — all seven remaining failures on the
CM33, zero on the CM7. The instinct is that a retraction costs you a finding. It
cost an artifact and revealed a result.

---

# 🎯 THE EQUIVALENCY PASS — scoring Renode against the QEMU model

Kyle's framing, verbatim: *"the roadmap is equivalency of function on renode and
qemu. e.g. mission is 'I want to do the exact same things (run the same software)
on a renode implementation of rt1180 as we can on a qemu implementation of
rt1180.'"*

That reframes the deliverable. Up to here the rungs were chosen by me (M0/M1/M2,
then Zephyr breadth). The equivalency target is **not mine to choose**: it is the
QEMU model's own corpus, `docs/validation/corpus.tsv` — 29 rows of real NXP SDK
firmware, each with an EXTERNAL oracle cited to SDK `file:line`. The score is
simply: *for each row that corpus runs, what does Renode do?*

## The one methodological rule that changed

Earlier sections compared my MEASURED Renode results against QEMU verdicts
**quoted** from the oracle's `SCORECARD.md`. That is a SOURCED-vs-MEASURED
comparison, which fleet Law 1 forbids in a headline. So the equivalency harness
(`scripts/equivalency.sh`) **runs qemu-system-arm itself**, on this box, on the
same pinned bytes, matching the same oracle string. Both columns are MEASURED by
the same harness in the same run. Nothing in the head-to-head table is quoted.

Corpus: `results/equivalency-corpus.tsv`. Output: `results/equivalency.tsv`,
consoles in `results/console-eq/`.

## ⭐ Renode defect #5 — `DMA.NXP_eDMA` discards the major-loop epilogue, then ABORTS THE PROCESS

**Severity: this one kills the emulator.** Not a wrong value — `rc=134`, a core
dump, on stock unmodified vendor firmware that QEMU passes.

Found by running `driver_examples/edma4/channel_link`, a row I had never run
because my eDMA coverage stopped at the two examples M1 needed.

`ExecuteTransfer()`'s major-loop epilogue computes three things into the local
`tcd` struct — the BITER→CITER reload, the SLAST adjustment to SADDR, and the
DLAST adjustment to DADDR — and then, on the **non-scatter-gather path only**,
returns without ever calling `UpdateTCDInLocalMemory(tcd)`. The ESG branch writes
back; the `else` branch forgets. Every minor loop re-fetches the TCD from the
register file, so the next activation reads the **stale CITER == 0** that the
last minor loop wrote, decrements it, and wraps.

MEASURED, with a trace instrument on `ExecuteMinorLoop`:

```
TRACE minorloop ch=0 citer=2 biter=2 elinkC=True majElink=True->2
TRACE minorloop ch=1 citer=1 biter=1 ...
TRACE minorloop ch=0 citer=1 biter=2 ...
TRACE minorloop ch=1 citer=0 biter=1 ...      <- reload was never committed
TRACE minorloop ch=1 citer=16383 ...          <- 0-- wrapped
TRACE minorloop ch=1 citer=16382 ...          ... 16383 spurious minor loops,
TRACE minorloop ch=1 citer=16381 ...              each one a real memory write
...
TCD-FIELD-OVERFLOW: CITER_ELINKYES ch=0 citerYes=65535 elinkC=True biterYes=2
ConstructionException: Value exceeds the size of the field   (CITER_ELINKYES is 9 bits)
-> UNHANDLED -> process abort, rc=134
```

Note the ordering: it is **silent-wrong before it is loud**. The runaway channel
performs thousands of unintended DMA writes into guest memory before the field
overflow finally trips an exception.

**The control that makes this a Renode defect and not mine.** I carry a patched
eDMA model (the v5 DONE-deferral). So I rebuilt the platform against the stock,
unpatched `DMA.NXP_eDMA` / `DMA.NXP_eDMA_Channels` that ship in Renode 1.17.0 —
the example uses channels 0–2, well inside the 32-channel cap that normally
forces my fork — and ran it:

```
rc=134
Unhandled exception. Antmicro.Renode.Exceptions.ConstructionException: Value exceeds the size of the field.
```

Identical abort, zero of my code involved. The crash site
(`UpdateTCDInLocalMemory`) is also byte-for-byte identical to upstream — I
diffed it before saying anything.

**Fix (v6):** commit the epilogue on the non-ESG path too. 1 line + comment.
All 8 eDMA4 examples then pass, scatter_gather (the ESG path v6 sits next to)
included.

## ⭐ Renode defect #6 — `LPI2C.MRDR` reports RXEMPTY for the byte it just returned

Upstream `S32K3XX_LowPowerInterIntegratedCircuit` defines MRDR bit 14
(`ReceiveEmpty`) as `rxQueue.Count == 0` — evaluated **after** the bit 0–7 data
field's provider has already dequeued. So the last byte of every FIFO drain comes
back tagged empty. MEASURED, stock prebuilt `demo_apps/bubble_peripheral` reading
the accelerometer's WHO_AM_I:

```
ReadUInt32 from 0x70 (ControllerReceiveData), returned 0x4086
                                                       ^^^^ ^^
                                          RXEMPTY=1 ---'    '--- the data byte 0x86
```

`fsl_lpi2c` correctly treats RXEMPTY=1 as "no valid data" and discards it — so
the driver never receives a byte it was in fact given. **257751 MRDR reads in
0.05 s of emulated time**, console silent. Single-byte reads can never succeed,
which is every register probe on every I2C device.

The QEMU oracle gets this right by testing emptiness **before** popping
(`hw/i2c/imxrt1180_lpi2c.c:295`, `if (s->rx_count == 0) return MRDR_RXEMPTY;`).

**Fix:** latch the emptiness of the fetch that produced the value, and report the
latch. `peripherals/IMXRT1180_LPI2C.cs`.

## What Renode gave for free here — the reuse win

`S32K3XX_LowPowerInterIntegratedCircuit` is NXP's LPI2C, the same IP the RT1180
carries. I verified the register map offset-by-offset against the oracle's
`hw/i2c/imxrt1180_lpi2c.c` **before** reusing it — VERID 0x00, PARAM 0x04, MCR
0x10, MSR 0x14, MIER 0x18, MDER 0x1C, MCFGR0 0x20, MCFGR3 0x2C, MDMR 0x40, MCCR0
0x48, MCCR1 0x50, MFCR 0x58, MFSR 0x5C, MTDR 0x60, MRDR 0x70 — identical, every
one. Six LPI2C instances for six `.repl` lines and zero C#, modulo defect #6.

That is the axis-3 (authoring effort) headline in miniature: **Renode's library
pays off exactly when a vendor reuses an IP block across SoC families, and its
bugs are inherited on the same terms.**

## A stale note in the QEMU corpus (routed to @rt1180emulator)

The oracle's `bubble_peripheral` row reads *"then polls the FXOS8700 accel over
LPI2C (sensor not modeled)"*. But `hw/arm/imxrt1180_soc.c:736` does
`i2c_slave_create_simple(s->lpi2c[1].bus, TYPE_FXLS8974, 0x19)`, and
`hw/sensor/fxls8974.c` is a real 145-line model. The part is an **FXLS8974**, not
an FXOS8700, and it *is* modelled — which is precisely why QEMU reaches the
banner. Their note understates their own coverage.

I ported that model to Renode (`peripherals/FXLS8974.cs`, 132 lines), carrying
over its honesty flag verbatim in intent: the axes report a fixed "board flat, at
rest" orientation (+1 g on Z), because there is no motion input. Flagged, not
faked.

**Result — the second byte-identical two-tool agreement of the project:**

```
        RENODE                                              QEMU
Welcome to the BUBBLE example                     Welcome to the BUBBLE example
You will see the LED brightness change...         You will see the LED brightness change...
x=  0 y =  0                                      x=  0 y =  0
x=  0 y =  0                                      x=  0 y =  0
```

## ⚠ And one harness error of mine, caught before it was published

My Tier A runner loaded every prebuilt `.bin` at `0x0FFE0000`. Three of the eight
— all the `usb_device_dfu` images — link at **`0x0FFF0000`**, reserving the low
64 KiB of CODE_TCM as the DFU update slot. Their reset PC landed 64 KiB past the
end of the loaded blob:

```
CPU abort [PC=0x10000000]: Trying to execute code outside RAM or ROM at 0x10000000.
```

I had this recorded as three Renode FAILs. It was three harness FAILs. The QEMU
model does not hardcode a base either — it derives it from the image's own reset
vector (`hw/arm/imxrt1180_evk.c`, the `!is_elf` branch: `cand = reset_pc &
0xFFFF0000`). The runner and the scorer now do the same:

| image | reset vector | derived base |
|---|---|---|
| hello_world, led_blinky, sai, bubble, multicore_trigger | `0x0FFE047D` | `0x0FFE0000` |
| the three usb_device_dfu images | `0x0FFF4F91` | `0x0FFF0000` |

**Caught by reading the oracle's loader before believing my own FAIL** — which is
the only reason it is in this section and not in a results table. The tally of
"every published finding that was wrong was wrong in my favour" survives because
this one was never published.

## Closing the USB gap — 3 rows for ~320 lines of C#

Best ratio in the gap map (3 corpus rows, 296-line reference model), so it went
first. Result: **equivalency 21/29 → 24/29 (72 % → 83 %)**.

`peripherals/IMXRT1180_USBPHY.cs` (136 lines, from the oracle's 196-line C) +
`peripherals/IMXRT1180_USB.cs` (181, from 296) + 4 `.repl` lines.

**The entire blocker for all three rows was one register.** MEASURED: 9555 reads
of `0x42CA00A0` — USBPHY1 `PLL_SIC` — in a single 5 s run, console silent,
nothing else polling. That is the SDK's
`while(0 == (USBPHY->PLL_SIC & PLL_LOCK)) {}`. Adding the PHY alone moved the
firmware from *silent* to *"USB device mouse failed"* — it reached the
application; the controller model then carried it to the banner.

### Two things I deliberately did not simplify away

Both are the oracle's lessons, carried over with their reasons, not just their
values:

1. **The PHY is born held in reset.** `CTRL` resets to `0xC000_0000` (SFTRST *and*
   CLKGATE set) and `PWD` to `0x001E1C00` (transmitters, receivers, bandgap all
   powered *down*). A zero-filled model is not simpler — it is a model of a board
   that has already booted. And because firmware read-modify-writes `PWD`, a
   zeroed one makes the guest read "everything is already powered up" and write
   that back as its own configuration. Their note records that their automated
   reset-value gate **refused** this row (the RM prints "See section" instead of a
   value) and nobody went back for it — *a refusal is not a check*.

2. **Two registers describing one resource must not disagree.** The endpoint count
   is reported by both `DCCPARAMS[DEN]` and `HWDEVICE[DEVEP]`. Both derive from one
   constant, and the C# constructor **throws** if `HWDEVICE != 0x11` (the RM's
   reset value), so an edit that made them contradict each other fails loudly
   instead of shipping a chip that disagrees with itself.

### The fidelity limit is declared, not smuggled

No USB host is attached and none is fabricated. `PORTSC1.CCS` is **clear** — a
driver polling for a connect is correctly observing an empty port, not waiting for
an event that cannot come. No transfer engine, so no endpoint completion or
interrupt is ever raised. The device completes init/run and stays un-enumerated.
That is exactly what QEMU does, which is why **both** tools stop at BANNER rather
than PASS — the agreement is on the honest outcome, not on a faked one.

### What this says about the authoring-effort axis

The USB port was ~320 lines of C# from ~490 lines of C, in one sitting, with zero
new Renode defects found. Contrast the two defects found the same session in
Antmicro's *own* shipped models (#5 eDMA, #6 LPI2C). **Porting a model whose
reference implementation you can read is cheap and low-risk; inheriting a model
someone else wrote is cheap right up until it isn't.** That is the honest shape of
Renode's library advantage — and it cuts both ways.

## Remaining gap to full equivalency (5 rows, ≈3700 lines of reference C)

| rows | block | reference |
|---:|---|---:|
| 1 | FlexSPI (mine is a deliberate stub that refuses to fabricate) | 790 |
| 1 | SAI1 + WM8962 + AUDIO PLL | 900 |
| 1 | ASRC (also needs SAI1) | 396 |
| 1 | NETC switch + ENETC + PHY | 1605 |
| 1 | cm7 `pmsm_enc` — needs a Renode **value-test**, not a peripheral | — |

NETC is 43 % of the remaining work for one row; it is the obvious last thing to do,
not the next thing.

## ⭐ The corpus blind-spot pattern — named after the third instance

Three times in this project a gap was invisible for the same structural reason,
and each time on a **different axis**:

| gap | what the corpus held constant | what revealed it |
|---|---|---|
| S3MU had no secure alias | every SDK example runs **non-secure** | Zephyr, which runs the cm33 secure |
| LPUART12 missing | every example uses **LPUART1** | the cm7 Zephyr target |
| **eDMA3 missing entirely** | every example the corpus ran used **eDMA4** | `sai/edma_transfer`, once its codec assert cleared |

Security state, peripheral instance, controller instance — three axes, one hole.

> **A corpus-driven coverage claim has structural blind spots along every axis the
> corpus happens to hold constant, and you cannot see them from inside the corpus.**
> Only a different driver, configuration, or tool reveals them.

The counter is not a better corpus — it is a modelling discipline: **model the full
set, not what the demo touches.** The oracle's platform does exactly that (all 12
LPUARTs, one blanket secure mirror, both eDMA instances), which is precisely why
all three of these landed as *my* platform gaps and not theirs. My equivalent on
the Renode side is per-instance declaration and reusing a whole IP model rather
than the subset one example exercises (six LPI2C instances, two USBPHY, two USB
OTG, now both eDMA controllers).

Note the symmetry that makes this checkable rather than rhetorical: because both
tools run the **identical bytes**, my measurement that `sai/edma_transfer` drives
`0x44010038` proves the oracle's eDMA3 is exercised by that row too. The same-binary
harness turns one tool's blind spot into a testable claim about the other's.

### And a correction to my own earlier characterisation

I have been describing Renode defect #3 as "`DMA.NXP_eDMA` hard-caps at 32
channels" — a flat limitation. Modelling both instances shows that is **too
broad**. eDMA3 has *exactly* 32 channels, so the cap costs nothing there; it is a
labelled deviation on **eDMA4 only** (64 channels). The defect is real but
**instance-specific**, and I could not have known that while modelling one instance.

## ⚠ An oracle error, caught only because the harness stopped quoting the oracle

@rt1180emulator wrote, unprompted:

> "FlexSPI: heads-up that mine is a DELIBERATE stub that refuses to fabricate IP-command
> results — it's an honest XFAIL on my scorecard too, so a Renode FlexSPI that
> also declines is AGREEMENT on the limit, not a delta."

**It is not.** Four independent checks say so:

| check | result |
|---|---|
| my MEASURED run of *their* `qemu-system-arm`, this box, that ELF | prints `Program data - successfully.` after a full erase/program cycle |
| my sweep row | `qemu=PASS renode=FAIL agree=NO` |
| *their* `corpus.tsv` | `kind=pass`, "full round trip … XIP read-back byte-exact" |
| *their* `SCORECARD.md` | **PASS** |

`hw/ssi/imxrt1180_flexspi.c` is 790 lines of LUT engine with a real `is25wp128`
flash behind it (`soc.c:605`).

**The deliberate stub is mine.** *"An M0-scope stub that refuses to fabricate"* is
my phrasing, for my FlexSPI, which I had sent them four times. It came back
attached to their model.

### Why this is the dangerous shape and not a typo

Accepting it would have reclassified a real 790-line gap as "agreement on an
honest limit" and closed the row — moving it from a red delta to a green
shared-limitation line. **Wrong, in my favour, sourced from the oracle, with my
own words as the evidence.**

This is structurally identical to the ESG incident earlier in the project: I sent
them a bad decoder finding, they began confirming it back using *my* number, and
only a different-width probe broke the loop. Two agents cross-checking each other
have a failure mode where a claim becomes "confirmed" purely by round-tripping.

> ⭐ **A CONFIRMATION MUST CITE AN INDEPENDENT MEASUREMENT, NOT AN AGREEMENT.**

And the thing that caught it is worth recording precisely: the equivalency harness
**runs their QEMU itself** rather than quoting their `SCORECARD.md`. I made that
change for a provenance reason (Law 1 forbids a SOURCED verdict in a
MEASURED-vs-MEASURED headline) — and it turned out to be the mechanism that
detected an error in the oracle. From a quoted verdict I would have had no way to
see it.

**Note the direction.** Every earlier wrong finding in this project was mine and
wrong in my favour. This one was the oracle's and *also* in my favour — which
means "the other agent is the check" is not sufficient on its own. The check has
to be an independent measurement, whoever runs it.

## FlexSPI — four defects in one peripheral, and what made each one findable

Full detail in `results/SCORECARD.md`. The part that belongs in the lessons record
is *why each defect stayed hidden until the exact moment it didn't*:

| # | defect | what would have hidden it forever |
|---|---|---|
| 7 | IP write deadlocks when TX data is pre-filled before the trigger | a driver that writes TFDR *after* triggering — which is what upstream's model assumes and what its own AHB path does |
| 8 | `Dummy_SDR` declared with no case → fast-read sequences abandoned | any LUT without dummy cycles; only the XIP read path uses them |
| 9 | `IPTXFSTS`/`MCR0` declared in the enum, never defined | a driver that doesn't size its FIFO writes from `FILL`; reads returning 0 look like "plenty of room" |
| 10 | `TFDR` position-addressed and MSB-first instead of push/LSB-first | **the erase path** — an erased NOR is all `0xFF`, which is insensitive to byte order *and* to arrival order |

Number 10 is the one worth keeping. The example erases, then programs, then reads
back. **Everything before the read-back passes with the bytes reversed and
three-quarters of them dropped.** Only the `memcmp` against programmed data can
see it. A test corpus that verified erase but not program would have certified a
FlexSPI that cannot write correctly — and would have looked *more* thorough than
one that only ran a read.

> ⭐ **A VERIFICATION STEP IS ONLY WORTH THE ASYMMETRY IT CAN DETECT.** Checking an
> all-`0xFF` buffer cannot detect an ordering bug, because `0xFF` is a fixed point
> of every permutation. The test has to compare against data whose *order* matters.

And two of mine, both caught by the regression suite rather than by reasoning:

1. **Defining `MCR0` made things worse.** My first version made `SWRESET` a stored
   flag and called `Reset()`; the example regressed from reaching the page program
   back to printing only its banner. **Defining a register is not automatically
   safer than leaving it unhandled** — an undefined register reads 0, which for
   `MCR0` happened to be survivable.
2. **The XIP window collided with Zephyr.** Registering the FlexSPI ciphertext
   region at both `0x28000000` and `0x38000000` shadowed the Zephyr platform's
   plain `MappedMemory` and broke both cm33 Zephyr targets. Zephyr's cm33 image is
   XIP-*linked* and ELF-LOADs at `0x38000000`, which must be writable memory, not
   a command-translated window. Split by security alias: NS `0x28000000` is the
   controller window (what `FlexSPI1_AMBA_BASE` resolves to for the SDK's
   non-secure build), secure `0x38000000` is Zephyr's memory.

   **Both of these were found by the Robot suite, not by me.** The suite has now
   caught three regressions this session that my own reading of the change did not
   anticipate — which is the entire argument for keeping a fast regression gate
   next to an exploratory bring-up loop.

### The blind-spot taxonomy, now five axes — four structural, one about the data

The first four are all about what the **corpus holds constant**. The fifth is not
about the corpus at all; it is about the **stimulus being symmetric under the very
operation under test**.

| # | axis | held constant | what revealed it |
|---|---|---|---|
| 1 | security state | every SDK example runs non-secure | Zephyr (cm33 secure) → S3MU had no secure alias |
| 2 | peripheral instance | every example uses LPUART1 | the cm7 Zephyr target → LPUART12 missing |
| 3 | controller instance | every example used eDMA4 | `sai/edma_transfer` → eDMA3 missing entirely |
| 4 | **path through one instance** | the transfer mechanism (eDMA vs interrupt) | `asrc_m2m_polling` → SAI had no IRQ line |
| 5 | **data symmetry** | the stimulus is a fixed point of the transform | FlexSPI read-back → TFDR byte/arrival order |

Axis 5 is the sharpest because no amount of *structural* variety fixes it. The
FlexSPI erase path exercises the whole controller — LUT engine, IP command
sequencing, the flash model, the XIP window — and still cannot see that `TFDR` was
byte-reversed and dropping three-quarters of its writes, because **`0xFF` is a
fixed point of both byte-reversal and reordering.** The plumbing can be arbitrarily
broken and an all-`0xFF` buffer still compares equal.

> ⭐ **A GOLDEN WHOSE VALUE IS INVARIANT UNDER THE TRANSFORM UNDER TEST PROVES
> NOTHING ABOUT THE TRANSFORM.**

This is the same root as the oracle's own "one shape is not a golden — sweep the
axis the register exposes." Their framing, which I'm adopting: axes 1–4 are
structural, axis 5 is about the data. A corpus can be exhaustively varied along
every structural axis and still be blind along this one.

The practical test: *for each verification step, name the fault class it can
detect.* If the answer for a step is "none that the previous step didn't already
cover", the step is decoration. `polling_transfer`'s read-back against
**programmed** data is the only step in that example that can detect an ordering
fault — it is not belt-and-braces, it is load-bearing.

## The cm7 FOC row — what "proof" would have to mean

The last row has no console oracle in either tool, so closing it is not a porting
job, it is a *definition* job. @rt1180emulator's value-test definitions, recorded
here as the reference for anyone who builds the Renode harness:

| claim | threshold |
|---|---|
| phase current obeys Ohm's law | to **one ADC count** |
| encoder speed tracks the rotor | **< 0.5 %** |
| driven to 2000 rpm | settles within **~1.5 %**, holds flat, `id ≈ 0`, zero faults |
| all of the above | under `-icount` — without it instant ADC conversions race the ISR (their RESFIFO-overflow finding) |

And their warning about the obvious shortcut, which I had proposed: watching the
eFlexPWM duty registers evolve non-degenerately is *"a good start, but the
plant-physics goldens are what make it PROOF rather than 'something changed'."*

That distinction is the whole row. A loop that is running and a loop that is
*correct* look identical from the register file; only a model of the plant it is
controlling can tell them apart. Which is why **stubbing is actively wrong here**
in a way it is not elsewhere: the FOC loop reads ADC current → derives rotor angle
→ writes PWM duty, and a stubbed ADC leaves the loop running and meaning nothing.

> ⭐ **A GREEN ROW ON A STUBBED FOC LOOP WOULD BE THE WORST ARTIFACT IN THE SET,
> PRECISELY BECAUSE IT PRODUCES NO OUTPUT TO CONTRADICT IT.**

## A cross-tree finding neither of us could have made alone

The QEMU corpus pointed at `examples/motor_control/pmsm/mc_pmsm/pmsm_enc`. In SDK
2026.06.00 that path does not exist; the example is at
`examples/demo_apps/mc_pmsm/pmsm_enc`. I inherited the stale path by copying their
corpus, so my row failed to build for a reason that had nothing to do with either
model.

The reason it survived on their side is the interesting part: it is a **VALUE**
row, proven by dedicated tests rather than built by their scorecard generator — so
the path is never exercised there. **A stale path in a not-built row is invisible
to the tool that owns it** and becomes a silent `FAIL(build)` the moment anyone
else builds it.

> Neither tree could find this alone. It took a *second* tree building the *same*
> corpus — which is, in miniature, the entire argument for the comparison being
> two independent implementations rather than one model and a checklist.

---

# 🏁 FINAL STATE

**Equivalency: 27/29 (93 %)** against the QEMU model's own corpus, both columns
MEASURED by one harness in one run on one box, on identical bytes.

| QEMU | Renode | rows |
|---|---|---:|
| PASS | **PASS** | 16 |
| BANNER | **BANNER** | 5 |
| XFAIL | **XFAIL** | 3 |
| XBUILD | **XBUILD** | 2 |
| RAN | RAN | 1 |
| PASS | FAIL | 1 — NETC switch data path |
| VALUE-PROVEN | NOT-ATTEMPTED | 1 — cm7 FOC, needs a value harness |

**16 of the corpus's 17 PASS rows now also pass on Renode.** Started this pass at
6 rows run and 21/29 agreeing.

**10 Renode defects**, all in Antmicro-shipped code, all found by stock vendor
firmware: ARMv7-M MPU applied to the PPB (tlib, patched) · `MPU_CTRL.PRIVDEFENA`
not propagated · `DMA.NXP_eDMA` 32-channel cap (eDMA4 only — **not** eDMA3) ·
`UART.NXP_LPUART` WATER field widths · eDMA major-loop epilogue discarded →
**process abort** · LPI2C `MRDR` returns data flagged `RXEMPTY` · FlexSPI IP-write
deadlock on pre-filled TX · FlexSPI `Dummy_SDR` unimplemented · FlexSPI
`IPTXFSTS`/`MCR0` declared but never defined · FlexSPI `TFDR` position-addressed
and MSB-first.

**Byte-identical two-tool agreements**: cm7 Zephyr printf samples,
`bubble_peripheral`, `sai/edma_transfer`, the NETC switch example — and
`cm7-tests-kernel-mutex-mutex_api` (68/68 lines), which had been scored `partial`
only because my harness's QEMU-side wall-clock guard cut that column at 30 lines.

**1 shared architectural limit**: `arm_mpu_wt` ≡ QEMU's `driver_examples/cache`
xfail — coherent-memory emulation cannot satisfy a stale-cache premise.

**5 blind-spot axes** — four structural (security state, peripheral instance,
controller instance, path through one instance) and one about the data (a stimulus
that is a fixed point of the transform under test).

Every wrong finding on either side — mine and the oracle's — was retracted **in
place** rather than edited away, and twice that discipline paid out directly: a
lesson survived into this codebase only because the mistake and its correction
were both still readable.

---

# 🔬 POSTSCRIPT — the instrument, and why it belongs in the record

Everything above is about the *models*. This section is about the *harness*, and
it is here because by the end of the session the harness produced more corrections
than the models did. Full detail in `results/SCORECARD.md`; this is the arc and the
lessons.

## The near-miss that reframed the rest

A closing sweep I did not strictly need read **22/29** instead of 27/29. Every lost
row was Tier A, and the cause was mine: each `.resc` carried its own hand-written
list of peripheral includes, I added one to `rt1180_m1.resc`, and the six Tier A
rows load the *same* platform through a *different* script. The platform then fails
to load and every row using it scores FAIL.

Two properties made this worse than a normal bug:

* **A model regression and a harness regression are indistinguishable in a tally.**
  A row that never ran reads exactly like a row that failed.
* Had I published it, I would have reported **five false fidelity failures against
  QEMU** — all in the direction of "Renode is worse."

## Four of my own guarantees turned out narrower than I had stated them

Every one found by running something, not by reasoning about it:

| claimed | actual |
|---|---|
| artifacts are pinned | `MANIFEST.sha256` covered **11 of 103**; `sha256sum -c` returned all-OK the whole time, because what it checked *was* fine |
| the miscount was `\t`-not-a-tab in ERE | **not reproducible**; a tidy causal story published before it was tested |
| "we both run GNU grep" | `grep` here is **ugrep 7.8.4**; I asserted a tool's behaviour without checking which tool it was |
| immune to the `timeout` orphan class | true of the *scripts*; I recreated it from one level up by wrapping a sweep in an outer `timeout` |

## What the harness looks like now

Three layers of coverage, each catching what the layer above cannot, **and each
watched failing rather than taken on inspection**:

| layer | prevents |
|---|---|
| corpus row count asserted before the run | a dropped row inflating the score — and note the two remaining differences are *red*, so a shrinking corpus would have read as *improving* fidelity |
| artifact manifest floor (103/103) | a shrinking manifest passing by checking less |
| harness-health check per row (`HARNESS-FAIL`, exit 4) | a broken harness masquerading as a fidelity result |

Plus one shared load list for every script, and one non-portable `grep -qP`
replaced by `awk -F'\t'` field equality.

## The lessons, in the order they were learned the hard way

> ⭐ **A GUARD YOU HAVE NOT SEEN FAIL IS NOT A GUARD.**
> ⭐ **AN EXPLANATION THAT FITS THE SYMPTOM IS NOT A DIAGNOSIS UNTIL IT PREDICTS
> SOMETHING YOU THEN OBSERVE.** A plausible cause published is a fabrication with
> good manners.
> ⭐ **A SILENCED ERROR IS AN ASSUMPTION WITH THE EVIDENCE DELETED** — safe only
> when what it hides is redundant with a result you capture *and* a failure branch
> you own.
> ⭐ **THE CHECK THAT MATTERS ON A MANIFEST IS COVERAGE, NOT PASSES.** Count the
> files, not the OKs.
> ⭐ **VERIFY THE IDENTITY OF THE PARTICIPANT, NOT JUST ITS BEHAVIOUR** — `grep
> --version` and `readlink /proc/PID/exe` are the same one-line reflex.
> ⭐ **THE FLATTERING FAILURE IS THE ONE TO HUNT.**

## Why this is the strongest evidence for the experiment's own premise

The brief asked which tool wins on fidelity, dual-core, and authoring effort. The
answer to all three is in the sections above. But the most reusable result is
structural:

**Every wrong finding in this project — mine and the oracle's, in both directions —
was corrected by an *independent measurement*, never by agreement.** The one time
the oracle was wrong (their FlexSPI "stub"), my harness caught it only because it
*runs their QEMU* rather than quoting their scorecard — a change made for
provenance reasons under fleet Law 1, which turned out to be the mechanism that
detected a content error. And several of my own overclaims surfaced only because
they **acted** on what I told them instead of nodding at it.

Two independent implementations checking each other found things neither could have
found alone — a stale corpus path invisible to the tool that owned it, a defect
class hidden by symmetric test data, and four of my own stated guarantees. That is
not an argument for the comparison; it is the comparison's result.

---

# 🔬 THE ZEPHYR DELTA STUDY — what two tools disagree about

Kyle's original ask, verbatim: *"i eventually want to compare zephyr outputs
between rt 1180 qemu and renode, do a study on the deltas."* It was blocked for
most of the project because QEMU could not run most Zephyr targets. Both blockers
were cleared by @rt1180emulator in one afternoon (a 16-region M7 MPU, then routing
the default `-serial` to the booting core), and the study finally ran.

**Method:** the same pinned ELF to both tools, same box, same run; compare the
**consoles**, not a pass/fail oracle. Raw UART bytes on both sides.

## ⚠ Three harness bugs had to be fixed first — all of them mine, all repeats

| bug | effect | how it was found |
|---|---|---|
| a **blanket ztest oracle** applied to every target | every *sample* scored FAIL on **both** tools while printing perfectly | the row list looked absurd |
| **common-prefix** comparison | scored a 162-vs-1-line pair "identical" | reading the rows, not the summary |
| **log-scraped UART capture** | dropped leading whitespace *and* under-captured badly (162 → 1 line) | diffing against QEMU's byte-exact capture |

The third is the one that matters: **a measurement artifact was about to be
published as a fidelity delta.** Renode's line-based UART log is not the UART;
`CreateFileBackend` gives the actual bytes. Agreement rose sharply once the
capture stopped lying.

> ⭐ **A FIX FOR ONE MEASUREMENT ARTIFACT CAN INTRODUCE A NEW ONE IN THE OPPOSITE
> DIRECTION.** The common-prefix rule was right for run-duration differences and
> wrong for coverage differences — and it failed *toward more agreement*, the
> direction least likely to be questioned. **Every normalisation step is itself a
> claim about what counts as "the same", and needs its own check.**

## A raw DIFFER count overstates real deltas — by a lot

`scripts/classify_deltas.sh` sorts differences by **what** differs. Four classes
are *not* fidelity deltas:

| class | example |
|---|---|
| **timing** | `PASS - test_x in 0.003s` vs `0.001s`; `Elapsed cycles: 4` vs `2`; `k_sleep() ticks: 5000` vs `5001`; log timestamps `.101,230` vs `.101,211` |
| **interleave** | `philosophers` — same lines, different positions (two schedulers) |
| **nondeterministic** | `test_canary_value` — the stack canary **is supposed to be unpredictable**; differing is correct behaviour |
| **capture length** | one tool simply ran longer |

Every filter pattern is justified in the source, and nothing is stripped without a
stated reason — because the filter is itself a normalisation claim.

> ⭐ **"THE CONSOLES DIFFER" AND "THE TOOLS DISAGREE" ARE NOT THE SAME CLAIM.**
> I conflated them for one row and had to correct it to the oracle.

## What survived: the real findings

**1. A systematic fault-layer difference (ARMv8-M TT / CMSE).** Two independent
tests, two unrelated addresses, identical divergence:

```
kernel-device  QEMU: BUS FAULT, BFAR 0x0          Renode: user_copy check denied region 0x0
thread_apis    QEMU: BUS FAULT, BFAR 0xfffffff0   Renode: user_copy check denied 0xfffffff0
```

Renode's `arch_buffer_validate` TT permission check rejects the invalid user
pointer *before* any dereference; QEMU's pre-check passes and the privileged copy
faults. **Both tools PASS** — Zephyr catches either way — so this is **invisible to
a pass/fail scorecard** and only a console-level study surfaces it. Scoped by the
oracle to QEMU-core `target/arm` CMSE, not their machine model; on real M33 the TT
should reject, so **Renode's layer is likely the faithful one** — a conclusion I
could not have reached alone.

**2. Three rows that were open questions all project, settled — in both
directions.**

| row | result |
|---|---|
| `arm_interrupt` | QEMU passes, Renode fails (`crash type 16384, expected 3`) → **Renode gap** → root-caused, [defect #11](#-renode-defect-11--a-nested-synchronous-exception-does-not-get-its-own-stack-frame) |
| `arm_thread_swap` | QEMU passes, Renode takes a fatal CPU exception → **Renode gap** (predicted before the oracle's run, on the record) → root-caused, [defect #12](#-renode-defect-12--the-ccrusersetmpend-exemption-is-missing-on-the-pmsav8-path) |
| `arm_mpu_wt` | **both** fail → **shared limit confirmed**, the cache-class claim I could only hypothesise from one side |
| `schedule_api` | ~~QEMU runs tests Renode's suite stops short of → **Renode gap**~~ — **RETRACTED.** Not a Renode gap: my own delta harness's 75 s wall-clock guard truncated the Renode run. Re-run with a real budget: 217 lines vs 217, agreeing but for 1 ms of rounding in three printed durations. [Written up below.](#-a-defect-in-this-instrument--a-wall-clock-guard-published-as-a-fidelity-delta) |

I had carried `arm_interrupt` and `arm_thread_swap` as *"undiagnosed — not naming
them defects, twice burned."* That caution was correct: I had no second source.
The second implementation supplied one.

> ⭐⭐ **A SINGLE IMPLEMENTATION CANNOT DISTINGUISH "MY GAP" FROM "EVERYONE'S
> LIMIT".** Only a second one can — and it did, in both directions, on the same
> afternoon: `arm_mpu_wt` went from my one-sided hypothesis to a two-sided result,
> while `arm_interrupt`/`arm_thread_swap` went from honest agnosticism to located
> gaps. That is the experiment's own premise, demonstrated rather than argued.

## ⭐ Renode defect #11 — a nested synchronous exception does not get its own stack frame

`tests/arch/arm/arm_interrupt`, cm33. QEMU passes; Renode reports
`Wrong crash type got 16384 expected 3`. I carried that number as an unexplained
symptom for the whole project and refused to call it a defect. The delta study
said QEMU passes it, which made it diagnosable.

**The number was never a crash code.** `16384 = 0x4000 = 1 << 14`, and
`238 % 32 == 14` — it is the **NVIC ISER bit for IRQ 238**.

### Evidence, to the instruction

The test pends IRQ 238; the ISR calls `k_oops()`, which is Zephyr's `ARCH_EXCEPT`:
`mov r0,<reason>; svc 2`. The reason travels in **r0 of the stacked frame**.

| | stacked PC | disassembly there |
|---|---|---|
| QEMU | `0x3800c524` | the instruction *after* `svc 2`, preceded by `mov r0, r3` → stacked `r0 = 3` |
| Renode | `0x3800c4d8` | `bx lr` in **`__NVIC_SetPendingIRQ`** → stacked `r0 = 0x4000` |

Every register in Renode's dump is explained by that other function: `r2 =
0xe000e100` is the literal at `0x3800c4dc`; `r3 = 0x47` is `7 + 64` where `7 =
238/32` is the ISER index; `r0 = 0x4000` is the bit. It is the **IRQ-238 entry
frame** — correct in itself, and the wrong frame for the SVC to be read from.

### And the NVIC trace names the mechanism

```
Acknowledged IRQ HardwareIRQ#238        ISR entered, frame stacked
Synchronous fault SuperVisorCall_S      k_oops() executes SVC *inside* the ISR
IRQ SuperVisorCall_S preempts HardwareIRQ#238
Acknowledged IRQ SuperVisorCall_S       SVC handler entered
```

Renode **preempts** the active handler with the SVC and enters it — but the
handler reads the *outer* frame, so no new exception stack frame was pushed for the
nested exception.

QEMU takes the other architectural route: it **escalates the SVC to HardFault**
(its console prints Zephyr's HardFault-path message *"Fault during interrupt
handling"*), and the HardFault pushes a fresh frame carrying `r0 = 3`.

### The mechanism, MEASURED — and both candidate mechanisms were wrong

I offered two candidates and asked the oracle to help separate them: Renode
should have **escalated to HardFault** and did not, or it was right to preempt
but **failed to stack a frame**. The oracle measured the priorities on QEMU
(SVCall = 0, IRQ 238 = 0x20) and ruled out escalation: SVCall is more urgent, so
preemption is architecturally correct on both tools. That left my second
candidate — "no frame was pushed."

**It is also wrong.** A hook on `z_arm_svc`'s first instruction, reading the
frame out of the emulated memory:

```
MSPFRAME sp=0x14001c20 r0=0x00000003 r1=0x3800c501 r2=0x00000000 r3=0x00000003
         r12=0x00000000 lr=0x3800f0f3 pc=0x3800c524 xpsr=0x610000fe
```
`(MEASURED — Renode 1.17.0, cm33, tests/arch/arm/arm_interrupt)`

**Renode pushes a perfectly correct nested frame.** `r0 = 3` is the `k_oops`
reason; `pc = 0x3800c524` is byte-for-byte the stacked PC QEMU produces. The
frame is on **MSP**, exactly where the architecture puts a frame stacked from
Handler mode. Nothing is missing and nothing is stale.

What is wrong is the **EXC_RETURN handed to the handler**:

```
SVCHOOK LR=0xfffffff5 SP=0x14001c20
```
`(MEASURED — same run, at z_arm_svc entry)`

`0xFFFFFFF5` decodes to **Mode = Handler (bit 3 = 0)** and **SPSEL = Process
(bit 2 = 1)** — a combination the ARM ARM never produces on exception entry,
because a frame stacked from Handler mode is always on the Main stack. And
Zephyr's `z_arm_svc` dispatches on exactly that bit:

```asm
3800ec58 <z_arm_svc>:
    tst.w  lr, #4          ; EXC_RETURN.SPSEL
    ite    eq
    mrseq  r0, MSP
    mrsne  r0, PSP         ; <-- taken, wrongly
    ldr    r1, [r0, #24]   ; frame->pc
```

So the handler walks off to PSP, which still holds the **outer IRQ-238 entry
frame** (that IRQ interrupted a Zephyr thread, and threads run on PSP), reads
`r0 = 0x4000`, and reports crash type 16384.

### Root cause, located in tlib

`tlib/arch/arm/helper.c:1864`, in `v7m_prepare_exception_taken()`:

```c
if(arm_feature(env, ARM_FEATURE_V8)) {
    /* We're changing v8-M mode here after we know which security state we're targeting.
     * On v8-M SPSEL is always saved. */
    *lr = deposit32(*lr, ARM_EXC_RETURN_SPSEL, 1,
                    FIELD_EX32(env->v7m.control[secure_target], V7M_CONTROL, SPSEL));
}
```

EXC_RETURN.SPSEL is taken straight from `CONTROL.SPSEL` — and **tlib never
clears `CONTROL.SPSEL` on exception entry**, as the architecture's
`ExceptionTaken()` requires. Instead it models "Handler mode implies MSP"
structurally, in `cpu.h:657`:

```c
static inline bool is_using_process_sp(CPUState *env, bool is_handler, bool is_secure)
{
    return !is_handler && (env->v7m.control[is_secure] & 2);
}
```

That keeps tlib **internally** consistent — the push and the pop both land on
MSP, so the CPU never corrupts its own state — while leaking the pre-empted
thread's stale `CONTROL.SPSEL = 1` into the EXC_RETURN that **software** reads.
The bug is invisible to a first-level exception (from Thread mode with PSP,
SPSEL = 1 is the right answer) and only appears on a **nested** one.

QEMU does it the architectural way. `target/arm/tcg/m_helper.c:972`, in
`v7m_exception_taken()`:

```c
    /* Switch to target security state -- must do this before writing SPSEL */
    switch_v7m_security_state(env, targets_secure);
    write_v7m_control_spsel(env, 0);
```

QEMU's own SPSEL deposit (`m_helper.c:900-903`) is written the same way tlib's
is — unconditionally from `CONTROL.SPSEL`. It is correct only *because* line 972
already cleared the field. **The divergence is not in the line that looks wrong;
it is in a line tlib does not have.**

> ⭐⭐ **AN EMULATOR CAN BE SELF-CONSISTENT AND STILL LIE TO THE GUEST.** tlib's
> shortcut is sound for every decision tlib itself makes with it. It is wrong only
> at the one place where the value leaves the emulator and becomes an input to
> firmware. Correctness of internal state and correctness of *architecturally
> visible* state are different properties, and a model can pass every one of its
> own consistency checks while failing the second.

> ⭐ **I WAS WRONG TWICE ON THE WAY TO THIS, AND SO WAS THE ORACLE.** I proposed
> "escalate vs. no frame"; the oracle's priority measurement killed the first and
> endorsed the second; the second was also wrong. The frame was there the whole
> time. What settled it was not more reasoning about the ARM ARM from either side
> — it was reading the eight words out of memory. **Two implementations narrowed
> the hypothesis; only an instrument closed it.**

**Blast radius (unchanged, and now with a known cause):** every Zephyr
`k_oops` / `k_panic` / `__ASSERT` raised from interrupt context reports a garbage
reason, because the reason rides in the stacked r0 and the handler is pointed at
the wrong stack. More generally, **any** Armv8-M firmware that dispatches on
`EXC_RETURN.SPSEL` — the standard CMSIS/RTOS idiom for locating the exception
frame — is mis-steered on a nested exception under Renode.

**Minimal fix.** Either clear `CONTROL.SPSEL` on exception entry as QEMU does
(architecturally faithful, but touches how `is_using_process_sp` and every
`MRS CONTROL` read behave), or gate the deposit on where the frame actually went:

```c
if(arm_feature(env, ARM_FEATURE_V8)) {
    /* PushStack(): a frame stacked from Handler mode is always on the Main
     * stack, so EXC_RETURN.SPSEL must be 0 regardless of CONTROL.SPSEL, which
     * tlib does not clear on exception entry. On a tail-chain the frame is the
     * one already stacked, so the inherited SPSEL is left alone. */
    if(!do_tailchain) {
        bool from_thread = (*lr & ARM_EXC_RETURN_MODE_MASK) != 0;
        *lr = deposit32(*lr, ARM_EXC_RETURN_SPSEL, 1,
                        from_thread && FIELD_EX32(env->v7m.control[secure_target],
                                                  V7M_CONTROL, SPSEL));
    }
}
```

Not applied or tested here — upstreaming is on hold, and an untested patch to a
CPU core is worth less than the measurement that located it.

> ⭐ **AN UNEXPLAINED CONSTANT IS A LEAD, NOT A VERDICT.** Refusing to call `16384`
> a defect while it was only a number was right; it became one the moment a second
> implementation showed what the number should have been — and then the constant
> itself named the mechanism.

## ⭐ Renode defect #12 — the `CCR.USERSETMPEND` exemption is missing on the PMSAv8 path

`tests/arch/arm/arm_thread_swap`, cm33. QEMU passes all three sub-tests; Renode
dies in the first one.

```
START - test_arm_syscalls
Available IRQ line: 238
USR Thread: IRQ Line: 238
E: ***** MPU FAULT *****
E:   Data Access Violation
E:   MMFAR Address: 0xe000ef00
E: r3/a4:  0xe000e100
E: Faulting instruction address (r15/pc): 0x3800c87e
E: Halting system
```
`(MEASURED — Renode 1.17.0, results/console/cm33-tests-arch-arm-arm_thread_swap.txt)`

`0xE000EF00` is **NVIC STIR**, the Software Trigger Interrupt Register. The
faulting instruction is the store the test performs *from a user-mode thread*:

```asm
3800c864 <user_thread_entry>:
    ...
    ldr    r3, [pc, #36]       ; r3 = 0xe000e100
    str.w  r4, [r3, #3584]     ; 0xe00  ->  0xE000EF00  (STIR)
```

This is not the test doing something illegal. STIR is the one SCS register the
architecture makes unprivileged-accessible, gated on `CCR.USERSETMPEND` — and the
test sets that bit on purpose, with a comment saying why
(`zephyr/tests/arch/arm/arm_thread_swap/src/arm_syscalls.c:218`):

```c
	/* Allow the user thread to trigger an interrupt;
	 * this is *ONLY* done for testing purposes, here, ... */
	SCB->CCR |= SCB_CCR_USERSETMPEND_Msk;
```

### Root cause: the exemption exists in tlib, on the *other* MPU path

tlib implements the rule — in `get_phys_addr_mpu()`, the **PMSAv7** lookup used
by the Cortex-M7 (`helper.c:2861`):

```c
if(is_user && unlikely(env->v7m.ccr[env->secure] & FIELD_MASK(V7M_CCR, USERSETMPEND)) &&
   tlib_nvic_is_stir_address(address)) {
    *prot = PAGE_READ | PAGE_WRITE;
    ...
```

The Cortex-M33 goes through `pmsav8_check_access_with_region()`, a **separate
function**, whose background-region arm has no such carve-out
(`helper.c:3325-3334`):

```c
} else {
    /* No region hit, use background region if: ... */
    if(!mpu_enabled || (!is_user && PMSA_PRIVDEFENA(env->pmsav8[secure].ctrl))) {
        *prot = cortexm_check_default_mapping_v8(address);
    } else {
        goto do_fault;          /* <-- unprivileged PPB access, no STIR exemption */
    }
}
```

With the MPU on and `is_user` set, every PPB access from a user thread takes the
`do_fault` branch. The access never reaches the NVIC, so the NVIC never gets to
apply the rule.

QEMU splits the two concerns instead of merging them. The MPU lookup exempts the
whole PPB on **both** the v7 and v8 paths (`target/arm/ptw.c:2894`,
`pmsav8_mpu_lookup`):

```c
} else if (m_is_ppb_region(env, address)) {
    hit = true;
```

and the *privilege* decision is then made per-register by the NVIC device itself
(`hw/intc/armv7m_nvic.c:2197`):

```c
static bool nvic_user_access_ok(NVICState *s, hwaddr offset, MemTxAttrs attrs)
{
    switch (offset) {
    case 0xf00: /* STIR: accessible only if CCR.USERSETMPEND permits */
        return s->cpu->env.v7m.ccr[attrs.secure] & R_V7M_CCR_USERSETMPEND_MASK;
    default:
        return false;
    }
}
```

> ⭐⭐ **THE SAME ARCHITECTURAL RULE, IMPLEMENTED TWICE IN ONE CODEBASE, WILL
> DRIFT.** tlib has two MPU lookups for two profiles and the rule was added to one
> of them. The bug is not that anyone misread the ARM ARM — the rule is right
> there in `helper.c:2861`, correctly written. It is that a second copy of the same
> decision existed and was not kept in step. QEMU is immune here not because it is
> more careful but because its shape has **one** place to make the decision: the
> MPU exempts the PPB uniformly, and the device owns the privilege rule.
>
> This is the same class as the `load_peripherals.resc` incident earlier in this
> project — an include list duplicated across eight `.resc` files, where adding
> EMDIO to one of them silently failed six Tier A rows. Both times the defect was
> the **duplication**, not the edit.

**Note on my own patch.** The PMSAv7 PPB short-circuit earlier in this project
(the `[RT1180 PATCH]` at `helper.c:2797`, which unblocked the cm7) returns
`PAGE_READ | PAGE_WRITE` for the whole of `0xE0000000-0xE00FFFFF` **without a
privilege check**. That is over-permissive: on the cm7 path it would let an
unprivileged thread reach any SCS register, not just STIR. It sits *above* the
`USERSETMPEND` carve-out it makes redundant. It bought a booting M7 at the cost
of a privilege check, and it is recorded here rather than left as a silent
liberty taken in the corner of a patch that "worked".

## ⚠ A defect in *this* instrument — a wall-clock guard published as a fidelity delta

The third row on my chase list was `schedule_api`, carried as *"QEMU runs tests
Renode's suite stops short of → Renode gap."* **It is not a Renode gap. It was my
delta harness.**

`scripts/zephyr_delta.sh` guarded the Renode run with
`timeout -k 20 $((SECS*3))` — 75 s of **host** time, derived from QEMU's 25 s
budget. But Renode is roughly 5× slower in host time per unit of virtual time, so
that guard bound the Renode side long before `RUNFOR` (60 s of *virtual* time)
ever could. Renode handles `SIGTERM` gracefully, so the log ends with
`Machine paused. / Disposed. / Renode is quitting` — a **clean exit**, with no
marker distinguishing it from a completed run.

MEASURED, same ELF, same platform, same emulator, only the budget changed:

| run | wall-clock guard | guest time reached | captured lines | scored |
|---|---|---|---|---|
| delta study | 75 s | 13.93 s | 150 | `DIFFER` (published) |
| re-run | 420 s | 14.66 s (suite ends) | **217** | see below |

Against QEMU's 217 lines, the full Renode capture differs on **3 lines**, all of
them the same 1 ms of rounding in a duration the firmware prints itself:

```
137c137
<  PASS - test_slice_reset in 1.807 seconds
>  PASS - test_slice_reset in 1.806 seconds
181c181
< SUITE PASS - ... duration = 14.648 seconds
> SUITE PASS - ... duration = 14.647 seconds
200c200
<  - PASS - [threads_scheduling.test_slice_reset] duration = 1.807 seconds
>  - PASS - [threads_scheduling.test_slice_reset] duration = 1.806 seconds
```

Same 217 lines, same 27 passes, same 1 skip, same suite verdict. This is
**agreement**, reclassified `len+content → timing/nondet`.

I then audited **every** DIFFER row for the same failure, rather than fixing the
one I tripped over: the last host timestamp in each Renode capture, against the
75 s guard.

| target | renode last host time |
|---|---|
| `cm33-tests-kernel-sched-schedule_api` | **69.4 s** ← at the guard |
| `cm7-tests-kernel-timer-timer_api` | 49.8 s |
| `cm33-samples-philosophers` | 45.1 s |
| all 31 others | ≤ 19.5 s |

Blast radius: exactly one row. The audit is the point — I could not have known
that without running it, and "only one" is a measurement, not a reassurance.

**The fix is not the number 75.** The guard is now its own knob (`RSECS`, sized
for Renode instead of borrowed from QEMU) *and* a truncated run is now detected
and reported as `truncated` — a harness verdict, never scored as agreement and
never scored as disagreement:

```sh
if [ "$rrc" -eq 124 ] || [ "$rrc" -eq 137 ]; then
    verdict="truncated"
```

### The audit was itself incomplete, and the platform work is what caught it

The audit above asked, of every **DIFFER** row, "was this Renode capture cut by
the guard?" That question has a blind spot I did not see until a *different* piece
of work tripped over it.

While regression-testing an unrelated change to the cm7 platform, one row came back
`CHANGED  old=140 new=217`: `cm7-tests-kernel-sched-schedule_api`. Looking at the
two published captures:

```
qemu   (139 lines)  ... START - test_slice_scheduling
                        AB
renode (140 lines)  ... START - test_slice_scheduling
                        ABCDEFGHI
```

**Both** sides had been cut, at nearly the same point, mid-`test_slice_scheduling`.
Neither ever reached `PROJECT EXECUTION SUCCESSFUL`. And because they were cut at
nearly the same place, the row scored **`identical`** — counted as agreement, in
the published 73/83.

> ⭐⭐ **TWO TRUNCATED CAPTURES AGREE WITH EACH OTHER PERFECTLY.** An audit that
> looks for truncation among the rows that *disagree* is structurally blind to the
> case where truncation destroys the study's meaning — because there, truncation
> manufactures agreement. My audit asked the DIFFER rows a good question and never
> asked the `identical` rows any question at all. **The flattering direction is
> the one to interrogate hardest, and it was the one I skipped.**

### So the check is now on the content, not the clock

A timing heuristic was the wrong instrument twice. A ztest target announces itself
(`Running TESTSUITE`) and ends with `PROJECT EXECUTION SUCCESSFUL/FAILED`; a ztest
capture without that terminal marker **did not finish**, regardless of exit codes
or elapsed time:

```sh
finished() {
    grep -qaE 'PROJECT EXECUTION (SUCCESSFUL|FAILED)' "$1" && return 0
    grep -qaE 'ZEPHYR FATAL ERROR|Halting system' "$1" && return 0   # guest halt, not a cut
    return 1
}
```

The second clause is load-bearing. `arm_interrupt`, `arm_thread_swap` and
`arm_mpu_wt` all end early because the **guest halted on a fatal error** — those
are the real model differences this study exists to find, and a naive "no terminal
marker ⇒ truncated" rule would have swept all three into the harness bucket and
erased two genuine Renode defects. *A completion check must distinguish "the clock
stopped it" from "the firmware stopped".*

Applied to all 83 rows, the content check flags exactly four ztest rows as
incomplete on at least one side — three of them the guest halts above, and **one
genuine double-truncation**: `cm7-tests-kernel-sched-schedule_api`. Blast radius
of this failure mode: one row, again — but this time it had been sitting *inside
the agreement count*, not outside it.

### The re-run, and what it did and did not change

Both sides re-run with real budgets (Renode `RunFor 90`, QEMU 400 s):

```
$ diff qemu_cm7-tests-kernel-sched-schedule_api.norm renode_...norm
$ echo $?
0
```

**217 lines vs 217, byte-identical**, both ending `PROJECT EXECUTION SUCCESSFUL`.

> ⭐⭐ **THE NUMBER DID NOT MOVE. THE EVIDENCE UNDER IT DID.** The row was scored
> `identical` before and is scored `identical` now, so 73/83 (88 %) is unchanged —
> but it had been **right by coincidence**, resting on 139 truncated lines that
> happened to match 140 truncated lines. It now rests on 217 lines of real
> agreement. *A correct total can be assembled from a row that was not actually
> measured*, and no amount of checking the total would ever have shown that.

There is one more confirmation in the re-run, and it is the reason the completion
check had to be content-based rather than exit-code-based: **QEMU exited 124** —
killed by its own guard — *after* printing all 217 lines and
`PROJECT EXECUTION SUCCESSFUL`. On that side a `timeout` kill is the normal way a
run ends, so an exit-code rule would have thrown away a complete capture as
truncated, exactly as surely as it earlier kept a truncated one as complete.
**Both error directions, from the same proxy, on the same row.**

> ⭐⭐ **A RESOURCE LIMIT THAT CAN SILENTLY BECOME A FINDING IS THE MOST DANGEROUS
> KIND OF BUG IN AN INSTRUMENT.** It does not crash, it does not warn, and it
> produces a result of exactly the shape the study is looking for. This one had
> been sitting in the published delta study as a "Renode gap", in the direction
> that flatters neither tool by accident — it just happened to land on Renode
> because Renode is the slower one. **Any budget in a comparison harness
> systematically penalises the slower side, which is precisely the side the study
> is measuring.**
>
> It also survived my earlier self-audits because those asked "is the comparison
> fair?" and "are both columns MEASURED?" — and both answers were yes. The
> question I had not asked was "**did the thing I measured finish?**"

### Then I looked for the mirror image, and it was there

A fix applied to one column of a two-column comparison is not a fix — it is a new
bias. So I asked the same question of the QEMU side, which ran under
`timeout -k 5 $SECS` = **25 s**.

MEASURED, `cm7-tests-kernel-mutex-mutex_api`:

| QEMU budget | captured lines | reached |
|---|---:|---|
| 25 s (the study) | 30 | mid-suite, `test_mutex_priority_inheritance` |
| 300 s (re-run) | **68** | `PROJECT EXECUTION SUCCESSFUL` |

And the full 68 lines against Renode's 68:

```
$ diff qemu_cm7-tests-kernel-mutex-mutex_api.norm renode_cm7-tests-kernel-mutex-mutex_api.norm
$ echo $?
0
```

**Byte-identical, 68 of 68 lines.** A perfect two-tool agreement — the project's
fourth — had been published as `partial`, which the study explicitly does *not*
count as agreement. The Renode-side bug cost one row in the flattering direction
for QEMU; the QEMU-side bug cost one row in the flattering direction for neither,
it just hid a result. **One row each. I would have found neither by fixing only
the one that bit me.**

### The asymmetry that stays, and is now stated rather than hidden

Renode is bounded by **virtual** time (`emulation RunFor`) with a host-time guard
behind it; QEMU has **only** a host-time guard. These are not comparable
quantities and no single number covers both, so they are now separate knobs
(`RSECS` = 420 s, `QSECS` = 300 s) rather than one budget with a multiplier.

That also means a `timeout` kill is *normal* on the QEMU side — `philosophers`
and `synchronization` never terminate — so `truncated` is scored on the Renode
side only, and the free-running samples remain compared over their common prefix,
as they always were.

**Revised delta-study totals:** 83 targets — 48 identical, 25 timing/nondet,
7 len+content, 2 CONTENT, 1 partial = **73/83 (88%) agree**, up from 71/83 (86%).
Both of the two recovered rows were my instrument, not either model.

## ⭐⭐ Defects #11 and #12, MUTATION-PROVEN

Locating a defect and proving it are different claims. Both fixes were applied to
tlib, rebuilt, installed, and the tests re-run — so the mechanism is not argued,
it is *switched off and on*.

The build loop already existed from the PPB patch earlier in this project, with
its discipline intact:

- built through `Cores/CMakeLists.txt` (a plain `tlib` build omits the 75
  `renode_external_attach__*` interop symbols);
- **ABI-verified against the shipped library** before installing —
  `nm -D --defined-only`: **2564 symbols on both sides, 0 missing, 0 extra**;
- **M0 as a positive control** after installing, to separate "my patch is wrong"
  from "my rebuild is incompatible". M0 passes.

The patch is `patches/tlib-defect11-defect12.patch` (52 added lines, of which the
functional change is ~10; the rest is the reasoning, in comments, at the site).

### Defect #12 — closed, byte-identical

```
$ diff qemu_cm33-tests-arch-arm-arm_thread_swap.norm renode_patched.norm
$ echo $?
0
```

**29 lines vs 29, byte-identical.** `SUITE PASS - 100.00% [arm_thread_swap]:
pass = 3, fail = 0, skip = 0`, matching QEMU exactly. Before the patch the first
sub-test died on `MPU FAULT / MMFAR 0xE000EF00`.

### Defect #11 — the mechanism is confirmed, and it uncovered another defect

| tlib | matching prefix vs QEMU's 83 lines | Renode capture |
|---|---:|---:|
| stock 1.17.0 + PPB patch | **22** | 30 lines |
| + defect #11 fix | **52** | 78 lines |

Everything defect #11 predicted, landed:

```
E: >>> ZEPHYR FATAL ERROR 3: Kernel oops on CPU 0     (was: ERROR 16384)
E: Fault during interrupt handling
...
E: >>> ZEPHYR FATAL ERROR 4: Kernel panic on CPU 0
```

`k_oops` → reason 3, `k_panic` → reason 4, `__ASSERT` → its assert text: all three
`ARCH_EXCEPT` reasons now arrive intact from interrupt context, byte-identical to
QEMU, because the handler is finally reading the frame tlib was pushing all along.
**The root cause was right.**

It was also hiding a second one. The test does not end at line 52.

## ⭐ Renode defect #13 — ARMv8-M stack limits are stored but never enforced

With #11 fixed, `arm_interrupt` runs 30 lines further and then diverges again, on
the sub-test that had never been reachable before:

```
                     QEMU                                  Renode
  E: ***** USAGE FAULT *****                  Assertion failed at arm_interrupt.c:283:
  E:   Stack overflow (context area not valid)  arm_isr_handler: (reason not equal to -1)
  E: r0/a1: 0x0  r1/a2: 0x0  r2/a3: 0x0        expected_reason has not been reset (2)
  E: >>> ZEPHYR FATAL ERROR 2: Stack overflow   FAIL - test_arm_interrupt
```

The test (`zephyr/tests/arch/arm/arm_interrupt/src/arm_interrupt.c:444`) sets PSP
to `stack_info.start + 0x10` and then takes an interrupt, so that **exception-entry
stacking drives PSP below PSPLIM**. The architecture requires a `UsageFault` with
`CFSR.STKOF`; Zephyr reports it as `K_ERR_STACK_CHK_FAIL` (reason 2). Renode takes
the interrupt normally, the ISR runs with `expected_reason` still 2, and the test
asserts.

### The registers exist. The check does not.

| | tlib | QEMU |
|---|---|---|
| `MSPLIM`/`PSPLIM` storage | `cpu.h:388-389`, banked | `cpu.h`, banked |
| `MRS`/`MSR` access | `helper.c:3851-3961` — reads and writes work | `m_helper.c:2538-2543` |
| limit *selection* (which of the four banked limits applies) | no such function | `v7m_sp_limit()`; explicit PSP/MSP choice at `m_helper.c:786-790` |
| **compared against SP** | **nowhere** | `m_helper.c:1233` (base frame), `:797-807` (callee stacking), `:626` (BLXNS) |
| `CFSR.STKOF` constant | **not defined at all** (`cpu.h:109-115` has no STKOF among the `USAGE_FAULT_*` bits) | `R_V7M_CFSR_STKOF_MASK` |
| `CCR.STKOFHFNMIGN` | field defined (`cpu.h:1324`), never read | honoured — `m_helper.c:1011`, gated on a negative-priority request |
| `FPCCR.SPLIMVIOL` | `helper.c:1665`: `/* TODO: Check if SP violates the current stack limit and set SPLIMVIOL accordingly */` | `m_helper.c:1009-1014` |

tlib's own `TODO` documents half of it. The other half — that the limit is never
enforced at all — is not marked anywhere.

> ⭐⭐ **A REGISTER THAT ACCEPTS WRITES AND CHANGES NOTHING IS WORSE THAN AN
> UNIMPLEMENTED ONE.** An unimplemented `PSPLIM` would log
> `Unhandled write to offset ...` and the firmware author would see it. Here the
> `MSR PSPLIM, r0` succeeds, the `MRS` reads the value back, every consistency
> check the firmware can perform passes — and **ARMv8-M hardware stack protection
> is silently inert.** `CONFIG_HW_STACK_PROTECTION` on a v8-M part is not
> approximated under Renode; it is absent while appearing present. A stack
> overflow that the silicon would have trapped runs on into whatever is below the
> stack.
>
> This is the same failure mode as defect #11 one level up: state that is
> internally consistent and architecturally wrong. It is also the reason the
> project's rule is *"model what the driver polls"* — a driver polls `PSPLIM` by
> writing it and trusting the hardware, and there is nothing to poll.

**Not fixed, and I am saying so rather than letting it look finished.** A faithful
STKOF needs the frame size computed *before* the FP and base-frame pushes (tlib
pushes word-by-word via `v7m_push()`, so a naive per-word check would write 7 of
the 8 words and produce a *different* console from QEMU's all-zero ESF), plus the
limit-selection rule, a new `CFSR.STKOF` bit, the `CCR.STKOFHFNMIGN` exemption, and
routing the fault through tlib's derived-stacking-fault machinery. That is a real
change to a CPU core with real regression risk, for one corpus row, and it is a
*fourth* defect beyond the three gaps on the approved chase list. It is documented
here with the exact call sites on both sides; it is not half-done and reported as
done.

### Which binary each number in this report was measured on

Three libraries are kept side by side, because a number is only reproducible
against the binary it was measured on:

| file | contents | what it backs |
|---|---|---|
| `translate-arm-m-le.so.orig-1.17.0` | stock Renode 1.17.0 | the untouched upstream baseline |
| `translate-arm-m-le.so.ppbpatch-baseline` | + the PMSAv7 PPB patch (needed for the M7 to boot at all) | **every published number**: the 30/31 equivalency scorecard and the 83-row delta study. This is the installed library. |
| `translate-arm-m-le.so.d11d12-verified` | + the defect #11 and #12 fixes | the mutation proof in this section **only** |

**The patched results are deliberately NOT folded into the headline.** The fixes
are not upstream, so they are not what a Renode user gets, and the scorecard must
describe the tool as it ships plus the one patch without which the M7 cannot run
the corpus at all. Mixing in a local fix would flatter the numbers with work
nobody else has.

## The cm7 FOC row — the frontier, and what each tool gave for free

The brief asks for effort and "what each tool gave for free" per rung. This row is
the one where the answer is most lopsided, in both directions.

### What the QEMU side gave for free: the knowledge, not the code

Five models were ported (LPADC, XBAR1, EQDC1, eFlexPWM, and a PMSM plant —
~1,600 lines of C#). None of that is the expensive part. **The expensive part is
knowing which bits are load-bearing**, and every one of those came from the
oracle's C models rather than from the reference manual:

| what | the "obvious" value | the real one | what the obvious one does |
|---|---|---|---|
| eFlexPWM `DTCNT0/1` reset | 0 | `0x07FF` | **shoot-through** — zero dead time shorts the DC bus through an inverter leg |
| EQDC `POSDPER` reset | 0 | `0xFFFF` | speed observer divides by it → **infinite rotor velocity before the motor moves** |
| LPADC `GCR[GCALR]` reset | 0 | `0x10000` | Q16 gain of *nothing* → every conversion scales to zero |
| ADC counts per amp | `0x7000/I_MAX` = 3475 | **1820** | ~1.9× high through the driver's decode → over-current trip at startup |
| DC-bus code | `v/60.8 × 0xFFFF` | `v/60.8 × 32768 × 11/12` | 2.18× high → reads 24 V as 52 V → over-voltage lockout, demo never leaves AppStop |
| encoder CPR | 2000 | **8000** (4× lines) | dq frame diverges; torque current lands in the d-axis; no sustained spin |
| LPADC `VERID` | anything | `0x02002C1B` | feature bits are *asserted on* by `LPADC_SetConvCommandConfig` |

> ⭐⭐ **EVERY ONE OF THOSE PRODUCES A SYSTEM THAT BOOTS, INITIALISES CLEANLY, RUNS
> ITS CONTROL LOOP AT THE RIGHT RATE, AND DOES NOTHING.** Not one is discoverable
> by the bring-up loop that carried the other 30 rows — "map the block, watch what
> the driver polls, stop when it stops spinning". The driver *does* stop spinning.
> This is the precise point where a console-oracle corpus runs out, and it is why
> the row is the frontier rather than merely the largest port.

### What Renode gave for free

- **Runtime C# compilation.** Five peripherals and a plant went from the oracle's
  C to running models with no build system, no rebuild of the emulator, and no
  restart — `i @file.cs` in the same session. The equivalent on the QEMU side is a
  meson build and a relink.
- **`LimitTimer` on the machine clock source** gave the PWM submodule counters and
  the 50 kHz plant step for free; QEMU's `ptimer` needs explicit
  transaction begin/commit around every change, and an open transaction is an
  assertion failure.
- **Platform inheritance.** `using "mimxrt1189_cm7.repl"` let the SDK platform
  extend the Zephyr one without duplicating 16 entries — the fix that removed a
  whole class of shadowing risk cost one line.

### What fought me

- **The console is on a different UART than Zephyr's**, on the same core, with no
  error — a Zephyr-derived platform is simply silent under an SDK image.
- **The plant is not observable from the monitor.** A peripheral with no bus
  address is unaddressable, `@ none` included (Renode's own `nvicInput7` behaves
  identically). That forced the harness onto the real register path, which is a
  better instrument — but it was the tool refusing, not me choosing.
- **My own harness failed silently three ways** (an `eval` that ate the script
  path, an unquoted `echo` argument, and `CR CR LF` breaking every `$`-anchored
  pattern) and **exited 0 with zero samples** — which looks exactly like "the rotor
  did not turn".
- **Renode crashed once** in `ConsoleIOSource.HandleInput` with stdin redirected,
  mid-run. Not reproducible at 10/30/60/120 `-e` flags; avoided by putting the
  probe in the `.resc`. Recorded as an observation with a workaround, **not** as a
  defect.

### Status

Four walls cleared (FlexSPI1 → console UART → ADC1 → PWM/EQDC/XBAR), the loop
electrically closed, and a value harness that reads the machine through EQDC1's
real registers — the ones the control loop reads — rather than through the plant's
own state. The row stays **`VALUE-PROVEN | NOT-ATTEMPTED`** until there is a
measured spin and a pass condition agreed against the oracle's goldens. No
threshold has been invented in advance.

## ⭐⭐ Running the ORACLE'S OWN value tests on Renode

The cm7 FOC row has no console success string on either tool, and I had been
treating "define what counts as proof" as work still to do. It was not: **the
oracle's proof is a set of binaries**, and binaries are exactly what this project
compares. Their value tests link to ITCM `0x0FFE0000` / DTCM `0x20000000` (the
CM33 map) and report through **semihosting** (`bkpt 0xAB` / `SYS_WRITE0`), so they
needed a CM33 motor platform and Renode's `CPU.SemihostingHandler` +
`UART.SemihostingUart` — and then they just run.

| their binary | on Renode | what it proves |
|---|---|---|
| `imxrt1180-motor/motor.elf` | **PASS** ×3 | rotor aligns at EQDC 500; phase current hits the first-principles golden; DC-bus sense reads the plant's real 24 V |
| `imxrt1180-adc/adc.elf` | **PASS** | LPADC command/FIFO basics |
| `imxrt1180-adc-ab/adcab.elf` | **PASS** ×2 | dual A/B-side conversion routing |
| `imxrt1180-adc-fifo-align/fifoalign.elf` | **PASS** ×3 | **one of the two tests the oracle pins `pmsm_enc` on** |
| `imxrt1180-pwmadc/pwmadc.elf` | **PASS** (after a fix) | PWM → XBAR → ADC synchronised sampling |

> ⭐⭐⭐ **THE GOLDEN COMES FROM NEITHER IMPLEMENTATION.** Their `motor.c` derives
> the expected phase current from duties 500/554/446 per-mille → amplitude-invariant
> Clarke → `v_beta` = 1.4965 V → Ohm's law at standstill → 2.400 A → 1820 counts/A
> → **4369 counts ±5 %**, and the alignment angle from EQDC 500 = 8000 × 22.5/360.
> `Rs`, `Pp`, `I_MAX`, `J`, `B` are motor/board datasheet facts. Two independent
> models and an expectation that belongs to neither is as close to ground truth as
> this experiment can get without silicon.
>
> And it independently confirmed the three constants I was least sure of — 1820
> ADC counts/A, the DC-bus 11/12 compensation on a Q15 frac16, and encoder CPR
> 8000 — every one an inverse of what the *driver* does rather than a datasheet
> ratio. **"A RANGE IS NOT A GOLDEN"** (@rt1180emulator): their mutation audit made
> the plant report 3× the phase current and a bounded-current check still passed.
> A golden misses where a range shrugs.

### `pwmadc` found a defect that lives BETWEEN correct blocks

Its first run said:

```
PWM->XBAR->ADC: FAIL - sync chain did not trigger a conversion
```

Every block was individually right. The eFlexPWM emitted its output trigger on
each reload; the XBAR routed input levels to selected outputs; the LPADC ran a
command chain on a hardware trigger. **The chain did not exist** — the models had
the machinery and the platform never connected it, and neither model implemented
`IGPIOReceiver`, so it could not have been connected.

Their test names the whole path in its own header —
`PWM1 SM0 OutTrig0 (XBAR in 74) → XBAR → ADC12_HW_TRIG0 (XBAR out 140) → ADC1 HW trigger 0`
— and programs `XBAR1_SEL70 = 74`. Wiring exactly that, plus `IGPIOReceiver` on
both sinks: **PASS**.

> ⭐⭐ **A PER-BLOCK CORPUS CANNOT SEE A DEFECT IN THE WIRING BETWEEN BLOCKS.**
> Every peripheral test in this project passes a peripheral. This one failed a
> *path*, and it failed it while all three participants were correct. That is a
> whole class of fidelity defect the 31-row console corpus is structurally blind
> to — and it took the other tool's test suite to expose it, because their tests
> were written to prove a *system* behaviour that has no console string.

### Scope is declared, not discovered

The first sweep ran **every** oracle test on the CM33 motor platform and printed
`FAIL` beside their `asrc.elf` and `dma.elf`, `NO-OUTPUT` beside `dualcore` and
the `cm7-*` binaries. That platform has no ASRC, no SAI, no eDMA, one core.

> ⚠ Those would have been **published failures of someone else's tests caused by
> my platform** — the same harness-limitation-as-finding pattern as every
> truncation bug in this document, except aimed at the other tool, which is the
> direction I have the least standing to get wrong. The in-scope set is now listed
> explicitly with its reason and everything else reads `OUT-OF-SCOPE`: a statement
> about this platform, never a verdict on a model or a test. Flagged to
> @rt1180emulator before they could read a `FAIL` next to their `asrc.elf`.

**Value tests, in scope: 5 PASS, 0 FAIL, 3 NO-OUTPUT** (`motor-load`, `motor-sat`,
`motor-thermal` — each needs plant parameters QEMU passes as `-global` and Renode
takes at construction, so they need a per-config platform; `motor-load` is being
run that way now).

## ⭐⭐⭐ Three wrong core clocks across two implementations, found by one value test

The oracle's `tests/imxrt1180-pwm` measures the eFlexPWM's actual **period** against
SysTick, and **sweeps the clock-root divider** — because a model that ignores the
clock tree reports the same period at every divider. It found three separate
defects in two independent emulators, none of which any console oracle could see.

### 1. Mine: the PWM ignored the clock tree

I had ported their eFlexPWM and kept a constructor default
`clockFrequency = 200000000` — which is *exactly* the anti-pattern their own source
documents having removed: *"This block used to hold a hardcoded default behind
`if (!clk) clk = DEFAULT;`, which made the missing wiring invisible and ran the
timer at the wrong rate."* **I ported the model and not the lesson.**

```
before:  prescale 64 modulo 1000 -> measured 76800,  expected 145454
         prescale 64 modulo  500 -> measured 38399,  expected  72727
```
Same period regardless of divider. The PWM now reads `CLOCK_ROOT[Bus_Wakeup]` from
the CCM **at the point of use**, and if the root reports 0 Hz the submodule does not
run and says so.

### 2. Theirs: both cores wired to one wrong clock

After the fix, all seven configs were off by **the same constant, 0.8**. A constant
factor once the tree is right is not a tree bug — it is a *reference-clock*
disagreement, and 0.8 = 240/300.

Their test anchors on the CPU (`SYST_CSR = 0x5`, core clock) at `CPU_HZ = 300 MHz`,
matching their machine. Five citations said 240:

- SDK `project_template/clock_config.c`: M33 root = `MuxSysPll3Out`, `div = 2`
- SYS_PLL3 = XTAL × 20 = 480 MHz — **their own constant**, `imxrt1180_ccm.c:203`
- 480 / 2 = **240 MHz**
- CMSIS `system_MIMXRT1189_cm33.h:73`: `DEFAULT_SYSTEM_CLOCK 240000000UL`
- the measurement: 0.8 exactly, on all seven configs

They verified and fixed it — **and chasing it found a second one**: their machine
wired *both* cores to one 300 MHz clock, where CMSIS cm7 says **792 MHz**. The M7
had been ~2.6× slow on SysTick and nobody had noticed, because no cm7 test pinned
an absolute rate.

### 3. Mine again, and the worst of the three: a citation that was wrong

Their M7 finding sent me to audit my own cores. My M7 was **798 MHz**. The comment
justifying it, in four of my `.resc` files, read:

> `798 = the core-clock the M7 firmware itself passes to SDK_DelayAtLeastUs (literal 0x2FAF0800)`

**`0x2FAF0800` is 800,000,000.** Not 798,000,000. The constant matched *neither*
of its candidate sources, and the citation beside it had made it look grounded for
the whole project.

> ⭐⭐ **A WRONG NUMBER WITH A REFERENCE NEXT TO IT IS HARDER TO CATCH THAN A WRONG
> NUMBER ALONE** — because the reference is precisely what stops you re-deriving
> it. Every provenance rule in this project exists to make numbers checkable; this
> one was checkable and never checked, by me, for months, because it *looked*
> checked. **Provenance is a claim, not a proof.**

Three numbers were in play and I had none of them: **800 MHz** (the firmware's
delay-loop maximum, and NXP's headline figure), **792 MHz** (what the board
*configures*: ARM PLL `loopDivider 132` / `postDivider 2` → 24 × 132/4 = 792, and
CMSIS `system_MIMXRT1189_cm7.h:73`), and **798 MHz** (mine, from bad arithmetic on
the first). The configured clock is what a core runs at — 792, independently where
they landed. Fixed in the platform and all five scripts, with the bad comment
**replaced by the derivation rather than deleted**, so the file records that it was
wrong.

### The golden echo, and why only a second implementation could break it

Their `CPU_HZ = 300` matched their model's wrong 300, so **the test passed by
echoing the very error it existed to catch** — the trap that file's own header
warns about, live, in the file that warns about it.

> ⭐⭐⭐ **A TEST AND A MODEL THAT SHARE AN ASSUMPTION CANNOT CHECK EACH OTHER.**
> Nothing inside either tool could have found this: their test agreed with their
> machine, my model agreed with my platform, and both pairs were self-consistent.
> It took a *second implementation with an independently-sourced constant* to
> break the echo. That is this experiment's premise, and it is now demonstrated on
> clocks, harnesses and models rather than argued — **three wrong core clocks, two
> emulators, one test measuring a period.**

## ⭐⭐ Defect #13 FIXED and verified — and it repeated defect #11's lesson one layer down

Implemented ARMv8-M stack-limit checking in tlib (`patches/tlib-defect13.patch`,
166 added lines):

- **`CFSR.STKOF`** (UFSR bit 4) defined — it did not exist in tlib at all;
- **`v7m_sp_limit()`** — selects MSPLIM/PSPLIM banked by security;
- **`v7m_check_stack_limit()`** — checks the WHOLE frame before any push, sets
  `CFSR.STKOF`, parks SP at the limit, and skips every push;
- routed as a **derived fault** through tlib's existing
  `exception_phase_fault` → `v7m_resolve_stacking_fault()` path, which is QEMU's
  `DerivedLateArrival()` shape, rather than inventing a second mechanism;
- **`CCR.STKOFHFNMIGN`** honoured (see the labelled approximation below).

> ⭐ **THE CHECK CANNOT BE PER-WORD, AND THAT IS WHY IT NEEDED THE FRAME SIZE UP
> FRONT.** `v7m_push()` pushes the base frame highest-address-first, so a per-word
> limit check would write 7 of the 8 words and only then fault — leaving a
> partially-written frame where the architecture and QEMU leave none, and producing
> a *different ESF from the same firmware*. That was the exact reason given for not
> attempting this fix earlier, and it turned out to be the whole design constraint.

### It failed the first time, for defect #11's reason

First build: the UsageFault **fired**, with QEMU's exact text
(`Stack overflow (context area not valid)`, all-zero ESF) — but on the **idle
thread**, long after the test had already failed.

`v7m_sp_limit()` selected the limit with `is_using_process_sp_current()`, which
consults `in_handler_mode()`. At that point in exception entry the NVIC has
**already** been acknowledged, so `v7m.exception` is non-zero and the CPU already
counts as being in Handler mode — while the frame is still being pushed on the
**pre-exception** stack (the SP switch happens later, at
`switch_v7m_sp(env, false)`). So the mode said *main* while `regs[13]` held PSP,
and every check compared the thread's SP against **MSPLIM**.

> ⭐⭐ **DO NOT CONSULT THE MODE AFTER THE MODE HAS CHANGED.** This is defect #11
> exactly — that defect was `EXC_RETURN.SPSEL` read from a `CONTROL.SPSEL` the
> architecture requires to have been cleared; this is the stack *limit* read
> through an `in_handler_mode()` that had already flipped. Same window, same
> mistake, made by me while fixing the first one. `v7m.process_sp` tracks which
> physical SP is in `regs[13]` right now, which is the stack the frame actually
> lands on.

### Verified

```
$ diff qemu_cm33-tests-arch-arm-arm_interrupt.norm  renode_patched.norm
62c62  PASS - test_arm_interrupt in 0.002 seconds  |  0.001 seconds
74c74  SUITE PASS ... duration = 0.005 seconds     |  0.004 seconds
76c76  - PASS - [arm_interrupt.test_arm_interrupt] |  0.001 seconds
```

**83 lines vs 83, `SUITE PASS - 100.00% ... pass = 3, fail = 0, skip = 1`** —
identical to QEMU but for three printed durations rounding by 1 ms. The row moves
from `DIFFER` (22-line matching prefix on stock) to **timing/nondet**, the same
class as 25 other rows.

Progression across the three fixes, on one test:

| build | matching prefix vs QEMU's 83 lines | verdict |
|---|---:|---|
| stock 1.17.0 + PPB | 22 | `FAIL`, crash reason 16384 |
| + defect #11 | 52 | `FAIL`, stack-overflow sub-test |
| + defect #13 | **83** | **`SUITE PASS`**, 1 ms rounding only |

ABI-verified at 2564 symbols (0 missing, 0 extra) and M0 passes as the positive
control, on a hardlinked validation tree so the in-flight backlog sweep kept
running against the library it started on.

### The labelled approximation

`CCR.STKOFHFNMIGN` suppresses the check while a **negative-priority** exception is
being taken, so a stack overflow inside a HardFault handler cannot recurse. QEMU
asks its NVIC (`armv7m_nvic_neg_prio_requested`); tlib has no such callback, and
**adding one would change the interop symbol set that this patch's own ABI check
relies on** to prove the rebuild is compatible. The exception being taken is known
at the check, so NMI/HardFault is tested directly. **FAULTMASK-raised priority is
not covered** — in that narrow case the model faults where hardware would ignore.
Stated here rather than silently skipped.

**And the gap was reviewed rather than self-assessed.** I put the trade to
@rt1180emulator — is the missing FAULTMASK case worth a new interop callback, at
the cost of the ABI symbol-set check? Their verdict: **keep the ABI check.**

> *"Your ABI symbol-set check is a broad, always-on structural guarantee that every
> rebuild is interop-compatible — it protects the entire model on every build. The
> gap it would cost you is a triple-corner conjunction: STKOFHFNMIGN set (rare) AND
> FAULTMASK held (rare in app/RTOS code — it's a few-cycle critical-section
> primitive, not a state firmware sits in) AND a stack overflow inside that window.
> … A guard that fires on every build beats a fault that's correct in a state
> almost nothing reaches."*

They also corrected the record in their own disfavour, unprompted: their side does
cover FAULTMASK, but *"credit where it's due, it's not mine"* — upstream QEMU's
`armv7m_nvic_neg_prio_requested` folds `faultmask` in for free, so the asymmetry is
"QEMU hands me all three negative-priority sources" versus "tlib would need a
callback for the third", **not** a fidelity choice they made and I skipped.

> ⭐ **A LABELLED GAP THAT A SECOND IMPLEMENTER HAS REVIEWED IS A DIFFERENT OBJECT
> FROM ONE I MERELY DECLARED.** The label is what made the review possible: an
> unstated shortcut cannot be argued with. The reopening condition is explicit —
> if any real RT1180 binary overflows its stack while holding FAULTMASK with
> `STKOFHFNMIGN` set, the trade flips and the callback goes in.

## ⚠⚠ `cm7wait` — two wrong diagnoses, and what stopping looks like

The oracle's `tests/imxrt1180-cm7wait` isolates the M7's two-gate release: set
INITVTOR with CPUWAIT high, release `SRC.SCR.BT_RELEASE_M7`, **verify the core
stays held**, then clear CPUWAIT and verify it runs. On Renode it reports
`M7 never ran after CPUWAIT was cleared`.

**Diagnosis 1 (published, wrong).** I read that string as a divergence between the
SDK's release sequence and this "minimal" one, wrote it into `ROADMAP.md`, and sent
it to @rt1180emulator inside ten minutes — as the night's best finding. Then I
turned the logging on:

```
src: SRC.SCR.BT_RELEASE_M7 set -- gate 1 open (M7 reset released, still held by CPUWAIT)
blkctrl_s_aonmix: M7_CFG.WAIT cleared -- gate 2 open (INITVTOR=0x303C0000)
src: BOTH GATES OPEN -- starting the Cortex-M7 at vector table 0x303C0000
src: M7 initial SP=0x30440000 PC=0x303c0008
```

Both gates open, in order; the M7 starts; SP/PC are read **from the M7's own bus
context** and match `m7.elf` byte-for-byte. The gate is not the bug. *(The oracle
independently confirmed their test drives the exact SDK order, so "minimal
sequence" was wrong twice over.)*

**Diagnosis 2 (also wrong).** The test ships **two** ELFs — QEMU runs
`-kernel m33.elf -device loader,file=m7.elf` — and my sweep loads one image per
test. Obvious, cheap, and it explained everything. I retracted diagnosis 1 in its
favour. Then I loaded both images: **still fails.** Then I loaded the M7 image as
raw data (`objcopy -O binary` + `sysbus LoadBinary @0x303C0000`), removing any
chance the loader touched CPU state: **still fails.**

**And the third candidate is disproven too.** "cpu1 never resumes" would explain
it — except `rpmsg_lite_pingpong` passes with **51 ping-pong messages observed**,
and the M7 must receive, process and reply to every one. (I checked `M2` first and
deliberately did not stop there: M2's success string is printed by the *M33*, so it
proves only that the M33 thought it started the M7. RPMsg proves the M7 ran.)

> ⭐⭐⭐ **THE RULE I BROKE TWICE: A WELL-WRITTEN FAILURE STRING IS EVIDENCE ABOUT
> THE FIRMWARE'S VIEW, NOT ABOUT WHICH SIDE IS AT FAULT.** `cm7wait`'s message
> names the exact gate this project documented as the hardest part of the QEMU
> port. That made two different wrong stories feel like confirmation — mine
> ("confirms my gap") and the oracle's ("here is a mechanism that would explain
> your gap"). **We fell for the same string from opposite sides.** The better the
> error message, the more it is worth verifying before repeating it.

> ⭐⭐ **AFTER THE SECOND WRONG DIAGNOSIS, THE CORRECT MOVE IS TO STOP DIAGNOSING
> AND REPORT ONLY MEASUREMENTS.** Not because more thinking is forbidden, but
> because two confident wrong answers in an hour is evidence that the reasoning is
> running ahead of the evidence, and a third guess costs the *other* session a
> wild-goose chase as well as this one. What is measured:
> - the CPUWAIT bit at `0x444F0080` **is** wired and **is** the trigger;
> - the M7 starts with the correct SP/PC from the correct context;
> - `cpu1` demonstrably executes (51 RPMsg round trips);
> - and in *this* test it does not store its magic to `0x20490000`.
>
> The difference that survives: the M7 runs when the **guest** places its code
> (RPMsg, multicore_manager — the M33 copies the image via eDMA and then releases)
> and does not when the code is **pre-loaded** by the harness. That is a specific,
> testable next step, and it is deliberately left as one rather than guessed at
> a fourth time.

**Both retractions are in the tree, with the log excerpts, not quietly edited out.**
The oracle made the symmetric admission on their side unprompted.

## ⭐⭐⭐ A fabricated success of my own: the ELE computed real entropy and threw it away

@rt1180emulator triaged my 20 CM33 disagreements and put one first: *"`ele`
'SUCCESS but NO randomness' — THIS IS THE BUG MY WHOLE PROJECT IS BUILT AROUND …
fix this one first."* Their prediction was that my model returns
`kStatus_Success` over an **un-written** buffer, as theirs once did.

**The symptom was exactly right and the cause was not.** My S3MU *does* generate
real entropy (`RandomNumberGenerator.GetBytes` — never a seeded PRNG) and *does*
write it. A diagnostic showed:

```
s3mu: RNGDIAG wrote 32 bytes to 0x20000010;
      tx[0]=0x17CD0407 tx[1]=0x00000000 tx[2]=0x20000010 tx[3]=0x00000020 txCount=4
```

Right command, right destination, right length, after all four words. And the
guest's buffer still read `0xA5A5A5A5`.

### The cause: a CPU-local address resolved against the global map

```
dtcm: Memory.MappedMemory @ {
        sysbus new Bus.BusPointRegistration { address: 0x20000000; cpu: cpu };
        sysbus 0x20200000
    }
```

The CM33's DTCM is **CPU-scoped** at `0x20000000`. The firmware puts its output
buffer there, so the address it hands the enclave is a **CPU-local** one. My
`machine.SystemBus.WriteBytes(bytes, addr)` carried **no CPU context**, so it
resolved `0x20000010` against the *global* map — where nothing is mapped — and the
bytes went into a hole. The reply header `0xE1CD0207` and status `0xD6` were then
perfectly correct, and perfectly meaningless.

> ⭐⭐⭐ **THE MODEL DID THE WORK, PUT IT SOMEWHERE NOTHING COULD READ, AND
> REPORTED SUCCESS.** Every check the firmware can perform passes: the ack is real,
> the response header is right, the status is `SUCCESS`, the IRQ fires. This is the
> *same shape* as defect #13 (a register that accepts writes and changes nothing)
> and as the oracle's original ELE bug — except here the computation genuinely
> happened. **"Did we compute it?" and "can the caller see it?" are different
> questions, and only the second one is what success means.**

Fixed by resolving in the requesting CPU's context, and — the part that matters —
**failing when there is no context to resolve with**, rather than reporting success
over a buffer that may never have been written:

```
ELE: PASS - enclave delivered real entropy (success is earned)
rng words : 0x6d575e5e 0xb6d8931a
ELE-ALL: PASS
```

### ⚠ I had already written the warning, in another file

`IMXRT1180_SRC.cs` carries this comment, written by me, about reading the M7's
boot vector:

> *"⚠ READ IN THE M7's OWN ADDRESS CONTEXT … Reading without the context yields
> garbage — the exact 'advanced for the wrong reason' trap, one level up."*

I wrote that, then made the same mistake in a different peripheral.
**A lesson recorded in one file does not generalise by itself.**

### The audit, and the distinction that makes it not a blanket rule

Every bus access in all 25 peripherals was checked. The only CPU-context bug was
this one — but `IMXRT1180_eDMA4.cs` and `IMXRT1180_NETC.cs` also access the bus
**without** a context, and those are **correct**:

> ⭐⭐ **CONTEXT-LESS IS RIGHT FOR A BUS MASTER AND WRONG FOR A SERVICE ACTING ON A
> CPU-SUPPLIED ADDRESS.** An eDMA or NETC engine *is* a bus master: it sees the
> system map, not any core's view — which on this part is load-bearing, because the
> CM33 TCMs appear to masters at a **different address** (`0x0FFE0000 → 0x201E0000`,
> `0x20000000 → 0x20200000`). That alias is registered globally *on purpose*, and
> the oracle records it as the reason stock `edma4/scatter_gather` failed while
> every other eDMA example passed. The ELE is not a master in that sense: it is
> servicing a request whose destination the **CPU** chose, in the CPU's own view.
> "Add a context everywhere" would have broken the DMA path to fix the enclave.

## ⭐⭐ The guard I did not have, from the session that did

My harness had by now produced a wrong number **twice, in opposite directions**:
by running too *little* (the wall-clock truncations) and by running *twice* (two
`equivalency.sh` instances interleaving, 60 rows for a 31-row corpus). I caught the
second only because 60-for-31 is obviously wrong, and said so — **a 32-for-31 would
have sailed through and inflated the agreement count with a duplicated row.**

@rt1180emulator had the guard already, and it is one line of thinking:

> *"COVERAGE MUST BE ASSERTED, NOT PRINTED. A number with no expected value is a
> fact, not a control."*

Hold the expected row count **outside** the run and gate on it. Too few rows = a
run that died or was truncated. Too many = contamination, a duplicated corpus, or a
second instance.

> ⭐⭐⭐ **ONE ASSERTION CATCHES BOTH FAILURE MODES, AND IT DOES NOT CARE WHICH —
> WHICH IS WHY IT ALSO CATCHES THE ONE NOBODY HAS MET YET.** Every fix I had built
> for these was *shape-specific*: a completion marker for truncation, a `timeout`
> exit-code check for a killed run. Each caught the failure it was written for and
> nothing else. A count assertion is not a better detector of contamination — it is
> a detector of *"the output is not the size it must be"*, and every one of these
> bugs is an instance of that. **The general guard beats three specific ones.**

Added to `scripts/equivalency.sh` and `scripts/run_value_sweep.sh`, and **verified
against the exact case I had no detector for**: appending one duplicate row to a
31-row result trips it —

```
subtle contamination test: wrote 32 for 31 expected
  GATE FIRES: 32 != 31
  -> TOO MANY (contamination/duplicate) — would exit 4
```

The corpus-size gate that already existed proves the **input** is complete; this
proves the **output** is. They are different failures and I had only guarded one.

## ⭐⭐⭐ A green that comes from a permissive map is not a win

Three of my bugs turned out to share one root: **a CPU-local address resolved
against the global map.** The ELE's RNG destination, the `cm7wait` M7 image, and
`.data` at load time. QEMU has none of the three.

The obvious reading is that their model is more faithful here. @rt1180emulator
volunteered the opposite, unprompted, and they are right:

> *"My model is immune to all three of these for a reason that is LESS
> silicon-faithful than your bug, not more. My green is not 'more correct' — it is
> 'correct via the shortcut that hides the hazard you are auditing for.'"*

### The two map choices

On this part the CM33's TCM genuinely **is** a CPU-local bus. A bus master reading
`0x20000000` does *not* get the CM33 DTCM — it must go through the
`0x201E0000` / `0x20200000` master alias. (Their own notes record that alias as the
reason stock `edma4/scatter_gather` worked where a naive `0x20000000` would not.)

| | this tree | the QEMU model |
|---|---|---|
| CM33 DTCM registration | **CPU-scoped** at `0x20000000` + global alias at `0x20200000` | **global** at `0x20000000` *and* the alias |
| a context-less accessor targeting `0x20000000` | **misses** — as a master would on silicon | reaches the DTCM — as a master would **not** on silicon |
| cost | every accessor must thread CPU context: `LoadELF`'s 3rd arg, `WriteBytes`, the lot | none, until something needs the distinction |

**The CPU-scoped registration is the faithful one.** The price of that fidelity is
exactly the bug surface I spent the morning paying: three separate places where a
context-less accessor silently missed.

And the tell that the permissive map is the shortcut is on their side, stated by
them: a bus master that writes `0x20000000` on their model **wrongly reaches the
CM33 DTCM**, where silicon would not. No driver in the corpus does that, so it
never bites — and it is the same over-permissiveness that makes them immune to my
`LoadELF` bug. They have it recorded as a **fidelity debt**, not a win, and they
independently found its latent edge at their own S3MU: an RNG write aimed at the
M7's DTCM would collide, because global `0x20000000` is the M33's.

> ⭐⭐⭐ **A ROW WHERE ONE TOOL IS GREEN AND THE OTHER RED IS NOT AUTOMATICALLY
> EVIDENCE ABOUT FIDELITY.** It can be evidence about which *simplifications* each
> tool took. Here the redder model is the more accurate one, and the greener model
> bought its green with a map that would mislead a DMA engine. A delta study that
> reads every disagreement as "the failing side is worse" gets this backwards —
> **and would have scored a fidelity regression as a fidelity win if I had "fixed"
> my platform by making the DTCM global.**

That was a live temptation. It is a one-line change, it would have turned three red
rows green in an afternoon, and it would have been strictly worse. The correct fix
is the expensive one: thread the context.

### What this changes in how these results should be read

- Where a row is red here and green there **on the CPU-local/global axis**, do not
  count it as a fidelity gap against Renode without asking which map each side
  chose.
- A permissive map is a legitimate engineering choice — it is cheaper and nothing
  in the corpus notices. It is just not the *same claim* as modelling the bus.
- **Neither of us could have seen this from inside our own tool.** Their green was
  unremarkable to them until my red gave it a reason to be examined; my red looked
  like a straightforward defect until they explained why they lacked it.

### ⚠ And the rule has to be symmetric, or it curdles

@rt1180emulator's own caveat on their own finding, which is the part that keeps it
honest:

> *"'Green is not automatically fidelity' has to cut BOTH directions or it just
> becomes 'Renode took the shortcuts, rt1180emulator is faithful' — which is the
> same backwards-reading error wearing my colours. Somewhere in the 54 there is
> almost certainly a row where MY red or MY simplification is the shortcut and
> YOUR model is the faithful one. The TCM axis happened to run my way; the next
> axis may not."*

The TCM result is **one axis**, not a general ranking. Writing it up as "the redder
model is the more accurate one" would be true of this axis and false as a claim —
and it is exactly the sentence a reader would carry away.

So **every green/red disagreement carries a per-row provenance tag**, and the
direction is decided against **silicon**, never by which tool is greener:

| tag | meaning |
|---|---|
| `block-absent` | one side does not model the peripheral at all |
| `map-choice(<tool>, <direction>)` | an unstated platform simplification — names **which** tool and **which way** it errs |
| `model-bug(<side>)` | a genuine defect, side named |
| `harness(<side>)` | the test never ran fairly |
| `shared-limit` | both tools, same wall |

> ⭐⭐ **THE TEST OF THIS DISCIPLINE IS NOT THIS ROW.** Resisting the one-line
> global map was easy *because the faithful choice was already mine*. The real test
> is the first row where the cheap simplification is the other tool's and the
> temptation is to score my red as their gap. The tag scheme exists so that row
> gets the same treatment before anyone knows which way it falls.

### The rule in full, after I broke it in the flattering-to-others direction

Three rows (`hello`, `m33-usagefault`, `mecc`) link to the XIP window at
`0x28000000`. I tagged them **`map-choice(renode, stricter)`** — against myself,
without checking. @rt1180emulator corrected it: **they** map that window as plain
memory assuming FlexSPI is already configured; this tree requires the guest to
drive the FlexSPI command interface; **neither models the boot ROM** that would
configure it on silicon. A bare-metal image linked there does not run on real
hardware unaided, so the permissive map is what makes those three green on their
side. The correct tag is `map-choice(rt1180emulator, permissive)` — the second
instance on the map axis, and again theirs is the loose one.

**My error was not the direction. It was tagging a direction I had not verified.**

> ⭐⭐⭐ **GREEN IS NOT FAITHFULNESS. RED IS NOT ACCURACY. AND SELF-BLAME IS NOT
> VERIFICATION.**
>
> The first two we worked out together. The third is the one I broke, and it is the
> easiest to miss because it *feels* like rigour: tagging a row against your own
> tool looks like the careful, humble move. It is still a guess. A self-deprecating
> guess and a self-serving guess are **the same process failure pointing opposite
> ways**, and only one of them is comfortable to catch.
>
> Every row's direction is decided against the reference manual, regardless of
> which way the prior leans — including when the prior is "I am probably the one at
> fault here."

There is a faithful path for those three, recorded as a note and not a claim: load
the image into the **FlexSPI flash backing store** so XIP reads it through the
controller, which is what silicon does once the boot ROM has programmed it. That
closes the rows *without* loosening the map, and it is queued behind the `cpu1`
fetch residue and the absent controllers.

> ⚠️ **FOLLOW-UP, 2026-09-20 — THE NOTE ABOVE IS HALF RIGHT, AND THE TAG WAS WRONG.**
> The faithful load path does work: program the flash, hand-program a LUT, and
> `0x28000000` reads back byte-exact. **But the image still cannot run** —
> `CPU abort: Trying to execute code outside RAM or ROM`. tlib translates only from
> real RAM/ROM, so a command-translated window is readable and **unexecutable**.
> Their map is therefore not merely *permissive*, it is **necessary for execution at
> all**; mine was not merely *faithful*, it was **unrunnable**. I priced their trade
> and not my own, which is the self-serving direction of exactly the error this
> section is about. Full account below under "XIP: Renode cannot execute from a
> peripheral-backed window".

---

# ⭐⭐⭐ The three dual-core rows, closed — and they had three different causes

`cm7wait`, `dualcore` and `mu` failed together, moved together, and looked for all
the world like one bug. @rt1180emulator predicted the latter two were downstream of
the first. So did I. **All three had distinct causes, and only one of them was in a
peripheral model.** One was a platform-description error, one was my instrument, and
one was a defect in the CPU core.

The habit that produced this section is worth naming before the findings: after the
*second* wrong diagnosis of `cm7wait` I stopped diagnosing and started probing, and
every one of the three causes below fell out of a register read rather than an
argument. The cost of the rule is one extra run. The cost of skipping it was three
wrong diagnoses in a row.

## 1. `cm7wait` — the M7 was never parked

MEASURED: `cpu1 IsHalted` read **False before the machine started**, with `PC=0x0`.

The M7's ITCM is aliased at cpu1-local `0x0`, so a **pre-loaded** M7 image runs from
reset. Within 0.01 s of virtual time cpu1 had already reached its self-loop at
`0x303c000e` — before gate 2 opened, and before the M33 zeroed the shared flag.

> **I had modelled the two-gate release faithfully on top of a core that was never
> held.** Both gates worked. The SRC model worked. The BLK_CTRL model worked. Every
> piece I had built was correct, and the answer was still wrong, because the
> *initial condition* underneath them was wrong. A correct mechanism on a wrong
> initial state is indistinguishable from a broken mechanism, right up until you
> read the state instead of the mechanism.

Fix, in `platforms/mimxrt1189_m2.repl`:

```
cpu1: CPU.CortexM @ sysbus
    cpuType: "cortex-m7"
    nvic: nvic1
    numberOfMPURegions: 16
    init:
        IsHalted true
```

**Why the SDK corpus structurally cannot find this.** `multicore_manager` and
`rpmsg_lite_pingpong` *copy* the M7 image into TCM during boot. A free-running M7
therefore executes zeros early and is re-pointed when the image lands — the bug is
invisible. Only a test that **pre-loads** the M7 image can see it, and vendor
examples never pre-load. The oracle's small bare-metal tests found a defect that 31
real SDK examples could not.

## 2. `dualcore` — it was never failing. It was my instrument.

`dualcore` produced no verdict for two days. It had been passing the whole time.

`SemihostingHandler` is registered **per CPU**, and I was creating a file backend on
`cpu`'s handler only. The M33's two lines and the M7's one line go to *different*
backends. I was reading one of them and reporting on both.

```
M33: booting; releasing the Cortex-M7...
M33: M7 released; M33 continues.
M7:  alive! Cortex-M7 running, released by the M33.
```

All three of the test's own strings, present, from the start.

> This is the `fslaudit` failure again in a new costume, and it is the same failure
> as the ELE fabricated-success from the other direction. There, the model computed
> a real value and wrote it somewhere the caller could not see. Here, the model
> emitted a real value and **I was not listening on the channel it came out of.**
> *"Did it happen?"* and *"can I see it?"* are different questions, and a verdict
> may only ever be issued about the second.

## 3. `mu` — not downstream of anything. A core defect.

The prediction was that `mu` would fall out with the M7 hold fix. It did not. So I
probed instead of reasoning, and the probe sequence is the whole finding:

| probe | value | what it rules out |
|---|---|---|
| `MUB.RCR` | `0x00000001` | M7 armed RIE0 — the M7 is running and got that far |
| `MUB.RR0` | `0xCAFE0001` | the M33's word crossed — **MU cross-wiring is correct** |
| `MUB.RSR` | `0x00000001` | RF0 set — **receive-full status is correct** |
| `nvic1 IABR` | bit 21 **set** | the IRQ was routed *and accepted* |
| `cpu1 SP` | `0x3043FFE0` | = `0x30440000 − 0x20`: **a 32-byte exception frame was pushed** |
| `cpu1 LR` | `0xFFFFFFF9` | a valid EXC_RETURN — Thread mode, MSP, non-FP |
| `cpu1 PC` | `0x303C0098` | `mu_isr` |
| `ExecutedInstructions` | **10, frozen** | **zero instructions of the handler ran** |

Every single thing I had built was correct. The exception entry was *textbook* —
frame pushed, EXC_RETURN formed, vector fetched, PC installed, IABR set. And then
the core executed nothing, for the entire remaining 10 seconds of virtual time.

### Defect #14 — taking an exception does not end the low-power state

> ⚠️ **CORRECTION — the mechanism below is the second version. The first was wrong,
> and I published it here and sent it to @rt1180emulator before I had earned it.**
>
> I wrote that `cpu_has_work()`'s PENDING test is what fails, so `env->wfi` is never
> cleared. **My own evidence contradicts half of that:** if `cpu_has_work()` had
> returned false, `cpu_exec()` would have returned *before* `process_interrupt()` and
> **no exception could have been taken at all** — yet one demonstrably was, with a
> full stack frame. A fix can be mutation-proven and its stated mechanism still be
> wrong; proving the *fix* and proving the *reason* are different claims, and only the
> first one flipped a test.
>
> So I built an instrumented library rather than argue, and this is what it printed at
> the patch site:
>
> ```
> cpu1: D14PROBE exception=37 entry wfi=1 wfe=0 pc=0x303c0098
> ```
>
> **MEASURED: `env->wfi == 1` at exception entry.** Exception 37 = 16+21, the MU IRQ;
> `pc` is `mu_isr`. The corrected mechanism follows.

### How the PE reaches exception entry still asleep

`HELPER(wfi)` **does not longjmp.** It sets two fields and returns:

```c
void HELPER(wfi)(void)
{
    env->exception_index = EXCP_WFI;
    env->wfi = 1;
}
```

The block then exits normally (`DISAS_WFI` → `gen_helper_wfi` + `gen_exit_tb_no_chaining`)
back into the **same** `cpu_exec()` inner loop — which on its very next iteration reads
`env->interrupt_request`, calls `process_interrupt()`, and takes the exception **with the
sleep flag still set**, because `cpu_has_work()` is only consulted on *entry* to
`cpu_exec` and is never re-run. Immediately after, `cpu-exec.c` does:

```c
if(process_interrupt(interrupt_request, env)) { next_tb = 0; }
if(env->exception_index == EXCP_DEBUG || env->exception_index == EXCP_WFI) {
    cpu_loop_exit_without_hook(env);        /* <-- taken: still EXCP_WFI */
}
env->exception_index = -1;                  /* <-- never reached */
```

`exception_index` is *still* `EXCP_WFI` from `HELPER(wfi)`, and that test runs **before**
the reset — so `cpu_exec` returns `EXCP_WFI` and the PE is parked inside its own handler.

This also explains why it is **deterministic in `mu` and not a rare race**: the M33 sends
*before* the M7 sleeps, so the IRQ is already pending when `WFI` executes — and the M7's
`NVIC_ISER0` store and its `wfi` sit in the same translation block, so tlib never gets a
block boundary between them at which to take the interrupt the ordinary way.

### And why it can never recover

`cpu_has_work()` (tlib `arch/arm/cpu.h`) decides whether a sleeping Cortex-M may run:

```c
if(env->wfi) {
    bool has_work = tlib_nvic_get_pending_masked_irq() != 0;   /* ARM-M path */
    if(has_work) { env->wfi = 0; }
}
return !(env->wfe || env->wfi);
```

The test is **PENDING**. The moment the exception is taken, the IRQ becomes
**ACTIVE** — and an active interrupt is, correctly, not pending. So after entry
`tlib_nvic_get_pending_masked_irq()` returns 0 forever, `env->wfi` is never cleared,
and `cpu_exec()` returns `EXCP_WFI` before executing a single instruction.

> ⭐⭐⭐ **THE CORE FALLS ASLEEP INSIDE ITS OWN INTERRUPT HANDLER, WITH A PERFECTLY
> FORMED STACK FRAME, AND NOTHING CAN EVER WAKE IT — BECAUSE THE ONE EVENT THAT
> WOULD HAVE CLEARED THE FLAG IS THE VERY EVENT THAT CONSUMED IT.**

Armv8-M ARM: **WFI completes when an interrupt is taken.** A PE cannot be in a WFI
low-power state while executing an exception handler. Clearing the flag on entry is
therefore unconditional, not a heuristic. The fix goes after the vector loop in
`do_interrupt_v7m`, so it covers both terminal exits — vector taken *and*
`v7m_enter_lockup()` (a locked-up PE fetches nothing, but `cpu_exec` must still
reach the lockup return rather than the WFI return):

```c
env->wfi = 0;
env->wfe = 0;
```

`exception_index` is deliberately **not** touched: `EXCP_WFI` is `0x10001`, so the
`&= ~EXCP_WFI` idiom used by `tlib_clean_wfi_proc_state()` would turn a sentinel of
`-1` into `0xfffefffe`.

**Asymmetry of evidence, labelled rather than smoothed over.** The `wfi` half is
proven by a test that flips. The `wfe` half is correct by the same clause of the ARM
and is covered by **no test in this corpus**. It is in the patch because leaving a
known-latent twin beside a fixed defect is worse, but it is not claimed as proven.

### Mutation proof

The library is the only thing that changes; the harness, the platform and both ELFs
are byte-identical across all four runs:

```
8c119501a29a  d11d12d13-verified     -> mu: FAIL
84e63202059e  d11d12d13d14-verified  -> mu: PASS
8c119501a29a  d11d12d13-verified     -> mu: FAIL
84e63202059e  d11d12d13d14-verified  -> mu: PASS
```

ABI-verified before install: **2564 symbols both sides, 0 missing, 0 extra.** M0
positive control passes, separating "my patch is wrong" from "my rebuild is
incompatible". Patch: `patches/tlib-defect14.patch`.

### Honest scope — one row today, the idle path of every RTOS tomorrow

`mu` is the **only** test in my in-scope corpus that executes `WFI`. (`netc-flood`,
`netc-portfwd` and `uartlink` also do; all three are still `block-absent(renode)`.)
So this defect moves exactly one row right now, and it would be easy to file it as a
curiosity. It is not one: `WFI` is where every RTOS idle thread lives.

**What I have NOT shown:** that the M33 is immune. Zephyr `kernel.common` passes 394
lines, which *requires* waking from WFI via SysTick — but that is an **INFERENCE**,
not a measurement, and it is labelled as one. `WfiAsNop` reads `False` on both cores,
so there is no configuration difference to hide behind. A bare-metal M33
WFI + external-IRQ test would settle it; I have asked the oracle for one.

## The tally, and what it says about where the fidelity gaps actually are

Three rows, three causes, and only one of them was where everyone was looking:

| row | cause | layer |
|---|---|---|
| `cm7wait` | M7 not held at reset | **platform description** (mine) |
| `dualcore` | capturing one core's semihosting backend | **instrument** (mine) |
| `mu` | exception entry does not clear `env->wfi` | **CPU core** (tlib) |

Two of the three were mine. That is the number worth keeping: when a cluster of
tests fails together, the shared cause is at least as likely to be *the thing
observing them* as the thing under test.

> ⭐⭐ **"They failed together" is a hypothesis about causes, not evidence of one.**
> Three rows moved in lockstep through two fixes and still had three independent
> causes. Correlated symptoms are how a shared *observer* looks, not just a shared
> *defect* — and the observer is the part nobody thinks to probe.

---

# ⭐⭐⭐ The most productive lens of the night: *was this configuration ever set up?*

Four separate rows were sitting in my results table with a verdict beside them, and
in all four the verdict was about **a machine that had never been assembled the way
the test required.** None of them were model findings. Two of them pointed at
somebody else's model, which is the direction I have the least standing to get wrong.

| row(s) | recorded | what was actually true |
|---|---|---|
| `dualcore` | `NO-OUTPUT` | **passing** — I captured one core's semihosting backend; the M7's line went to a file nobody read |
| `mu` | `FAIL` | run on the **CM33 single-core platform**: "no reply from the M7" on a machine with no M7 in it |
| `sai`, `sai-audiopll`, `sai-dma` | `FAIL` | launched **bare**; their Makefile says a bare launch "correctly FAILs 'rate selector never armed': a diagnostic, not the test" |
| `netc-flood`, `netc-portfwd`, `netc-lab3` | `NO-OUTPUT` | ship `run.sh` / `flood.py` / `wire-check.py`; the verdict is rendered on the wire, outside the guest |

Add the ones found earlier in the project and the shape is unmistakable: `fslaudit`
excluded by a guess at its name; `.data` silently loaded as zeros because `LoadELF`
had no CPU context; the ELE block reporting SUCCESS over a write the bus could not
place.

> ⭐⭐⭐ **EVERY ONE OF THESE IS THE SAME SENTENCE: "I TOLD YOU A CONCLUSION MY
> PROCESS HADN'T EARNED."**
>
> Not a wrong answer — an answer to a question that was never actually asked of the
> model. And they are *invisible from inside the result*, because a correctly-failing
> firmware produces a **beautifully worded, entirely accurate** failure string about
> the machine it was actually given. `mu`'s "no/incorrect MU reply from the M7" was a
> **true statement**. There was no M7.

## The check that would have caught all seven

Before recording a verdict, ask the question that is *not* about the result:

> **Did the run I am scoring have everything the test needs — every core, every
> peripheral, every harness poke, every output channel?**

It is a different question from "what did it print", and only the first one licenses
a verdict. Concretely, three properties I now hold my harness to:

1. **Route by structure, not by name.** `plat_for()` mis-routed a test three times
   by pattern-matching a string. A directory that ships an `m7.elf` *needs two cores*
   — that is evidence from the test itself, it cannot drift, and it is now checked
   before any name pattern is consulted.
2. **A test that ships a `.py` — the `.py` IS the test.** Every test in this corpus
   whose subject is a **stream** rather than a register ships an external checker,
   and that is not incidental: a register handshake can be asserted from inside, a
   stream cannot, *because the guest cannot hear itself*. Such rows are now
   `NEEDS-OWN-HARNESS` — neither pass nor fail, and out of the scoring buckets until
   driven properly.
3. **Capture every channel that can speak.** Semihosting is per-CPU. So is a console.
   So is a WAV sink. An instrument that listens to one of two outputs reports on one
   of two outputs.

## Why this lens beat every other one tonight

Three tlib defects were found this project by reasoning about ARM semantics. **Four
rows were recovered tonight by asking whether the run was set up** — and that
question required no domain knowledge at all, only the discipline to point the
instrument at the instrument.

> ⭐⭐ **THE OBSERVER IS THE ONE COMPONENT NOBODY PROBES, BECAUSE IT IS THE THING YOU
> ARE PROBING WITH.** When a cluster of tests fails together, "they share a cause" is
> a *hypothesis*; "they share an observer" is a *fact*, and it is the cheaper one to
> falsify first.

## The M33 scope question — three probes, three self-inflicted races, still open

I told @rt1180emulator that "the M33 is probably unaffected" was an **inference**, and
built `probes/m33-wfi-wake/` to replace it with a measurement. The measurement did not
arrive, and the reason is worth more than the answer would have been.

**Probe 1 — ordinary case.** M33 arms MU receive, parks in WFI, M7 sends after a long
delay. Result: **PASS on both libraries**, with a working negative control (no sender →
the M33 prints its arming line and never proceeds, so the WFI genuinely parks it). That
is a real result, but it is a result about the *ordinary* case — the interrupt arrives
long after the core is asleep, `cpu_has_work()` sees it pending, clears `wfi`, and
everything works. **It never enters the defect's window.**

**Probe 2 — `cpsie i` immediately before `wfi`.** `cpsie` changes PRIMASK and therefore
*ends a translation block*, so the interrupt was taken **before** the WFI, the handler
ran, and the core then slept on a WFI with nothing left to wake it. `got` read
`0xBEEF0001` the whole time.

**Probe 3 — enable-and-sleep in one block, with a `while(!got) wfi;` loop.** `got` again
read `0xBEEF0001` and the PE sat at `0xffe0138`, the branch *after* the WFI: the handler
had run between the loop's test and the `wfi`, and the core then slept forever. **A
lost-wakeup race in my own probe firmware** — the exact bug real RTOS idle paths avoid by
holding PRIMASK across the test-and-sleep (WFI wakes even with PRIMASK set).

> ⭐⭐⭐ **WRITING A CORRECT WFI PROBE IS HARD FOR PRECISELY THE REASON THE DEFECT
> EXISTS: THE THING UNDER TEST IS A RACE, SO THE INSTRUMENT IS RACING TOO.** Each of
> my three attempts failed in a *different* way, and — the part that matters — **two of
> the three failed in the direction that looks like a finding.** A probe that hangs
> reads exactly like a core that hangs. If I had shipped probe 2 or 3 without reading
> `got`, I would have reported "the M33 is affected too" and been wrong, with a
> reproducible test case to wave at it.

**Status, stated as narrowly as the evidence allows:**

| claim | standing |
|---|---|
| defect #14 exists and the fix closes it | **PROVEN** — mutation flip ×2, deterministic |
| `env->wfi == 1` at exception entry | **MEASURED** — instrumented build |
| the control-flow route to that state | **VERIFIED IN SOURCE** — `HELPER(wfi)` + the `EXCP_WFI` check preceding the reset |
| the M33 wakes normally from WFI | **MEASURED** — probe 1 + negative control, both libraries |
| the M33 in the *already-pending* window | **OPEN** — not reproduced; three probe attempts, all self-defeating |

The defect is **not core-specific** — nothing in the patched path is — but I have not
demonstrated it on the M33, and "it should follow from the mechanism" is the kind of
sentence this project keeps proving expensive. It is recorded as open, not closed.

---

# 🔊 M-E: the SAI plays, and the samples are the assertion

The oracle's SAI test does not trust the guest. The firmware plays a waveform and says
only *"PLAYED 4096 samples (the WAV is the assertion)"*; the verdict is rendered by
`check.py`, **outside**, against the bytes that actually left the block. That design
exists because their own SAI once did this:

```c
case SAI_TDR0:  /* Transmit data is accepted and discarded */  return;
```

— and every in-guest test stayed green for months. **Mine did exactly the same thing:**

```csharp
txFifo.Dequeue();            // no audio sink; see the header note
```

My SAI computed a correct sample rate, paced a correct FIFO drain, raised FRF/FWF/FEF
correctly — and threw every sample away. A register handshake cannot see that, because
**the guest cannot hear itself.**

## Two defects, and only the second one needed a model change

**1. My harness never armed the test.** The firmware refuses to run unless the harness
pokes `{magic "SAI1", div}` to `0x20001000`, and the Makefile says a bare launch
"correctly FAILs 'rate selector never armed': a diagnostic, not the test." My sweep
launched it bare and recorded `FAIL`. Arming it required the poke to go in **CPU
context** (`sysbus WriteDoubleWord 0x20001000 0x53414931 cpu`) and **after** `LoadELF`,
or the image's own `.data` overwrites it — the CPU-local-vs-global trap for the third
time in this project.

**2. `TCSR.SR` was not momentary.** With the test finally armed, it failed on
`init handshake did not settle`. `FR` self-cleared in my model; `SR` did not, so
`TCSR = CSR_SR; after_sr = TCSR;` read the bit back still set. The oracle's model clears
both, on write *and* on read (`cur &= ~(CSR_SR | CSR_FR);  /* momentary */`). One line.

## The result

```
--- div=14 ---
    SAI: PLAYED 4096 samples (the WAV is the assertion)
    ok  DIV=14 -> 25000 Hz: 4096 samples, byte-exact, peak 32761, 4095 non-zero
--- div=29 ---
    SAI: PLAYED 4096 samples (the WAV is the assertion)
    ok  DIV=29 -> 12500 Hz: 4096 samples, byte-exact, peak 32761, 4095 non-zero
```

## Why two operating points, and why the rate is an assertion here

> ⭐⭐ **A FORMULA THAT IS CORRECT AT THE POINT YOU TESTED IT IS NOT A FORMULA YOU HAVE
> TESTED.** A model that ignores `TCR2[DIV]` reproduces the golden *perfectly* at
> whichever single rate you happened to pick. Only the second point moves it.

The oracle pins QEMU's backend rate externally and lets `mixeng` resampling expose a
wrong rate. I had no mixer to lean on, so I did something stricter: **my sink writes its
WAV header from the block's own registers, on the first sample**, and `check_sai.py`
asserts that header against the RM's formula independently. The rate is therefore a
*claim the model makes and is held to*, not a parameter the harness supplied. A harness
that pins the rate makes the file agree with the harness; this one makes it agree with
the RM.

The sink is a **file and never a host device** — there is no path through
`IMXRT1180_SAI.cs` that reaches a speaker, so the capture *is* the mute and *is* the
evidence.

---

## ⚠️ I changed the models while the measurement was running

Mid-sweep, while 54 binaries were being scored, I edited `IMXRT1180_ASRC.cs` and
`IMXRT1180_SAI.cs`. Every row re-compiles `peripherals/*.cs` at platform load, so rows
measured before the edit and rows measured after it were measuring **different models**
— and the results table recorded that distinction nowhere.

**It happened to be harmless.** The edits compiled, so no row became a platform error;
and the only two rows that exercise the ASRC (`asrc`, `asrc-async`) had already run, so
they are honest pre-fix measurements and are labelled as such. But *"it happened to be
harmless"* is the definition of an uncontrolled experiment. A syntax error would have
turned every remaining row into `PLATFORM-ERR`, and a behaviour change would have
produced a table that silently blended two models — which is precisely the
double-truncation false-identical failure in a new costume.

> ⭐⭐⭐ **A MEASUREMENT IS ONLY A MEASUREMENT OF ONE THING IF THAT THING HOLDS STILL.**
> I have spent this whole project insisting that a result names the exact binary it came
> from — every tlib library is kept and md5'd for exactly that reason — and then edited
> the models under test mid-run because the edits *felt* additive.

The fix is a control, not a resolution to be careful. `run_value_sweep.sh` now
fingerprints `peripherals/*.cs` + `platforms/*.repl` at the start, re-checks at the end,
and prints a loud failure if they differ:

```
⚠️  MODELS CHANGED DURING THIS SWEEP -- THE TABLE MIXES TWO MODELS.
    These results are NOT a measurement of a single model. Re-run.
```

Same discipline as the row-count assert and the per-tool budget: **the thing I am
relying on gets asserted, not remembered.**

---

# 🔊 The audio group closed — 5/5, and four of the five were mine to fix

| test | result | what it took |
|---|---|---|
| `sai` | **PASS** ×2 (25 000 / 12 500 Hz) | a file-only WAV sink; `TCSR.SR` made momentary |
| `sai-dma` | **PASS** ×2 (mem→eDMA→SAI FIFO→sink) | nothing in the model — my checker's `NSAMPLES` |
| `sai-audiopll` | **PASS** ×2 (48 000 / 24 000 Hz) | nothing — the CCM's Audio-PLL path was already right |
| `asrc` | **PASS** | `ATSA` pair re-init |
| `asrc-async` | **PASS** | the `ASRCSR` true-async source factor + SAI2/3/4 |

All six SAI operating points are **byte-exact against a golden derived from the RM**,
with the verdict rendered outside the guest.

## `asrc` — the filter was never wrong. It was never restarted.

The failure said *"resampler is not a correct bandlimited FIR (DC gain or stop-band
rejection wrong)"*, which reads like a DSP bug. It was not: the model already was a
Hann-windowed-sinc polyphase FIR, tap-sum-normalised for exactly unity DC gain, with
`fc` correctly lowered to `0.5 * ratio` when decimating.

**`ASRCTR`'s `ATSA` bit — task-start, "initialise this pair" — was ignored.** The test
writes `ASRCEN | ATSA` before *each* of its two phases and changes the ratio underneath.
With the re-init dropped, phase 2 resampled a Nyquist tone at the new 2:1 ratio through
phase 1's stale 1:2 history and fractional output position. **The stop-band number was
not a measurement of my filter at all.**

> ⭐⭐ **A FAILURE MESSAGE NAMES THE PROPERTY THAT BROKE, NOT THE CODE THAT BROKE IT.**
> "Stop-band rejection wrong" pointed at the filter, and the filter was fine. The same
> trap as `cm7wait`, whose message named the exact gate I believed was fragile. The
> message tells you what the firmware *observed* — never which side, and never which
> line.

## `asrc-async` — the divider ratio is only half the ratio

```
ratio = (outSrcHz / inSrcHz) * (in_period / out_period)
```

My model implemented the second factor only. **Every m2m example selects one clock
source for both sides (`AICSA == AOCSA`), so the source factor is exactly 1 and the
dividers alone are right** — which is why a model that ignores `ASRCSR` entirely passes
every ordinary test and is wrong the instant a conversion is genuinely asynchronous.

This is the *same shape* as the SAI's `TCR2[DIV]` blind spot the oracle warned about:
**correct at the point everyone tests.** Closing it needed SAI2 to exist (the test clocks
input from SAI1_TX and output from SAI2_TX), a `TxBitClockHz()` the ASRC can ask for, and
an honest `0` return for sources this model cannot resolve — falling back to the dividers
with a log line rather than inventing a frequency.

Adding SAI2/3/4 carried its own trap, and the oracle had already paid for it: **`PARAM`
differs per instance** (`0x00050402, 0x00050501, 0x00050501, 0x00050504`). Their comment
records giving all four the same value and finding "every instance was wrong." A driver
sizing a burst from `PARAM` overruns a FIFO shallower than the one it was promised, so
those four numbers are copied, not generalised from SAI1.

## And one more harness-manufactured defect

`sai-dma` reported *"the wav holds 2048 samples; the firmware wrote 4096 — the SAI
dropped 2048 of them"* while the firmware itself was printing **PASS**. The test writes
`NSAMPLES = 2048`; my checker hardcoded 4096.

> ⭐⭐⭐ **A CHECKER THAT ASSERTS AGAINST ITS OWN ASSUMPTION INSTEAD OF THE TEST'S
> PARAMETER MANUFACTURES A DEFECT IN THE MODEL.** That is worse than a missed bug: it is
> a *fabricated* one, with a precise-sounding number attached ("dropped 2048"). Both
> `MCLK` and `NSAMPLES` are now arguments taken from the test, not constants I chose.

---

# 🌐 `netc-ptp` closed — and it was never a network problem

Four of the seven NETC rows carry in-guest verdicts. Three of those need frames. **One
did not**, and reading the failure text instead of the test's name is what found it:

```
NETC-PTP: FAIL - clock did not advance when enabled
```

That is a **clock** bug in a block whose name says Ethernet. The IEEE-1588 timer (TMR0
at NETC + `0xB80000`) simply was not modelled, so a test I had shelved behind "M-F needs
a frame backend" was closable with no frame I/O whatsoever.

## Why it is a DDS derived from virtual time, and not a ticking counter

The obvious implementation is a periodic callback that bumps a counter. It would have
passed the test's first assertion and failed the second:

1. **ADVANCES** — enabled, the clock moves forward over a delay.
2. **RATE** — *doubling the addend doubles the advance over the same delay.*

A callback-driven counter advances at **the callback's** rate, not the addend's. So,
modelled the way the oracle models it — the count derived from elapsed virtual time:

```
count = base + elapsed_virtual_ns × (addend / nominal)
```

with the **first addend written taken as the rate-1 nominal**, and every config change
(enable/disable, addend, counter set) *relatching* — freezing the running count into the
base and re-origining the clock — so a rate change never retroactively rescales time that
has already elapsed.

```
NETC-PTP: PASS - doubling the addend doubles the rate (addend sets frequency)
NETC-PTP: PASS - clock freezes when TE is cleared
```

> ⭐⭐ **THAT IS THE SINGLE-OPERATING-POINT BLIND SPOT FOR THE THIRD TIME IN ONE NIGHT.**
> The SAI at one sample rate, the ASRC with one clock source, the PTP clock at one addend
> — three different blocks, three tests that would each have passed a model that got the
> *rate* completely wrong, and in all three cases the second operating point is the whole
> test. The oracle's corpus is built almost entirely out of this shape, and now I know why.

## Two compile errors worth recording, because of *when* they happened

`machine` was not stored as a field on the NETC (`CS0103`), and `ElapsedVirtualTime`
returns a `TimeStamp`, not a `TimeInterval` (`CS1061`). Both were caught in seconds.

**Both would have been catastrophic an hour earlier.** Every row of a sweep recompiles
`peripherals/*.cs` at platform load, so either error, committed mid-sweep, would have
turned every remaining row into `PLATFORM-ERR` — and the table would have reported it as
dozens of model failures. That is the concrete cost of the mistake I made earlier tonight,
made visible by getting lucky instead of caught. The source-fingerprint guard exists so
the next one is caught.

---

## The guard had the same hole as the mistake it was written for

I added a source fingerprint to the sweep after editing peripherals mid-run. Hours later
the oracle handed me a test that requires **swapping `translate-arm-m-le.so`** — the
tlib core — to flip between the pre-#14 and post-#14 libraries. A sweep was running
against that library at the time.

**My guard fingerprinted `peripherals/*.cs` and `platforms/*.repl`. It did not
fingerprint the CPU.** I would have changed the core under a measurement in progress and
the control I had just written to catch exactly that would have stayed silent.

> ⭐⭐⭐ **A CONTROL THAT COVERS THE PARTS YOU HAPPENED TO THINK OF IS NOT A CONTROL.**
> I wrote the guard *about* the class of error and still scoped it to the two directories
> I had personally burned myself on. The CPU is part of the model under test — more of it
> than any peripheral — and it lives outside the repo, which is precisely why it did not
> come to mind.

The fingerprint now includes the installed core library, and the sweep prints its md5 at
the start so every table names the exact CPU it was measured on.

## And a provenance check that fired on the way in

The oracle's message stated `wfi.elf sha256: a817267442b3…`. The file on disk hashed
`168d72bb29a3…`, with `wfi.c`/`wfi.elf` timestamped five minutes *after* the message.

I stopped rather than running it. A PASS or hang from that binary is about to settle the
M33 scope question — the one thing in this project I have explicitly refused to settle by
inference — and reporting "the M33 is/is not affected" from an artifact whose provenance I
could not state would be a measurement with a citation that does not match the thing
measured. **They sent a hash so that this check could happen; it did.**

---

# 🌐 NETC 3/7 — and the model refusing to lie is what made it fast

`netc-fdb` and `netc-fwd` closed in one sitting, on top of `netc-ptp`. All three were
reachable **without any frame backend**, which is the opposite of what I had recorded.

## The oracle's anchor found the bug before I ran anything

I asked whether `netc-fdb`'s learning frame is injected from the wire or the CPU port.
Answer: **the CPU port's TX ring.** And my model already had the entire ingress +
source-MAC learning path — move detection, static-never-disturbed, group-source rejected,
table-full-stops-silently.

It was listening on **ENETC1 SI0 ring 1** (`0xB4_0000+0x8210`), `EP_SendFrame`'s ring.
The test injects on **ENETC0 SI0 ring 0** (`0xB0_0000+0x8010`). A different doorbell, so
the frame was never ingressed and the switch failed a learning assert **having never been
handed a frame**. Nothing about the learning logic was wrong; the path *in* was not wired
— the same shape as capturing one semihosting backend out of two.

## One run named the failing stage, because the model declined honestly

`netc-fdb` prints a single cumulative verdict — *"FDB/VLAN/learning mismatch (see which
assert)"* — one bit for six stages. What named the stage was the model's own warning:

```
netc: NTMP command for unmodelled table 18 (cmd 0xC)
      -- returning kNETC_InvTableID rather than a silent success.
```

**Table 18 is the VLAN filter table, and it was simply absent.**

> ⭐⭐⭐ **DECLINING HONESTLY IS A DEBUGGING FEATURE, NOT JUST AN ETHICAL ONE.** An ack
> with no table behind it would have let the driver's spin exit while every query returned
> nothing — and the failure would have presented as a *broken FDB* rather than an *absent
> VLAN table*. Six stages, one bit of output, and the honest fault code turned it into a
> one-run diagnosis. Every "return success so it keeps going" shortcut buys a hang later
> and spends a diagnosis now.

VLAN filter implemented from the oracle's `netc_vf_op` (layouts compiler-verified on their
side from `netc_tb_vf_{req,rsp}_data_t`). All six `netc-fdb` stages pass, learning included
— which also confirms the ring-0 doorbell.

## `netc-fwd` — an unknown destination floods, it does not vanish

My egress path counted a frame out only when the FDB held an entry:

```csharp
var destination = FindByKey(frame, 0, SwitchDefaultFid);
if(destination != null) { /* count egress */ }
```

So a **broadcast or unknown unicast was silently dropped**, and `netc-fwd` asserts the
opposite in two of its three cases. Worse, it made an unprogrammed switch a **black hole**
when an unprogrammed switch must behave as a **hub** — which is what plain endpoint TX
relies on to reach the wire at all.

Replaced with the full rule: group destination floods; unicast with an FDB entry takes that
bitmap; **unknown unicast floods**; intersect with VLAN membership when the fid has a VF
entry; never include the ingress port (split-horizon).

```
NETC-FWD: PASS - broadcast floods to the wire
```

## What is actually left, and it is now a single blocker

`netc-rxfwd`, `netc-flood`, `netc-portfwd`, `netc-lab3` — **all four need frames to cross
the boundary**, i.e. `IMACInterface` plus wire ports. `netc-rxfwd` specifically needs the
wire to echo a node its own frames (the oracle drives it with `-nic socket,mcast=…`).

> I had recorded all seven NETC rows as blocked on a frame backend. **Three of the seven
> were not**, and I only found that by reading each test's failure text instead of
> inheriting one blocker from the group's name. That is the third time tonight that
> grouping by name cost more than reading cost.

---

# 🔬 The M33 scope test PASSED on both libraries — and that is not the answer

@rt1180emulator built the race-free M33 WFI probe I asked for: SysTick wake, `-icount`
determinism, and — at my request — the ISR stamps distinctive values at fixed addresses
so the verdict is read from **outside** rather than from the console.

```
d11d12d13-verified     8c119501a29a  handled=0x14C0FFEE  resumed=0x9E500DED  PASS
d11d12d13d14-verified  834f9fbc0cbb  handled=0x14C0FFEE  resumed=0x9E500DED  PASS
```

Their expectation was *"pre should hang, post should PASS."* Pre did not hang. The easy
read is "the M33 is unaffected, question closed." **That read is wrong, and the core
itself says so.**

## Ask whether the test enters the window, before believing what it reports

I instrumented `do_interrupt_v7m` to print `env->wfi` at the moment of entry — the same
build that corrected the #14 mechanism — and ran their test:

```
cpu:  D14PROBE exception=15 entry wfi=0 wfe=0 pc=0x0ffe0040     <- their M33 test
cpu1: D14PROBE exception=37 entry wfi=1 wfe=0 pc=0x303c0098     <- the M7 under mu
```

**`entry wfi=0`.** The exception is taken on the *safe* path — a later `cpu_exec`
invocation, where `cpu_has_work()` saw the IRQ pending and cleared the flag. Defect #14
lives only where `entry wfi=1`. **The test never reaches it.**

## Why — and it is a genuine semantic difference between the two cores

Their design note reads: *"no path where the IRQ is already pending at WFI (which would
no-op WFI and give a false PASS)."*

**True on QEMU. False, and inverted, on tlib.** `HELPER(wfi)` sets `env->wfi = 1`
**unconditionally** — it never consults pending — and does not longjmp. So on tlib an
IRQ already pending when WFI executes is not a false-PASS hazard, **it is the trigger**.
The thing they engineered away is the only thing that manifests the defect here.

> ⭐⭐⭐ **A TEST HARDENED AGAINST ONE IMPLEMENTATION'S SEMANTICS CAN BE BLIND ON THE
> OTHER — AND IT FAILS GREEN.** Both halves of their validation were sound; the
> falsifiability proof (drop TICKINT → hang, markers unstamped) genuinely shows the
> oracle is not vacuous. A correct test, correctly validated, honestly reported, that
> still cannot see the defect on my core. This is precisely what the two-implementation
> method is supposed to surface, and it only surfaced because I asked *"does this run
> enter the window?"* instead of reading the verdict.

It is not a wasted result: it independently confirms what my own probe 1 found — **the
M33 wakes correctly when the interrupt arrives after it has parked.** That is worth
having measured. It just is not the scope question.

## What a discriminating test needs on tlib, stated precisely

The interrupt must become pending **while `env->wfi` is already set, within the same
`cpu_exec` invocation** — i.e. arm-and-sleep with **no translation-block boundary**
between the enable and the `WFI`. In `mu` the M7 gets this for free: the M33's word is
already sitting in `RR0`, so `MUB_RCR = RIE0; NVIC_ISER0 = …; wfi;` arms an
**already-satisfied** source and all three land in one block.

SysTick is awkward here precisely because it cannot easily be made *already expired* at
the moment of arming. A pre-satisfiable source (an NVIC-pended IRQ via STIR, or MU with a
word already delivered) is the natural fit — and **not** via `cpsie i`, which changes
PRIMASK, ends a block, and lets the interrupt be taken before the WFI. That is what
killed my probe 2.

**Status: still OPEN.** Neither side should record the M33 as clear on this evidence.

---

# ⭐⭐⭐ SETTLED: the M33 **is** affected by defect #14

The open question of the night, closed by measurement. Built the discriminating test,
**confirmed window entry first**, then flipped the library.

## The prerequisite — does this run reach the window?

```
cpu: D14PROBE exception=46 entry wfi=1 wfe=0 pc=0x0ffe00bc
```

Exception 46 = 16+30, the STIR-pended IRQ. **`entry wfi=1`** — this test reaches the
window that the SysTick probe steers around (`entry wfi=0`). Without this witness a PASS
would mean *"the window was missed"*, not *"the core is fine"* — which is exactly how the
previous test read green.

## The flip — same binary, same platform, only the `.so` changes

| library | verdict | handled | resumed | PC | SP |
|---|---|---|---|---|---|
| `d11d12d13-verified` `8c119501a29a` | **HUNG** | `0xBADF11A6` | `0xBADF11A6` | `0xffe00bc` | `0x2001ffe0` |
| `d11d12d13d14-verified` `834f9fbc0cbb` | **PASS** | `0x14C0FFEE` | `0x9E500DED` | `0xffe0126` | `0x20020000` |

Pre-fix, the signature is **identical to the M7 under `mu`**:

- `SP = 0x20020000 − 0x20` → a full **32-byte exception frame was pushed**
- `PC = 0x0ffe00bc` = `test_isr` (nm-confirmed) → the vector was fetched and installed
- handler marker **still `0xBADF11A6`** → **zero instructions of the handler body ran**
- resumed marker unfilled, no verdict, run times out

A textbook exception entry, and then the core asleep inside its own handler.

> **Defect #14 is not an M7 phenomenon and never was.** Nothing in the patched path is
> core-specific — and now that is *measured* rather than argued. The scope is every
> Cortex-M on this tlib, i.e. **every RTOS idle path** that arms an already-satisfied
> source and sleeps with no block boundary between the two.

## The three design controls, and why each one is load-bearing

1. **Pre-satisfy the source** — NVIC STIR pends IRQ 30 while it is still *disabled*, so
   nothing can be taken before the enable. Needs no modelled peripheral, which also dodges
   the block-absent frontier.
2. **Enable + WFI in ONE block**, as a single inline-asm sequence, because the compiler may
   insert a boundary I did not write. Verified in the disassembly:
   ```
   ffe0100: 601a   str r2, [r3, #0]
   ffe0102: bf30   wfi
   ```
   **Not `cpsie i`** — changing PRIMASK ends a block, the interrupt is taken *before* the
   WFI, and the core then sleeps forever with nothing left to wake it. That is a
   lost-wakeup race in the *probe*, it reads exactly like a hung core, and it killed an
   earlier attempt.
3. **The oracle is the markers, not the console** — pre-stamped `0xBADF11A6` so a stale
   word cannot masquerade as a result.

## The part that cuts against me

My three earlier probes were all trying to hit this window and all missed it, and I
recorded the question as **open** — which was right. But I had *also* written that the
mechanism "is not core-specific and nothing in the patched path is" as an argument for why
it probably affects the M33.

**It does. I was right for reasons I had not earned**, and the distance between those two
things is the entire reason this test exists. A correct conclusion reached by an argument
you cannot check is indistinguishable, from the outside, from a lucky guess — and it stops
being either the moment somebody measures it.

---

# 📀 XIP: Renode cannot execute from a peripheral-backed window

`imxrt1180-hello` read `NO-OUTPUT` for the whole project. It is the simplest test in the
corpus — print a string, exit — and it links to **FlexSPI1 NOR XIP at `0x28000000`**,
entry `0x28000009`.

## What actually happened, in three measurements

**1. The CPU never ran.** `PC = 0xeffffffe` (lockup), and the controller logged
`Transmission finished in the unexpected state: RecognizeOperation`. So `0x28000000` on
this platform is not memory — it is the FlexSPI1 controller's `ciphertext` region, where
every read is served by executing the AHB read sequence from the LUT against the NOR.
**That is the faithful arrangement**, and a plain `LoadELF` there writes *through* the
controller and lands nowhere.

**2. The faithful load path works.** Give the NOR's backing store a harness alias,
program the image into it, and hand-program a LUT for a standard `0x03` READ — the
harness acting as the boot ROM, because neither model has one:

```
LUT0 = 0x08180403   CMD_SDR 0x03 | RADDR_SDR 24
LUT1 = 0x00002404   READ_SDR 4   | STOP
```

```
0x28000000 -> 0x20020000    (initial SP)
0x28000004 -> 0x28000009    (reset vector)
```

Byte-exact against the image. **The XIP data path is genuinely correct.**

**3. And it still cannot run.**

```
cpu: CPU abort [PC=0x28000008]: Trying to execute code outside RAM or ROM
```

> ⭐⭐⭐ **TLIB TRANSLATES ONLY FROM REAL RAM/ROM. A COMMAND-TRANSLATED WINDOW IS
> READABLE BUT NOT EXECUTABLE.** No XIP-linked image could ever have run against that
> map — the test was not failing on a missing peripheral, it was failing on an
> *unexecutable address space*, and no amount of peripheral fidelity would have fixed it.

## The correction this forces to my own gap map

I had tagged the XIP rows `map-choice(rt1180emulator, permissive)` — their machine maps
`-kernel` straight into `0x28000000` as plain RAM, skipping the boot-ROM step. That tag
was **accurate about their side and wrong about the trade**:

| | what I wrote | what is true |
|---|---|---|
| their map | permissive — a shortcut | permissive **and necessary for execution at all** |
| my map | faithful | faithful **for data, and unexecutable** |

**Both sides made a real trade; I had only priced theirs.** Characterising their choice
as a shortcut while calling mine faithful was exactly the self-serving direction of the
error the oracle and I agreed to watch for — and it survived a round of "verify the
direction" because I verified *whose* map was looser without asking *what each map
costs*.

## What the project already knew, three lines away

The base platform's own comment, about the **secure** alias:

> *"Zephyr's cm33 image is XIP-LINKED and its ELF LOADs at `0x38000000`, so that address
> has to be plain writable memory, not a command-translated window."*

**The same conclusion, already measured, already written down — and never applied to the
non-secure window.** A lesson recorded in one place and not generalised to its neighbour
is a lesson half-learned.

## The change

The XIP window is now backed by **the NOR's own store** (`flexspi1_flash_memory` at
`0x28000000`), with the controller keeping its register interface and IP-command path.
That is strictly better than mapping an unrelated RAM there: the bytes the CPU fetches
**are** the bytes in the flash, through the same `MappedMemory` the ISSI model serves, so
*"program the flash, then execute"* is a real sequence rather than two unrelated loads.

```
hello:      === Hello from i.MX RT1180 (Cortex-M33) ... ===
hello_uart: === i.MX RT1180 LPUART1 console alive (Cortex-M33)! ===
```

**⚠ What it does not model:** the AHB read sequence is no longer on the fetch path, so a
misprogrammed LUT cannot break execution the way it would on silicon. Recorded as a note,
not claimed as fidelity.

**And it is a base-map change, so it is being re-verified against the full 31-row
equivalency corpus before it is trusted** — one SDK row (`flexspi/nor/polling_transfer`)
exercises the controller directly and is the row that would break.

---

# ⚖️ The window test found a bug in BOTH cores — and that beats the result I asked for

I sent `probes/m33-wfi-window/wfi_window.elf` to @rt1180emulator wanting a **positive
control**: a correct core must wake, run and return even in the arm-and-sleep
configuration. I said that if QEMU did *not* pass, that would be a QEMU bug worth
knowing.

**It did not pass.** Artifact identity confirmed both directions — `c8a33fa7dcaa…`, the
same bytes on both sides, so the two failures are observations of one artifact.

## Same window, opposite halves of the same sequence

| core | what happens |
|---|---|
| **tlib pre-fix** | exception taken, frame pushed, PC = handler, **zero handler instructions** — asleep *inside* the handler (#14) |
| **QEMU** | exception taken, handler **runs fully and returns cleanly** — and the return lands **on the WFI** (`0x0ffe0102`), which re-executes with its wake source already consumed by the ISR → hang |

One core never leaves the sleep state. The other leaves it and then re-enters it. They
traced theirs with `-d in_asm,int` rather than inferring it from the hang — the same
discipline as reading the ISR's marker instead of believing the console.

Per the ARM ARM, a `WFI` completed by a taken interrupt **retires**, and the stacked
return is the instruction *after* it. Post-fix tlib demonstrably resumes at `0xffe0104`
(that store is what stamps `MARK_RESUMED`, and PASS is computed from it), so post-fix
tlib and silicon agree, and QEMU differs.

## The measurement I can add, and why it is better than the argument

The field in dispute is the **stacked return address**. On pre-fix tlib the core parks at
handler entry with the frame intact (`sp = 0x2001ffe0`), so the stacked PC sits at
`sp+24 = 0x2001fff8` and is directly readable:

- `0xffe0104` → tlib stacks the ARM-correct return, and #14 is purely the sleep-state bug
- `0xffe0102` → **tlib has the same return-address deviation as QEMU, and #14 was masking
  it**

I do not know which. That is the point of running it rather than reasoning about it, and
the second outcome is the one that would change what #14 *is*.

> ⭐⭐⭐ **"QEMU CONFIRMS TLIB" WOULD HAVE BEEN A WEAKER RESULT THAN THIS.** Two
> independent implementations, one test, **two different bugs in one window** — and
> neither of us would have found our own. A positive control that fails is not a wasted
> control; it is the control doing its job on the controller.

Their caveat is the right standing and I said so: *measured deviation, not yet a confirmed
upstream bug*, because a return-address deviation could be machine-level interrupt wiring
rather than core WFI handling. One discriminator that needs no mainline build: **their
safe-path test returns to after-its-WFI on the same machine, same wiring, same NVIC
path** — only the window differs. The wiring is the constant across the two runs and the
behaviour changes with the window.

## The answer: two different bugs, measured to the field

```
tlib pre-fix, core parked AT handler entry, frame LIVE:
  pc      = 0xffe00bc    (test_isr)
  sp      = 0x2001ffe0
  [sp+24] = 0x0FFE0104   <- stacked return = the instruction AFTER the wfi
  [sp+28] = 0x01000000   <- stacked xPSR, Thumb set, well-formed frame

QEMU (theirs, via resumed pc after a successful exception return):
  stacked return = 0xffe0102   <- the wfi itself
```

**tlib stacks the ARM-correct return; QEMU does not.** So defect #14 is *purely* the
sleep-state bug — every field of the frame tlib builds is right, it simply never runs the
handler that would consume it. The return-to-WFI is a **separate** defect that happens to
live in the same window.

### One of my two rows is weaker than it looks, and it says so

The **pre-fix** row is the measurement: the core is parked mid-exception, so `sp+24` is
inside a *live* frame. The post-fix row reads the same value at the same address, but
there `sp = 0x20020000` — the test completed and the frame was popped — so that address is
**below sp** and I am reading stale stack. It corroborates; it does not independently
confirm. Only the pre-fix read is citable.

### The inference I held back, and why

I could see that the #14 patch sets `env->wfi` *after* the vector loop while stacking
happens in `v7m_prepare_exception_taken` well before it — so the patch cannot touch the
stacked PC, and post-fix tlib passing already implied `0xffe0104`. **I did not send it.**

> ⭐⭐ **BEING RIGHT BY REASONING AND RIGHT BY MEASUREMENT ARE DIFFERENT CLAIMS, AND ONLY
> THE SECOND ONE BELONGS IN SOMEBODY ELSE'S BUG REPORT.** That inference is exactly the
> shape of code-reading argument I got *wrong* about #14's own mechanism a few hours
> earlier. It turned out correct this time. That is not a reason to have sent it.

### What it does to the upstream case

It **strengthens** it by removing the alternative explanation. Had tlib also stacked
`0xffe0102`, they would be arguing about what the ARM requires with two implementations
agreeing against them. Instead there is an independent M-profile implementation that
stacks the after-WFI address **in the identical window, on the identical binary** — so
"return to the instruction after a WFI completed by a taken interrupt" is not one
reading of the pseudocode, it is what another core does.

## The XIP map change closed four rows, and only one of them was about XIP

Having found that `0x28000000` was unexecutable, I went looking for **other XIP-linked
tests by entry point** — on the theory that `NO-OUTPUT` might have been the unexecutable
map rather than a missing peripheral. Two more were:

```
imxrt1180-mecc            entry 0x28000039 -> MECC: PASS - single-bit error corrected on read
imxrt1180-m33-usagefault  entry 0x28000099 -> UFAULT-DELIVERED UFSR.UNDEFINSTR=1 -> PASS
```

Both had been sitting as `NO-OUTPUT`. Both were **fine the whole time**. MECC in
particular is a block I had *modelled*, whose SDK rows *pass* — so a working peripheral
was scoring `NO-OUTPUT` because its test image could not execute from the address space
it was linked to.

**Four rows closed by one map change** (`hello`, `hello_uart`, `mecc`,
`m33-usagefault`), none of which needed a line of peripheral work.

> ⭐⭐⭐ **GROUPING BY SYMPTOM WOULD HAVE SENT ME TO FOUR DIFFERENT BLOCKS. THE ENTRY
> POINT SORTED THEM IN ONE QUERY.** `NO-OUTPUT` is a symptom of the *instrument's view*,
> not of a subsystem — and the fourth time tonight that the useful partition of a failing
> set was a property of the **test** (its entry point, its harness, its ring, its
> verdict token) rather than of the peripheral its name mentions.

And the tag is not corrected, it is **retired**. With the XIP window backed by the NOR's
own store, neither side is the loose one on that axis: they map it as memory because
execution requires it, I map it as the flash's own memory, and both execute. That row is
plain agreement now.

---

# ⏱ The timer block: four rows, and two bugs I introduced myself

`lpit`, `tmr`, `timers2`, `fslaudit` — all four closed by modelling LPIT1, TMR1
(quad timer), GPT1, TPM1 and LPTMR1 from @rt1180emulator's per-block anchors. None of
these had been *failing*: they **hung**, spinning on a status bit nothing ever set, which
the sweep recorded as `NO-OUTPUT`.

The anchors named three traps I would have got wrong by convention, and they were right
about all three:

- **LPIT `CVAL` must genuinely decrement.** The test reads *both* the W1C flag and the
  counter. A flag-only model passes "does the IRQ fire" and dies on the read-back.
- **qtmr `SCTRL.TCF` is cleared by writing 0, not W1C.** The driver clears it with
  `SCTRL = TCFIE`. The usual write-1-to-clear convention either never clears it or clears
  it on the wrong write.
- **TPM `CnV` must round-trip.** `fslaudit` writes 1234 and reads it back. This is the
  *positive* form of the rule this project keeps meeting from the other side: usually the
  sin is a register that accepts a write and changes nothing (the ELE ack, the TDR that
  swallowed every sample) — here the driver requires storage to be real.

## Bug 1 — a counter that moves is not a counter that is configured

`lpit` still hung after the model existed. The probe:

```
MCR=0x1  MIER=0x1  TVAL=0x1388 (5000)   MSR=0x0
CVAL -> 0xFF48E4FF ... 0xFF2445FF
```

The counter was **genuinely running** — two reads, two different values, visibly
descending. It was descending from `0xFFFFFFFF`, the construction-time default, because
**setting `LimitTimer.Limit` does not reload `Value`**. It would have reached zero
eventually; the guest hung waiting.

> ⭐⭐ **"IS IT TICKING?" AND "IS IT CONFIGURED?" ARE DIFFERENT QUESTIONS, AND ONLY THE
> COUNTER'S ACTUAL VALUE DISTINGUISHES THEM.** A liveness check would have passed. This
> is the same shape as every single-operating-point trap tonight — and it is exactly the
> failure the oracle's *own* anchor warned about ("a flag-only model is caught by the
> CVAL read"), arriving from a direction the anchor did not anticipate: not a missing
> counter, a mis-seeded one. `ResetValue()` on every reconfigure, in all five models.

## Bug 2 — I implemented the LPTMR *stricter* than the reference, and broke its test

The anchor said a `CNR` read must be preceded by a `CNR` write to latch, and that their
test and model "handle this". I read that as **enforce the latch**, and made a read
without a preceding write return the last latched value. `timers2` reads `CNR` in a loop
with no write at all, so it read 0 forever and the counter looked stuck.

Their model says the opposite, in as many words:

```c
imxrt1180_lptmr.c:  /* CNR reads the live count */
mcxn_lptmr.c:       case R_CNR: return;   /* writing CNR latches on HW; ignore */
```

> ⭐⭐⭐ **BEING STRICTER THAN THE REFERENCE IS STILL BEING WRONG.** I have spent this
> whole project catching *permissive* shortcuts, and here I made the opposite error and
> it did not feel like an error at all — it felt like rigour. It is not more faithful; it
> is a **different model of the hardware that the shared corpus does not describe**, and
> a corpus is only an oracle for the behaviour it actually encodes.
>
> The process failure underneath is plainer: the brief says *read the QEMU model when you
> need exact behaviour*. I implemented from a one-line summary instead — **and the
> summary was about their TEST, not their REGISTER.**

---

# 🌐 NETC RX: frames now cross the boundary — 4/7

`netc-rxfwd` passes: *"unicast to a wire-only MAC was NOT delivered to the CPU"*. The
NETC now implements `IMACInterface`, so a frame goes CPU-ring → switch → wire → echo →
switch decision → RX ring, and the FDB decides at every hop.

## Three tool-boundary facts worth writing down

**1. The three network types live in three different namespaces, and none is the
obvious one.** Found by probing the runtime compiler, not by guessing:

```
IMACInterface -> Antmicro.Renode.Peripherals.Network
MACAddress    -> Antmicro.Renode.Core.Structure
EthernetFrame -> Antmicro.Renode.Network
```

`using Antmicro.Renode.Network` resolves `EthernetFrame` and **not** `IMACInterface`,
which is the kind of thing that reads as "Renode can't do this from a runtime-compiled
peripheral" if you stop at the first error.

**2. A peripheral registered `@ none` is not monitor-addressable**, so
`connector Connect` cannot name it. The echo node is registered at a harness address
purely so the monitor can reference it — it has no registers and says so.

**3. Counting a frame out is not sending it.** I had `EmitToWire` written and **never
called**: the TX path incremented the egress MAC counter and dropped the frame.

> ⭐⭐⭐ **THAT PASSED `netc-fwd`.** Its observable is the port MAC's transmit counter,
> so a model that counts and discards satisfies it completely — which is *precisely* the
> defect class this project keeps finding in other people's blocks (the ELE that acked a
> write it could not place, the TDR that swallowed every sample) and I had just built a
> fresh instance of it in my own. The row was green and the wire was silent.

## The echo wire is a BACKEND, and its scope is a hazard

`EchoWire` models nothing on the RT1180. It reproduces, deliberately, what QEMU hands
@rt1180emulator for free: `IP_MULTICAST_LOOP=1` is hardcoded in `net/socket.c`, so a node
on a multicast socket sees its own frames return. `netc-rxfwd`'s sentinel-barrier oracle
*requires* that echo.

**And the same loopback is a trap for the switched tests.** A switch flooding onto a
shared group re-ingests its own flood and storms — a real loop, but not the topology
under test, and it would read as a forwarding defect while being a backend artefact.
So the sweep now has a `WIRE` group that only `netc-rxfwd` routes to;
`netc-flood`/`portfwd`/`lab3` need point-to-point wires instead.

---

# 💾 eDMA: two defects, and one of them I had already "eliminated"

`imxrt1180-edma` passes. It found exactly what its own header says it was written to find.

## Defect 1 — DONE set, nothing moved

```
CH_CSR = 0x40000000   (DONE)
src    = 0x11111111
dst    = 0x00000000
```

The channel captured the initiating CPU **only in the `CHn_CSR` write callback**. Phase 1
starts the transfer from `TCDn_CSR[START]` and never touches `CHn_CSR`, so `context` was
null, the copy resolved through the **global** map, and this platform's CM33 DTCM is
CPU-scoped at `0x20000000`. It read zeros, wrote nowhere, and reported completion.

**Third instance tonight of a block signalling work it did not do** — after the ELE ack
and my own `EmitToWire`-never-called. The domain keeps being irrelevant.

### ⚠️ And I had already ruled this cause out, wrongly

Hours earlier I asked whether the eDMA resolves addresses in CPU context, found
`dmaEngine.IssueCopy(req, context)` with `context = GetCurrentCPUOrNull()`, and eliminated
it — and *said so*, explicitly contrasting it with a behavioural guess:

> *"hypothesis eliminated by a direct code fact rather than a behavioural inference"*

It **was** a direct code fact. It was also the wrong question.

> ⭐⭐⭐ **READING THAT A MECHANISM EXISTS IS NOT VERIFYING THAT IT RUNS ON THIS PATH.**
> I checked *whether* context is captured and never checked *when*. This is the defect
> #14 mechanism error — reasoning from code structure to runtime behaviour — repeated
> four hours later, on a different block, by the same person who had just written a
> section about it. Knowing the shape of an error does not stop you making it; only
> measuring does.

## Defect 2 — a software START is ONE MINOR LOOP

Mine ran the **entire major loop** on one START. The test's header predicts precisely why
nothing had ever caught it:

> *"At CITER=1, ONE MINOR LOOP IS THE WHOLE MAJOR LOOP — so a model that [runs the whole
> major loop] is INDISTINGUISHABLE from a correct one. Every eDMA test we had used
> CITER=1."*

Phase 2 (CITER=4, where one START must decrement CITER 4→3) is the only thing that can
see it. The hardware-request path one screen above was already passing
`singleIteration: true`; only the software path was not.

```
eDMA: PASS - CITER=1 copy + DONE, and CITER=4 needs FOUR STARTs
             (one minor loop per service request), byte-exact
```

`edma-swstart-order` re-run after: still PASS, 32 bytes byte-exact.

## The pattern, now with four instances

| block | the collapsed parameter | what it hid |
|---|---|---|
| SAI | one sample rate | `TCR2[DIV]` ignored entirely |
| ASRC | one clock source (`AICSA == AOCSA`) | the `outSrcHz/inSrcHz` factor |
| NETC PTP | one addend | rate derived from the callback, not the addend |
| eDMA | `CITER = 1` | a START running the whole major loop |

> ⭐⭐⭐ **A PARAMETER THAT IS 1 — OR THAT NEVER VARIES — COLLAPSES TWO BEHAVIOURS INTO
> ONE OBSERVATION.** A corpus that only ever uses the collapsed value is blind to the
> difference *by construction*, and no amount of care inside the model compensates.
> Every one of these four was found by a test that carried a **second operating point**,
> and in every case the first point passed against a model that was substantially wrong.

---

# 🔌 LPI2C DMA: a tagged flag is a register that accepts a write and changes nothing

`imxrt1180-lpi2c-dma` passes:

```
I2CDMA: PASS - TDDE gate refuses, RDDE gate refuses, both open =>
        TMP105 register read by eDMA, byte-exact vs PIO,
        CPU never touched MTDR/MRDR
```

## The defect, diagnosed by the oracle from my symptom alone

I reported `"NEG-RX: TX channel never completed (TX gate stuck?)"`. Their reply:
*"reads like your TX request isn't gated by TDDE — if TX asserts regardless of
MDER.TDDE, the negative test never completes because the gate is stuck open."*

Exactly right, and the mechanism is one this project keeps meeting:

```csharp
Registers.ControllerDMAEnable.Define(this)
    .WithTaggedFlag("TransmitDataDMAEnable", 0)     // accepted and DISCARDED
    .WithTaggedFlag("ReceiveDataDMAEnable", 1)
```

> ⭐⭐⭐ **A TAGGED FLAG IS THE REGISTER FORM OF "ACCEPTS A WRITE AND CHANGES
> NOTHING."** The request line could not be gated by a bit the model never stored, so
> the two **negative** phases — MDER=0 → TX must stay down; MDER=TDDE only → RX must stay
> down — were **unpassable by construction**. No amount of DMA plumbing would have fixed
> it, because the test was asking about a bit that did not exist.
>
> Fourth instance of one class in one night: the ELE ack, the SAI TDR that swallowed
> every sample, my own `EmitToWire`-never-called, and now this. Crypto, audio, switch
> fabric, I2C. **The class does not care what domain it is in.**

## An instance correction that would have cost hours

The oracle caught that **`lpi2c-dma` uses LPI2C1 @ `0x44340000`, not LPI2C4**. The two
lpi2c tests use different instances, and LPI2C1 sits in the `0x4434xxxx` AON window next
to eDMA3 — which is *why* it is an eDMA3 source (Tx=7, Rx=8), while LPI2C3/4 are eDMA4
sources. I had the right source numbers attached to the wrong peripheral.

## Held levels on an edge-serviced engine

Their model drives the requests as **booleans re-evaluated on every state change**;
Renode's eDMA services **one minor loop per rising edge**. The held level is therefore
expressed by re-arming — and the re-arm is driven by **the eDMA's own access**: an MTDR
write re-arms TX, an MRDR read re-arms RX.

That is self-limiting by construction. When the channel's major loop retires it stops
touching the data registers, so the requests stop on their own — no guard counter to
tune, and no way to spin.

### The bug that cost me the first attempt

I computed the RX level from `rxQueue.Count` but only **re-evaluated** it on MEN/MDER
writes. Bytes arrived with the line still low, the RX channel never got a service
request, and the guest spun on `CH_CSR[DONE]` forever (measured: PC parked at
`0xffe018a`, instruction count climbing 200M → 300M — *running*, not hung).

> ⭐⭐ **A LEVEL IS ONLY "HELD" IF SOMETHING HOLDS IT.** A derived signal has to be
> recomputed **where its inputs change**, not where it was first configured. I had
> written the condition correctly and then evaluated it in the wrong places, which looks
> identical to not having implemented it at all.

---

## Scoring honesty: structural detection, and an asymmetry worth defending

Two scorer changes, neither of which raises the pass count much — both of which make the
table mean what it says.

**1. Detect "ships its own harness" structurally.** It had been a hardcoded list of six
names. That is the same anti-pattern as routing by name, which has now mis-scored a test
three times in this project (`fslaudit` guessed into META, `mu` routed to a single-core
platform, `netc-rxfwd` needing a wire group). A test that ships `run.sh` / `check.py` /
`wire-check.py` **says so by shipping it**, and that fact cannot drift as tests are added.
**21 directories ship one** — my list had six.

**2. The exclusion is ASYMMETRIC, and deliberately.**

| bare result | verdict |
|---|---|
| ships a harness, **fails** bare | `NEEDS-OWN-HARNESS` — it was never armed |
| ships a harness, **passes** bare | **PASS** — a real verdict |

A test that fails without its harness has told us nothing: `imxrt1180-sai` literally
prints *"the rate selector was never armed by the harness"*. But a test that **passes**
bare has given a verdict — its firmware's own checks were satisfied with no harness help,
and `ele`, `flexspi`, `uartlink` and `xip` all do exactly that.

> ⭐⭐ **DISCARDING A REAL RESULT TO KEEP A TIDY RULE IS ALSO A SCORING ERROR.** The
> symmetric version of this rule — "ships a harness, therefore unscorable" — would have
> deleted four genuine passes to make the categories uniform. Tidiness is not accuracy in
> either direction: I have spent tonight catching rules that were too permissive, and
> this one would have been too strict.

### And the structural check shipped broken, caught by reading the summary

The first version used one multi-path `ls`:

```sh
ls "$2"/run.sh "$2"/check.py "$2"/check-token.sh "$2"/wire-check.py >/dev/null 2>&1
```

**`ls a b c` exits non-zero if ANY one is missing, not if all are.** Every SAI test ships
`check.py` and no `run.sh`, so all three tested *false* and went straight back to being
scored as `FAIL`s of my model — the exact misattribution the change existed to prevent.

It was caught because the summary line read `0 META-excluded` where the previous run said
`6`, and the FAIL count rose from 7 to 10 while I had changed nothing that could add
failures.

> ⭐⭐ **THAT IS ATTENTION DOING A CONTROL'S JOB.** The row-count assert passed — 55 rows
> for 55 binaries, exactly as designed — because the bug did not lose rows, it
> *reclassified* them. Every guard I have built tonight checks **completeness**
> (coverage, fingerprints, corpus size) and none checks **distribution**. A run that
> silently moves six rows from "not scored" to "failed" is precisely as misleading as one
> that loses them, and nothing would have said so.

---

# 🧩 The last three blocks: FlexCAN, uSDHC, and three small blocks for one row

`misc1`, `flexcan`, `usdhc` all pass. Each was register-level, and each came with an
anchor that mattered **beyond the test that motivated it**.

## `misc1` — three blocks, one row

- **SEMA42**: the interesting case is the **refusal**. Take gate 0 for domain 1, have
  domain 2 try, require the gate to *still* read 1, then release. A gate that simply
  stores what you write passes the take and the release and fails only the middle
  assertion — which is the block's entire purpose. *"Stores the value"* and *"enforces
  exclusion"* are indistinguishable until somebody else tries to take it.
- **VREF**: `CSR` bit0 enables, bit2 reads STABLE — the same always-settled ready-bit
  shape as the DCDC `STS_DC_OK`, which exists because a driver otherwise spins forever.
- **CMP**: config round-trips; `CFR` is a status flag software **cannot set**. A register
  that merely stored the write would report a comparator event that never happened.

## The anchors that were about the SDK, not the test

Both of the last two came with a warning of the same shape, and it is the sharpest
version of tonight's recurring lesson:

**FlexCAN** — their smoke test writes `MCR` directly and never polls the freeze
handshake, but `FLEXCAN_Init` in the real `fsl_flexcan` driver **spins on FRZACK and
NOTRDY**. **uSDHC** — the caps test never touches `SYS_CTRL`, but `fsl_sdhc` spins on its
reset bits self-clearing and on `INITA`, and reads `PRES_STATE`.

> ⭐⭐⭐ **A MODEL BUILT TO EXACTLY WHAT THE TEST POLLS PASSES THE TEST AND HANGS THE
> DRIVER.** This is the single-operating-point trap one level up: the collapsed case is
> **the test itself** rather than a parameter inside it. Every other instance tonight —
> SAI rate, ASRC source, PTP addend, eDMA CITER, SPI frame count — was a variable held at
> one value. This is the whole *stimulus* held at one shape.
>
> Both were modelled anyway, on an anchor rather than a failing row. That is the only
> time all night I wrote code for something nothing was asking for, and it is the one
> case where a green row would have been actively misleading.

## A number that travelled with its caveat

@rt1180emulator's uSDHC `CAP` value (`0x07F30000`) is explicitly flagged **best-effort,
not silicon-exact**, and they said to match the shape rather than their bits. The test
only checks `!= 0 && != 0xFFFFFFFF`, so almost anything passes.

It is carried **with their flag attached**, rather than laundering a flagged number into
an unflagged one by copying it across a boundary.

> ⭐⭐ **A NUMBER NOTHING CHECKS IS THE EASIEST KIND TO QUIETLY PROMOTE TO FACT.** The
> test's inability to tell the difference is exactly why the provenance has to survive
> the copy.

---

# 🎯 The three faces of one failure, and the defence that covers all of them

Named jointly with @rt1180emulator at the close of the run. Three mistakes I made
tonight, at increasing distance from the code, are the same mistake:

| # | the check that passed | the question it never asked |
|---|---|---|
| 1 | the mechanism **exists** — `IssueCopy(req, context)` is right there | …but does it run **on this path**? (`context` was populated only on the `CHn_CSR` path; the test starts from `TCDn_CSR[START]`) |
| 2 | the value is **flagged** — "best-effort, not silicon-exact" | …but did the flag **survive the copy** across the repo boundary? |
| 3 | the line is **in front of you** — `QEMU = os.environ.get("QEMU", …)`, printed in my own terminal | …but did you **read** it, or read it as evidence for a conclusion you already had? |

> ⭐⭐⭐ **EACH IS THE GAP BETWEEN *HAVING* THE INFORMATION AND *USING* IT** — and every
> one of them passed a check that **looked at the artifact without interrogating it**.
>
> The defence is identical in all three: **make the check ask a question the wrong
> answer fails, not just display the thing.** A grep that prints the line, a comment
> that records a caveat, a code path that contains the right call — none of them
> assert anything. The row-count gate, the source fingerprint, the verdict-delta
> report and the artifact-hash refusal all exist because of this, and each was added
> only *after* the corresponding failure.

## And the sharpest argument for two implementations

Not *"one catches the other's bug"* — that undersells it. On the `RBMR.EN` scope
question we converged on the same answer **from opposite errors**:

- Mine gated **ingress** too widely: a switch that only forwards while the host is
  listening. That says where the gate must **not** be.
- Theirs risked gating **delivery** too loosely: a switch that DMAs into a deaf host —
  the 88-frames-into-guest-address-zero measurement. That says where it **must** be.

**Each failure bounds the truth from a different side, and neither is derivable alone.**
That is what the two-implementation method is actually for, and it is a stronger claim
than cross-checking.

---

# 📄 README.md — written to mirror the QEMU repo's, and two defects it exposed

Kyle: *"Also build the readme for the repo. Follow rt1180 qemus readme just show
it's for renode of course. Ultimately I want them to be equivalent."*

`README.md` (216 lines) follows `~/Documents/GitHub/rt1180emulator/README.md`
section for section — title+hero, intro bullets, Quickstart, What runs today,
motor-control frontier, NETC switch, Validation, Required artifacts, Building,
Architecture, Repository tour, How this model is validated, Known limitations,
Roadmap, License — with the QEMU-specific rungs swapped for the Renode ones
(`.repl`/`.resc` + runtime-compiled C# instead of compiled-C devices; a
**Patched core** section where theirs has none, because Renode's CPU defects
are below the scripting boundary and needed a native `tlib` rebuild).

**Writing it found two defects, both in the same class the fleet keeps hitting:
a claim that displays fine and fails the question.**

### 1. The quickstart was a promise the repo could not keep

`scripts/rt1180_hello.resc` loaded its ELF from
`/tmp/claude-1000/.../scratchpad/hw/hello_world_cm33.elf` — **this session's
scratchpad.** `run_m0.sh` passed, every day, for anyone who was this session.
For any other reader the documented quickstart is a `LoadELF` on a path that
does not exist. The file was never wrong enough to fail a test; it was wrong in
the one way the tests structurally cannot see, because the test and the defect
share the same machine.

Fixed: the path now defaults to the reboot-persistent artifact cache
(`~/.cache/rt1180-artifacts/sdk/hello_world_cm33.elf`, md5 `731e0ae9…` —
verified byte-identical to the scratchpad copy before the swap, not assumed),
and is overridable with `-e '$bin=@/path'` before the include. Re-measured after
the edit: `run_m0.sh` → **M0 PASS**, and the README's quickstart command run
**verbatim from the repo root** prints `hello world.`

> The quickstart in a README is executable documentation. It was pasted into a
> shell and run before it was published, because a command block nobody ran is
> the same artifact as a metric nobody measured.

### 2. A headline figure quoted one generation stale — 75/83 vs 73/83

The README's first draft claimed **75/83** on the Zephyr delta. The canonical
number in `results/SCORECARD.md` is **73/83 (88 %)** — 48 byte-identical + 25
timing/nondeterminism — and `results/zephyr-delta-classified.tsv` arithmetic
agrees exactly (48+25=73). `results/DELTA-STUDY.md` still carries **72/83** and
says so in its own header: *"Do not quote any number from this file."*

Three artifacts, three numbers, one of them self-marked superseded. The draft
took the one that was in neither. Caught by re-deriving every headline from the
artifact rather than from the draft — the README's figures were **all** re-read
from `results/*.tsv` and `SCORECARD.md` before publishing, and every path it
names (38 peripherals, 12 platforms, 3 patches, every script, every probe) was
existence-checked in a loop. One of nineteen path claims would have been wrong
without it.

**Rule this earns:** *a number that appears in three files has three chances to
be stale and one chance to be right.* The README now carries the scorecard's
figure with its composition spelled out (48+25), so a future reader can check
the sum instead of trusting the total — the same defence as citing a frame read
instead of the reasoning.

---

# 🔭 HOLOBENCH BRING-UP — the seam, and the two defects it exposed

Kyle, green-lighting it: *"Gree lit but no upstream yet"* — proceed with holobench,
keep the hold on upstreaming (both the tlib patches and the QEMU return-to-WFI
report).

`netc-lab3` was scored **NEEDS-OWN-HARNESS** on the honest grounds that
`wire-check.py` spawns its own emulator nodes with QEMU CLI argv at three sites.
That classification is now **obsolete**: the seam is built and the same eight
phases run against a Renode node.

## The seam, in two halves

**Oracle half** — `rt1180emulator/tests/imxrt1180-netc-lab3/wire-check.py`:
three `subprocess.Popen` sites collapse to one `spawn_node(group, port, mac)`.
`NODE=qemu` (default) builds exactly the argv it always built; anything else
execs `NODE_SPAWN` with a four-argument contract. **Nothing about Renode is
encoded in the oracle's tree.**

**Renode half** — `scripts/holobench-node.sh`: `spawner <group> <port> <mac> <elf>`
→ a node joined to that group, running that ELF, console on stdout, and *being*
the emulator process so `terminate()` frees the socket.

> ⭐ Note what the seam preserves. This file exists because *"a rehearsal whose
> other actors are copies of you cannot discover that you disagree with anyone."*
> Teaching the checker to launch a **second implementation** is the opposite of
> that bug: it widens the set of actors that are not us.

**REFACTOR PROVED NEUTRAL BEFORE USE.** QEMU baseline captured first
(`rc=0`, PASS); after the refactor, `rc=0`, PASS, and the emitted check sequence
**diffs clean** against the baseline. A seam that quietly changes the reference
is worse than no seam.

## Five things the spawner contract had to get right, each found by failing

1. **`exec`, not a wrapper.** The harness kills the pid it started. A shell
   wrapper dies while Renode keeps the socket, and the next phase binds a group
   whose old node is still transmitting — a harness that manufactures findings.
2. **`/tmp/holobench` was already taken** — root-owned, by the real fleet
   holobench with an imx95 lab in it. `mkdir -p` failed, the heredocs failed,
   and the script **exec'd Renode on a `.resc` that had never been written**.
   Renode printed "File does not exist" and **exited 0**. Now the scratch root is
   user-namespaced and every write is checked, because past an `exec` there is
   nobody left to notice.
3. **Generated `.repl` must not `using`.** A relative `using` resolves against
   the directory of the file at the TOP of the chain, so a /tmp fragment
   inheriting `mimxrt1189_cm33_value.repl` made *that* file's
   `using "mimxrt1189_m1.repl"` resolve into /tmp (`Error E36`). Load the stock
   platform absolutely, then the fragment additively.
4. **Declaring a port and a wire does not connect them.** Renode joins network
   peripherals through a switch at runtime. Without `emulation CreateSwitch` +
   two `connector Connect`s the node boots, joins the group, logs *"wire port 0
   registered"* — and moves no frames. Every log line true, node deaf.
5. **`logLevel 2` swallowed the console.** `showAnalyzer`'s analyzer emits at
   **INFO**, below the WARNING global — analyzer attached, guest printing, stdout
   empty. Same trap the PMSM probe documented; hit anyway.
6. **`< /dev/null` killed the node.** The `.resc` ends in `start`; the monitor
   then reads stdin, and **EOF is a `quit`**. The node lived 90 ms and `timeout`
   returned 0, so nothing looked wrong: *a node that lived 90 ms reads exactly
   like a node that said nothing.* Fixed with a read-write fifo, which never EOFs.

## And one assumption in the harness itself

Phase 1's 12-second beacon window started the instant `Popen` returned — which
silently charges the node for its **emulator's startup**. Invisible at QEMU's
0.3 s; fatal at Renode's ~8 s.

The barrier is the **guest's first print**, not its `UP` banner. I tried the
banner first and it **broke QEMU** — which is the instructive part: under QEMU
that banner does not appear until phase 1 is already running, so phase 1's recv
loop *is* the wait for it, and blocking on it ahead of time deadlocked a node
that was working perfectly. No assertion changed: six beacons, strictly
increasing sequence, exact 64-byte agreed body, all untouched. **Patience is not
an assertion.**

## 🐞 TWO REAL MODEL DEFECTS, FOUND ONLY BY A PEER THAT COUNTS

### Defect A — ENETC0's TX-done rang ENETC1's doorbell

`DoEndpointTxRing` ended in a hardcoded
`EmitMsix(Enetc1MsixTable, Enetc1TxRingVector1)` for **both** callers. ENETC0 sent
its frame, wrote its BD back, advanced its consumer index — and then signalled
completion on ENETC1's table. Everything the driver could *see* was correct; the
only missing thing was the completion it was blocked on.

**MEASURED**, one passive listener pointed at each emulator in turn, 25 s window:

| node | beacons in 25 s |
|---|---:|
| QEMU | **21,841** |
| Renode, before | **0** (one frame at startup, then silence forever) |
| Renode, after | **481**, sequence strictly increasing |

SOURCED from `hw/net/imxrt1180_netc.c` — `R_SIMSITRVR0 = ENETC0_SI0_OFF + 0xB00`
(:173), `NETC_MSIX_TABLE 0xBF0000` (:178), `netc_emit_msix()` (:272), and :591 at
the end of the ENETC0 TX ring. The table and vector are now parameters of the
ring, not constants of the function.

### Defect B — the RX writeback put the length at byte 12; it belongs at byte 8

`stw_le_p(wb + 8, len)` in the oracle; `Array.Copy(..., 12, 2)` here. The driver
read `bufLen` from +8, got 0, never freed the descriptor: **RBCIR stayed 0 while
RBPIR climbed to 7 and the ring wedged FULL** after exactly one ring's worth of
frames. `RX ring full (pir=7 cir=0)` forever, node deaf to the whole segment.

> ⭐ **BOTH DEFECTS WERE INVISIBLE TO EVERY TEST IN THE CORPUS, FOR THE SAME
> STRUCTURAL REASON.** `netc-fwd`/`-fdb`/`-flood`/`-portfwd`/`-rxfwd` all inject
> a frame and assert on what came out. **Not one asks the node to send a second
> frame on its own initiative**, and a ring only wedges once it *wraps* — more
> frames than any of them send. A completion that never arrives and a four-byte
> offset error both survive every test that needs exactly one transfer.
>
> This is the *"a parameter that never varies collapses two behaviours into one
> observation"* rule in its harshest form: the varying parameter here is **how
> many frames in a row**, and the whole corpus held it at one.
>
> It is also the argument for holobench stated as a measurement: a live peer
> found in one night two defects that a 55-row fixture suite could not see.

## Where the lab3 row stands now

| phase | before | after |
|---|---|---|
| node emits the AGREED body, read off the wire | 1 frame | **6, accepted by the peer's `frame_ok()`** |
| foreign (IPv6) frames drew no CORRUPT | ok | ok |
| imx91 0x88B8 body read and ACCEPTED | **never** | **ok** |
| un-upgraded peer counted, not condemned | — | **ok** |
| **PASS with BOTH peers content-VERIFIED** | — | **ok** |
| guest `t=` a plausible Unix epoch | — | **FAIL — see defect #16** |

The node is interoperable on the protocol. What stops the row is not the RT1180
model.

## 🐞 RENODE DEFECT #16 — semihosting SYS_TIME is unimplemented, returns -1

The firmware timestamps its PASS beats with ARM semihosting **SYS_TIME (0x11)**,
which returns seconds since the Unix epoch. QEMU answers it: its guest prints
`t=1789974258.340` against a host clock of `1789974870`. Renode answers `-1`, so
the guest prints **`t=4294967295.000`** — exactly `0xFFFFFFFF`.

**MEASURED**, from the node's own log:
`Unhandled 0x11 operation (SYS_TIME)` ×2, and `Unhandled 0x10 operation
(SYS_CLOCK)` ×2.

**SOURCED**, from Renode's own tree — `Emulator/Cores/Arm/SemihostingHandler.cs`:
`SYS_TIME = 0x11` is declared in the `Operation` enum (:1178) but has **no case**
in `DoSemihosting`, so it reaches `default:` → log + `return unchecked((uint)-1)`
(:464). The file carries the admission in a comment at :638 —
*"TODO: Add check for the following when they get implemented … SYS_TIME"*.

**Not fixable from this repo.** `DoSemihosting` is **not `virtual`** and
`Arm.Register` takes the concrete `SemihostingHandler`, so no subclass can
intercept; unlike defects #11–#14 this one is in the **managed** core, not
`tlib`, so it needs a renode-infrastructure rebuild rather than a `.so` swap.
Recorded, not attempted — and **not upstreamed**, per the standing hold.

> This is a general Renode gap, not an RT1180 one: any firmware that timestamps
> with semihosting gets `0xFFFFFFFF` on Renode and a real clock on QEMU.

## Defect #16 — a patch written, reviewed, and deliberately NOT claimed

`patches/renode-defect16-semihosting-systime.patch` implements `SYS_TIME` and
`SYS_CLOCK`. **It has never been compiled**, and the file says so at the top.
There is no .NET SDK on this machine, and installing a toolchain is an
environment change outside this repo — Kyle's call, not mine, at 2am.

Everything that could be done without a build, was:

* **Source-tree provenance established by content.** The same TWO-TREES hazard
  that nearly got the wrong `tlib` patched. Three string literals from
  `SemihostingHandler.cs` were found in the **shipped** `Infrastructure.dll`.
  ⚠ The first probe returned **0 hits and would have "proved" the tree wrong** —
  `strings` defaults to ASCII and .NET stores literals as UTF-16. `-el` is not
  optional. *A negative result from a mis-aimed instrument is not evidence.*
* **Every API read in the tree**, not assumed: `LogReceived`, `LogFailure`,
  `IMachine.ElapsedVirtualTime`, `TimeStamp.TimeElapsed`,
  `TimeInterval.TotalSeconds`, `IMachine.RealTimeClockDateTime`,
  `Misc.UnixEpoch` — each with a file:line in `patches/README.md`.
* **The first draft was worse and got replaced.** It hand-rolled its own epoch
  anchor field. Renode already has `machine.RealTimeClockDateTime`
  (`RealTimeClockStart + elapsed virtual time`), which every RTC peripheral in
  the tree uses and which honours the user's existing `RealTimeClockMode`. The
  rewrite deletes the invented field. *When the codebase already has the
  mechanism, adding a parallel one is a defect with good intentions.*
* **The caller was configured for it**: `holobench-node.sh` now sets
  `machine RealTimeClockMode HostTimeUTC` (default is `Epoch` = 1970 + virtual
  time, which no plausible-epoch check would accept). Verified to parse and to
  leave the node booting — and **commented as doing nothing until the assembly
  is rebuilt**, so the node's wrong clock stays *one* defect, not two.

> ⭐ **WHAT MAKES THIS DIFFERENT FROM #11–#14 IS THE ONE THING THAT MATTERS.**
> Those four were mutation-proven: swap only the `.so`, watch the verdict flip.
> This one is **argued**. The repo's whole method is that a reading is not a
> measurement, and this patch has had a careful reading and no measurement. It
> is filed as a **proposed** fix, and the row it would close stays **red**.
>
> The failure mode being avoided has a name in this log already: publishing
> defect #14's *mechanism* before proving it, when my own evidence contradicted
> it. The cheap way not to repeat that is to let an unproven thing be labelled
> unproven, even when the argument feels airtight.

## Defect #16, continued — built, and the build falsified the write-up

The previous section filed #16 as *"argued, not demonstrated"* and listed the
APIs verified by reading. Kyle then green-lit the SDK install (*"yes do it."*),
so it got built. **The patch code was right; everything around it was wrong in
three ways that reading never touched.**

1. **`dotnet build src/Infrastructure.csproj` cannot work.** ~12
   `ProjectReference`s to `../../../lib/**`, which from a bare
   `renode-infrastructure` checkout resolve to `~/lib/**`. It is a *submodule*.
2. **`net6.0` in the csproj is not what ships.** `build.sh:42` sets
   `TFM="net8.0"` into a generated `Directory.Build.targets`. And
   `-p:TargetFramework` (singular) does nothing — restore reads `TargetFrameworks`
   (plural), and the net6.0 restore fails outright because GirCore 0.7.0 has no
   net6.0 target at all.
3. **`lib/resources/libraries/*.dll` are not in git.** Taken from the shipped
   portable, which is better than a substitute: the compile links against
   exactly what runs.

> ⭐ **A READING IS NOT A BUILD**, and the unproven label was carrying real
> weight. Every API call in the patch was correct. The *context* — how it is
> built, against what, at which framework — was three separate errors, and no
> amount of careful reading of `SemihostingHandler.cs` would have found any of
> them.

### And the tree I had patched was the wrong one

Before building, the TWO-TREES check ran again, and this time there were three
candidates. `~/.cache/renode-infrastructure` @ `47a4e12` — the tree I had
patched and documented — is **not** the shipped source; the v1.17.0 tag pins
`src/Infrastructure` to `066a7f1`, and the two differ in **42 `.cs` files**
including `NVIC.cs`, `CortexM.cs`, `TimeSourceBase.cs` and `Machine.cs`.

Settled by a two-directional literal vote against the shipped binary:
cache-only literals **0 present / 10 absent**; tag-only **2 present / 0 absent**.

**Three instrument failures on the way, each of which would have "proved"
something false:**

* `strings` defaults to ASCII; .NET stores literals as **UTF-16**. The first
  probe returned 0 hits and would have condemned the correct tree.
* The first three discriminators returned 0 for **both** trees — they were
  `.Trace(...)` and `DebugHelper.Assert(...)` strings, compiled out of Release.
  A **positive control** (literals present in both trees, from the same file)
  showed they were all in the binary, so the probe was fine and my
  *discriminators* were the problem.
* `diff -rq --include="*.cs"` reported "0 files differ" for trees differing in
  42. `--include` is a `grep` option; `diff` ignores it silently.

> ⭐ **A NEGATIVE RESULT FROM AN UNVALIDATED INSTRUMENT IS NOT EVIDENCE.** Run
> the positive control first, every time.

### Equivalence, and the limit of it

The built assembly differs from the shipped one in **six strings**: the five
this patch adds, and the version stamp. But the stamps disagree —
shipped `1.0.0+cd4b002a…`, built `1.0.0+066a7f13…` — so the shipped DLL came
from a **third** commit that is not fetchable. Identical string tables cannot
rule out a difference in code carrying no literals, and one such difference is
known to exist between trees here (`NVIC.FilterCcrDiv0Write`, a bool default
that flips). **The empirical bound is what counts: 55-row sweep, M0, and both
wire harnesses, all unchanged with the new assembly under them.** Stated as a
residual risk rather than waved away.

### The two remaining failures were the harness being QEMU-shaped

Neither was the node being wrong, and both are now fixed inside `spawn_node`
so no call site can forget them:

* **Boot barrier.** Phases 1 and 8 started measuring the instant `Popen`
  returned. Phase 8 sent all 80 frames and waited 1.5 s — 5.5 s total — at a
  node still booting, then reported *"the node neither counted nor condemned
  them. Silence is not a verdict."* **It was not silent. It was absent, and the
  harness said so about the node.** The barrier is the guest's FIRST PRINT;
  blocking on the `UP` banner instead **broke QEMU**, where that banner only
  appears once phase 1 is already running — phase 1's recv loop *is* the wait.
* **Console decoration.** The banner grammar check anchors with `^`, and
  Renode's analyzer prefixes every line with `[INFO] lpuart1: [host: …]`. That
  prefix is the emulator talking over the guest, so the spawner strips it. En
  route: `CreateFileBackend` writes raw bytes but **buffers** — the console file
  was empty for a whole 25 s run and filled only on dispose. *A backend that
  delivers the console after the test is over is not a console.* It also refuses
  a second UART on one path, and **that error aborts the rest of the `.resc`**,
  so `LoadELF` and `start` never run and the "node" is not a node.
* And I hit the trap **my own PMSM script documents**: backticks in an unquoted
  heredoc got command-substituted (`Epoch: command not found`). Two had been
  latent since earlier the same night, harmless only because the mangled line
  still began with `#`.

### Result

`netc-lab3` **PASSES on Renode**, `rc=0`, all eight phases, through
@rt1180emulator's own `wire-check.py` with every assertion unchanged — and QEMU
re-verified after every edit, check sequence diffing clean against the original
baseline. NETC is **7/7**.

Mutation proof, swapping only `Infrastructure.dll`:
`02d8f934…` → **FAIL** `t=4294967295.000`; `814444ea…` → **PASS**,
`t=1790010017.220 -> 1790010019.670` over 26 beats.

---

# 🧩 100% BLOCK EQUIVALENCY — the audit, and what following symptoms had missed

Kyle: *"I want 100% functional equivalency. Meaning if qemu models a GPIO or a
UART or I2C or core to core communications and renode doesn't, I want that
closed."*

I had found RGPIO and RTWDOG by **following symptoms** — unmapped-access warnings
in a boot log. That is a fine way to find a bug and a **terrible way to bound a
gap**: it can only surface blocks that some firmware in the corpus happens to
touch. The audit that actually answers the question compares against the oracle's
`imxrt1180_soc.c` instance-for-instance.

It found **12 more families short**, none of which any test had complained about.

## Tier 1 — no model existed at all

| block | instances | evidence it was needed |
|---|---|---|
| **RGPIO** | 6 | 18 unmapped writes to `0x43830044` + 18 to `0x43830054` per boot = RGPIO4 **PSOR** and **PDDR**: firmware setting a direction and driving a pin into nothing |
| **RTWDOG** | 5 | 9 unmapped accesses each at `0x442D0000`/`0x442E0000`, with `0xC520` then `0xD928` at +0x4 — the unlock key sequence, going nowhere |

Both ported from the oracle, **including its two paid-for reset lessons**: RTWDOG
`CS` resets to `0x900` (a zero CS is a disabled watchdog with a zero timeout, and
`RTWDOG_Init` read-modify-writes it), and `RCS` is **latched**, never ORed in —
a watchdog must not report a reconfiguration it was never asked to perform.

## ⭐ AND RGPIO'S ABSENCE HAD BEEN HIDING BEHIND A GREEN ROW

`demo_apps/led_blinky` scored **RAN / RAN → agree = YES** on the equivalency
table. The QEMU corpus asserts something real for that row — *"RGPIO4[27] toggle
observable in PDOR"* — and this side could not assert it, because **the register
the toggle lands in did not exist**.

> ⭐ **A ROW WHERE BOTH SIDES REPORT "IT RAN" IS NOT AGREEMENT. It is two
> silences that happen to match.** The weakest observable always agrees. This is
> the project's own "green is not fidelity" rule, found this time in my own
> results table rather than in a model.

Now MEASURED, stock SDK `rled_blinky_demo_cm33.bin`:

```
PDDR (0x43830054) = 0x08000000          bit 27 -- the EVK user LED, configured as output
PDOR (0x43830040) x24 samples:
  0x00000000 x10 -> 0x08000000 x10 -> 0x00000000 x4      one full blink period
```

⚠ Two things worth recording about that test. The stock `led_blinky` **source
does not build** against this SDK (`BOARD_USER_LED_GPIO_PIN` undeclared,
warnings-as-errors), so the SDK's **prebuilt** binary was used — which is the
better test anyway, since both models run the byte-identical image. And there is
**no led_blinky artifact in the pinned set**, so that equivalency row was never
backed by a binary on either side.

## Tier 2 — the model existed; the instances did not

Twelve families were mapping only the instances the corpus happened to touch:

```
LPUART 2→12   TMR 1→8    TPM 1→6    LPSPI 3→6   CMP 1→4
EQDC 1→4      PWM 1→4    LPIT 1→3   LPTMR 1→3   FLEXCAN 1→3
GPT 1→2       SEMA42 1→2
```

Every base and IRQ **SOURCED** from `hw/arm/imxrt1180_soc.c` — the cfg tables at
`:533/:789/:813/:832/:856/:1224/:1235/:1246`, the stride maps for TMR
(`IMXRT1180_TMR1_BASE + i*0x10000`) and CMP (`0x42DC0000 + i*0x10000`), and
`soc.h:188-189` for EQDC. **Nothing derived from a pattern I inferred myself.**

IRQ lines were added only where the family's instance 1 already wires one: a
dangling GPIO is worse than an absent one (`Error E14`, learned on uSDHC).

**Result: 22 of 22 families at full complement, 0 short.**

## Regression

45 PASS / 0 FAIL / 10 NEEDS-OWN-HARNESS, **no row changed verdict**, coverage
asserted 55 == 55; M0 PASS; `netc-flood` and `netc-portfwd` byte-exact;
`netc-lab3` on Renode through the QEMU harness still `rc=0`, all eight phases.

> ⭐ **THE LESSON IS ABOUT THE AUDIT, NOT THE BLOCKS.** Following symptoms found
> 2 of 14 gaps. The other 12 were invisible because *no test addressed those
> instances* — and a test suite cannot complain about an address nobody sends.
> Bounding a gap requires enumerating the reference, not waiting for the subject
> to fail.

---

# 🔁 THE SAME COMPARISON BUG, THREE TIMES — and the guard that ends it

Chasing "how close are we to QEMU", I compared two files three times and got a
confident wrong answer every time:

| # | what I ran | what it claimed | truth |
|---|---|---|---|
| 1 | `diff -rq --include="*.cs"` | "0 files differ" | **42** files differ; `--include` is a **grep** option, `diff` ignores it |
| 2 | `comm` on the two example columns | "32 examples uncovered" | 0; the columns spell it `A demo_apps/x` vs `demo_apps/x` |
| 3 | `comm`, naming fixed | "1 uncovered (`pmsm_enc`)" | 0; our column can carry a trailing ` [long note]` |

Each printed a number I then reported to Kyle. #1 nearly caused a build from the
**wrong source tree**; #2 and #3 overstated a validation gap by 30× and 1×.

> ⭐ **THE ROOT CAUSE IS NOT CARELESSNESS, IT IS A MISSING FAILURE MODE.** An
> ad-hoc comparison between two files whose formats you have not validated
> produces a wrong answer that is *indistinguishable from a right one*. There is
> no error, no empty output, no exception — just a number.

`scripts/check_corpus_coverage.sh` replaces the ad-hoc version, and its whole
design is one idea this project already had and I failed to apply here:

**MAKE THE CHECK ASK A QUESTION THE WRONG ANSWER FAILS.**

It carries a **positive control** — examples that *must* appear on both sides
(`hello_world`, `led_blinky`) plus a floor on each list's length. If a control is
missing, the parsing is broken, and the script **exits 2 and makes no coverage
claim at all** rather than reporting a confidently wrong one.

Verified in both directions, because a guard never seen to fail is decoration:

```
$ ./scripts/check_corpus_coverage.sh
oracle corpus examples : 30
measured on this side  : 30
COVERAGE COMPLETE — every oracle corpus example is measured on this side.

$ ORACLE=<corpus with our spelling injected> ./scripts/check_corpus_coverage.sh
BROKEN: control 'demo_apps/led_blinky' absent from the ORACLE list -- corpus parsing failed
NO COVERAGE CLAIM MADE -- fix the comparison first.          rc=2
```

That second invocation reproduces failure mode #2 exactly — the one that invented
32 missing rows — and the guard refuses instead of inventing.

**And the actual answer: SDK corpus coverage is 30/30, complete.** `pmsm_enc` was
never missing; it is a `value` row, VALUE-PROVEN on both sides. The remaining
delta with QEMU is not corpus coverage and is no longer peripheral blocks — it is
**breadth of firmware**, which is why the Zephyr corpus is the next lever.

---

# 🧪 THE ZEPHYR DRIVER TESTS WERE THE WRONG CORPUS — and the platform bug they found anyway

Widening the Zephyr corpus, I built six of Zephyr's own `tests/drivers/*` on the
grounds that they would exercise the peripheral instances just added. The
instinct was right; the selection was not.

## What the numbers looked like

| target | qemu | renode (before platform fix) |
|---|---:|---:|
| `adc_api` | 10 | **0** |
| `can-api` | **363** | **0** |
| `counter_basic_api` | 123 | 13 |
| `spi_loopback` | 15 | 43 |
| `uart_basic_api` | 16 | 16 |
| `wdt_basic_api` | 13 | 4 |

`can: qemu=363 renode=0` reads like a damning FlexCAN gap. I was one step from
chasing it.

## What the consoles said

```
can-api          qemu: 52 pass 13 fail -> PROJECT EXECUTION FAILED
counter_basic    qemu:  0 pass 11 fail -> PROJECT EXECUTION FAILED
adc_api          qemu:  0 pass  1 fail -> never finishes
spi_loopback     qemu:  0 pass  0 fail -> never finishes
wdt_basic        qemu:  0 pass  0 fail -> never finishes
uart_basic       qemu:  3 pass, then blocks on
                       "Please send characters to serial console"
```

**The reference fails all six.** `tests/drivers/*` are HARDWARE-IN-THE-LOOP
tests written for a real EVK: a CAN transceiver with a bus peer, a MOSI–MISO
loopback jumper, analog input on the ADC pins, a real watchdog reset, and a
human typing into the UART. Those 363 QEMU lines end in **FAILED**.

> ⭐ **BEFORE CALLING A DIFFERENCE A GAP, CHECK WHETHER THE REFERENCE PASSES.**
> A test the oracle itself fails cannot distinguish a good model from a bad one
> — it can only generate work. The line-count column looked like signal and was
> not; the verdict was three greps away and I read it only after treating the
> counts as meaningful.

The six are parked in `~/.cache/rt1180-artifacts/zephyr-not-a-bar/` with that
evidence in a README, deliberately **outside** the corpus, so nobody later reads
`renode=0 vs qemu=363` as a defect.

## ⭐ BUT THEY FOUND A REAL BUG, IN THE HARNESS'S OWN PLATFORM

Chasing why Renode printed *nothing* exposed something the block audit had
missed entirely:

**`mimxrt1189_zephyr.repl` inherited bare `mimxrt1189_cm33.repl`** — so it had
**no LPADC, LPSPI, FlexCAN, GPT, LPTMR, RGPIO, RTWDOG or eDMA** — while the
board's own devicetree enables `lpadc1`, `lpspi3`, `flexcan3`, `gpt2`, `lptmr1`
and `edma3/4`. **Every one of the 83 Zephyr rows has been running against those
holes.** And LPADC was worse: it existed only in the motor and cm7 platforms, so
**no platform the SDK or Zephyr harnesses use had an ADC at all.**

> ⭐⭐ **AND THE MORNING'S AUDIT CERTIFIED 22/22 FAMILIES COMPLETE WHILE THIS WAS
> TRUE.** It counted instances across the **union of all platforms** — a
> perfectly accurate answer to the wrong question. *A capability present
> somewhere is not a capability present where the firmware looks.* The union is
> what a maintainer believes; the platform under test is what the guest gets.

Fixes, each verified rather than assumed:
* `zephyr.repl` re-parented onto `m1.repl`. The seven entries it duplicated
  (`trdc1-3`, `dcdc`, `blkctrl_wakeupmix`, `src`, `blkctrl_s_aonmix`) were
  confirmed **byte-identical in type and base** before removal — Renode rejects
  a redeclaration outright (`Error E02`).
* **LPADC1/2 added to `m1.repl`**, base/IRQ SOURCED from `adc_cfg[]`
  (`soc.c:1093`).
* Confirmed present at runtime: `adc1 adc2 edma4 flexcan3 gpt2 lpspi3 lptmr1
  rgpio4 rtwdog1`. M0 still passes.

MEASURED effect of the platform fix alone, same binaries:

| target | before | after |
|---|---:|---:|
| `adc_api` | 0 | **10** (= QEMU's count) |
| `counter_basic_api` | 13 | **76** |
| `spi_loopback` | 43 | 45 |
| `wdt_basic_api` | 4 | 5 |

Silent → talking. The tests remain a bad bar; **the bug they surfaced is real and
affects the whole Zephyr study**, which is why the 83 rows now need re-running
against the corrected platform rather than assuming 73/83 still holds.

---

# 📊 ZEPHYR DELTA RE-CUT — 90 targets on the corrected platform

The platform fix (`zephyr.repl` re-parented onto `m1`, LPADC added) sits under
**every** Zephyr row, so the 73/83 figure had to be re-measured rather than
assumed. Re-cut over 90 targets: **51 identical, 35 DIFFER, 4 partial.**

## No existing row regressed. All 10 movements are accounted for

| movement | n | cause |
|---|---:|---|
| DIFFER → identical | 4 | genuine gain: `arm_thread_swap`, `kernel-poll`, `sched-schedule_api`, `lib-hash_map` |
| identical → partial | 4 | all four `synchronization` variants — **free-running samples**, no terminal marker, line count = how long the run lasted. Counts exploded on BOTH sides (cm33 33→324 qemu / 55→151 renode), changing the prefix-overlap ratio the classifier uses |
| partial → DIFFER | 1 | `cm7-samples-philosophers`, same free-runner effect (232→5259 / 1107→2145) |
| identical → DIFFER | 1 | `cm7-tests-kernel-threads-thread_apis` — 249/249 lines, **6 differing, every one a duration** (0.102 vs 0.101 s); both `SUITE PASS 31/31`, both `PROJECT EXECUTION SUCCESSFUL` |

⭐ Note which way the free-runner rows moved: they got **worse-looking while
nothing changed**. A sample that prints until a guard stops it measures the
guard, not the model. Those rows should never have been scored on line count.

## The 7 new targets all agree

| target | verdict | if DIFFER, what differs |
|---|---|---|
| `kernel-obj_tracking` | identical | — |
| `kernel-pending` | identical | — |
| `lib-mem_blocks` | identical | — |
| `lib-onoff` | identical | — |
| `lib-sprintf` | DIFFER | 6 lines, all durations |
| `subsys-logging-log_msg` | DIFFER | 6 lines, all durations |
| `lib-ringbuffer` | DIFFER | 64 lines; the non-duration ones are perf telemetry — `executed:1632` vs `1636`, `Average CPU load:13%` |

All seven reach `PROJECT EXECUTION SUCCESSFUL` on **both** models. Each was
gated on the reference *before* being pinned — the rule the driver-test round
taught.

## ⚠ AND I COMPARED THE WRONG FILES AGAIN, TWICE, INSIDE THIS ONE ANALYSIS

1. **Diffed `.txt` instead of `.norm`.** The `.txt` capture is the harness's
   **stdout** — Renode's startup banner and log lines against QEMU's bare serial
   output. It reported "321 differing lines" for a row whose real difference is
   **6**. The harness compares `.norm`, the raw UART captured via
   `CreateFileBackend` on both sides, which is stated in its own comments.
2. **`join` on unsorted input.** It printed `join: input is not in sorted order`
   and produced a result anyway. The numbers happened to look plausible. Redone
   with `LC_ALL=C sort -k1,1` and an assertion that the join returns exactly 83
   rows before reading anything into it.

That is the fourth and fifth time in this project that a comparison ran against
inputs I had not validated. The pattern is stable enough to name: **the error is
never in the reasoning about the diff — it is in reaching for the diff before
checking what is on each side of it.** Every guard added today
(`check_corpus_coverage.sh`, `check_platform_capability.sh`) exists because of
this one habit, and both of them assert their inputs before reporting.

---

# 🏁 THE ACCEPTANCE TEST PASSES — both models on one wire, each verifying the other

Kyle's bar, set at the outset: *"My test will be holobench booting 1180 renode
alongside qemu 1180 and not be able to tell a functional difference."*

```
node A (QEMU,   0x88B6): ENET-LAB3 PASS #1240 -- 0x88b9 VERIFIED, 0x88b5 VERIFIED
node B (Renode, 0x88B9): ENET-LAB3 PASS  #161 -- 0x88b6 VERIFIED, 0x88b5 VERIFIED
HOLOBENCH PASS - both models on one segment, each VERIFIED the other's frames
```

Both emulators on one multicast segment **at the same time**, each having
**content-verified** the other's beacons. `VERIFIED`, not presence-only: the
peer's 64-byte body was read and checked field by field.

## What actually blocked this, and why `-nic mac=` was not the answer

The lab-3 image **compiles in** its MAC and EtherType. MEASURED: QEMU launched
with `-nic ...,mac=54:27:8d:00:00:99` still produced a node announcing
`mac=54:27:8d:00:00:00` — the NIC model's address is not the one the firmware
stamps into frames. Two RT1180 nodes from one image are two stations with one
identity, and the segment cannot tell them apart.

@rt1180emulator's own build script carries this lesson already, about its
self-test stand-ins: *"THREE STATIONS WITH ONE MAC ... Give each node its own."*

The fix was not to patch a binary. `build_node()` in `tools/netc-eth-lab3.sh`
takes EtherType **and** MAC as parameters — `ME=$1 PA=$2 PB=$3 OUT=$4
MAC=${5:-...}`. Only the `--build` *mode* hardcodes `0x88B6`. So the function was
extracted verbatim into a standalone builder (their script untouched, nothing
else in it executed) and run twice:

| node | EtherType | required peers | MAC | runs on |
|---|---|---|---|---|
| A | `0x88B6` | `0x88B9` + `0x88B5` | `…:00` | **QEMU** |
| B | `0x88B9` | `0x88B6` + `0x88B5` | `…:09` | **Renode** |

`0x88B9` is free in the fleet's allocated block `0x88B5..0x88BF`
(`B5` mcx, `B6` rt1180, `B7` imx95, `B8` imx91).

## ⭐ THE TEST CANNOT PASS BY ACCIDENT

**Each node's required peer set names the other model's EtherType.** Neither can
reach PASS by talking to itself, and neither can reach it via the synthetic third
peer alone — a node needs TWO verified peers. So "both printed PASS" *means* the
Renode node verified the QEMU node's frames and the QEMU node verified the
Renode node's. The third peer (`0x88B5`) is synthetic and deliberately **not**
one of the models, so a failure can be localised.

**Mutation-proven both directions**, exit codes captured without a pipe:

| run | rc | result |
|---|---:|---|
| control — both on one segment | **0** | HOLOBENCH PASS |
| mutation — Renode node moved to its own group | **1** | both FAIL lines, neither node saw the other |

## Two instrument errors on the way, both caught by reading from the subject

1. **A byte-count probe said node B contained no `0x88B9`** and looked like a
   failed build. It was blind: ARM encodes a 16-bit immediate split across the
   instruction, so the EtherType is not a literal in the image. What settled it
   was booting each node and reading the identity **it announces**:
   `ethertype=0x88B9 ... mac=54:27:8d:00:00:09`. *A finding read from the subject
   survives a bug in the observer.*
2. **The first mutation run reported `rc=0` while printing both FAIL lines.**
   The script was piped through `tail`, so `$?` was **tail's** status, not the
   harness's. Re-run without the pipe: mutation `rc=1`, control `rc=0`. A verdict
   read through a pipeline is a verdict about the pipeline.

Artifacts pinned: `lab3-0x88B6-peerB.elf`, `lab3-0x88B9-peerA.elf`; manifest 121
entries, 0 mismatches.

---

# 🔬 THE DELTA, CLASSIFIED BY RULE — 70/76, and the difference surface is TWO mechanisms

`scripts/classify_zephyr_delta.sh` replaces hand-labelling with a rule applied to
each row's own two captures. Over the 90-target re-cut:

| class | n |
|---|---:|
| identical | 44 |
| timing-only | 26 |
| **CONTENT-DIFFER** | **5** |
| TRUNCATED | 1 |
| UNSCOREABLE | 14 |

**Agreement: 70 of 76 scoreable targets.**

## ⭐ FOURTEEN TARGETS ARE UNSCOREABLE, AND SAYING SO IS THE POINT

`synchronization` and `philosophers` are **free-running samples**: they print
until a guard stops them and reach a terminal marker on **neither** side
(verified — 0 occurrences of `PROJECT EXECUTION` in both captures, every row).
Their line count measures how long the run lasted. On 2026-09-23 all four
`synchronization` rows moved `identical → partial` **while nothing about them
changed** — the runs simply lasted longer (cm33 33→324 qemu, 55→151 renode).

Counting such a row as agreement inflates the headline; counting it as
disagreement invents a defect. It is counted as **neither**, and the denominator
says so.

## The five content differences are all inside PASSING tests

Every one reaches `PROJECT EXECUTION SUCCESSFUL` on **both** models. They reduce
to two mechanisms and two nondeterministic values:

**Mechanism A — an invalid user pointer is refused at a different layer** (seen in
`kernel-device` and `kernel-threads-thread_apis`):

```
qemu   : E: ***** BUS FAULT *****  Precise data bus error  BFAR Address: 0xfffffff0
renode : E: syscall user_copy failed check: Memory region 0xfffffff0 read access denied
```

Both refuse the access and both tests pass; QEMU faults at the bus, Renode
catches it in the MPU/syscall check first. A real divergence, recorded rather
than explained away — which layer wins the race on silicon is the open question.

**Mechanism B — the fault dump's stacked frame** (`cm7 arm_interrupt`, the row
that originally found tlib defect #11):

```
qemu   : r14/lr: 0x00000000   xpsr: 0x00000000   pc: 0x00000000
renode : r14/lr: 0x000016c9   xpsr: 0x01000000   pc: 0x000016d8
```

> ❌ **SUPERSEDED — this paragraph is wrong. See "Mechanism B — RESOLVED" below.**
> The frame is architecturally UNKNOWN (the *stacking itself* faults into the MPU
> guard), and Renode's "populated" values are stale stack residue, not a modelled
> frame. Neither model is more physical. Reclassified UNSCOREABLE.

⚠ ~~Renode's side looks the more physical one: a fault dump with `pc = 0` and
`xpsr = 0` is implausible for a taken fault, since the stacked frame should carry
the faulting context.~~ **Not asserted** — deciding it needs the RM, and the test
passes either way.

**Not mechanisms:** `mem_protect-stackprot` differs in a **stack canary**
(`0xe375c474` vs `0xc6d8fa74` — random by design) and `timer-timer_monotonic` in
a measured delta within tolerance (`240023322` vs `240011756`, expected
`240000000`, both 100%).

## ⚠ THE CLASSIFIER'S FIRST RUN WAS NONSENSE, AND ITS OWN OUTPUT SAID SO

It reported **218 targets classified from a 90-row input**, with a blank class on
128 of them. Cause: `grep -c … || echo 0`. `grep -c` **prints a count and exits
non-zero when that count is zero**, so `|| echo 0` appended a *second* line;
every value became `"0\n0"`, breaking each comparison and emitting extra records.

It now asserts `rows == inputs` and **exits 2 reporting no agreement figure** if
they disagree — the check that turns a wall of plausible nonsense into a refusal.
*A classifier that cannot count its own output cannot be trusted with a headline.*

---

# 🎤 SAI RX — MEASURED, and it corrects a claim I have been repeating

The roadmap item read *"close the shared SAI RX gap on both sides, with a test
that consumes received samples — so the direction is proven, not asserted."*
Measured tonight with the stock SDK `sai/edma_record_playback` example, which
does exactly that: records over SAI RX and plays back over TX.

| model | console |
|---|---|
| QEMU | `MCUX SDK version: 2026.06.00` / `SAI example started!` — then stops |
| Renode | `MCUX SDK version: 2026.06.00` / `SAI example started!` — then stops |

**Byte-identical, and both stall at the same point.**

## ⭐ SO "RENODE IS AHEAD ON SAI RX" WAS AN OVERSTATEMENT

The claim, repeated in the README, the scorecard and to Kyle, was that this side
models an RX path the reference does not — a *divergence in the faithful
direction*. At the register level that is true: `RCSR/RCR1-5/RDR0-1/RFR0-1`
exist here and the oracle's own `PERIPHERALS.md` says *"RX path not modelled
(RFR reads empty)"*.

But **no sample source feeds that path on either side.** Nothing calls
`PushReceivedWord()`. From the firmware's point of view the two models are
indistinguishable: the record example blocks waiting for data that never
arrives, identically, on both.

> ⭐ **A REGISTER THAT NOTHING DRIVES IS NOT A CAPABILITY.** The RX path is real
> code with real semantics and zero observable consequence, which is precisely
> the shape of thing this project keeps catching in other people's models — and
> I had it in mine, and was quoting it as an advantage.
>
> It is the same error as the `led_blinky` row scoring RAN/RAN: I was comparing
> *what exists* instead of *what the guest can observe*.

## What closing it actually requires

Not more RX registers. A **sample source** on each side — a codec model, a file
backend, or a TX→RX loopback — plus a test whose assertion is the *received
payload*, not a counter. Until then this is honestly stated as:

**SAI RX: registers modelled here, absent there, observable difference NONE.**

Deliberately not built tonight: adding a Renode-only sample source would widen
the divergence while making the headline look better, which is the opposite of
the goal. This one needs both sides, and the oracle's half is theirs to write.

---

# 🔎 MECHANISM A, RESOLVED: it is an MPU/TT query divergence, and it points at QEMU

The `CONTENT-DIFFER` row `cm33-tests-kernel-device` (and `thread_apis`) showed:

```
qemu   : E: ***** BUS FAULT *****  Precise data bus error  BFAR Address: 0x0
renode : E: syscall user_copy failed check: Memory region 0 (size 1) read access denied
```

Both tests PASS on both models, so it was filed as "a divergence, which layer
wins is an open question". Chasing it to the source resolves it.

The test is `test_null_dynamic_name`: it deliberately hands a syscall a NULL
pointer. Zephyr guards syscall buffers with `arch_buffer_validate()` →
`arm_core_mpu_buffer_validate()` → `mpu_buffer_validate()`, which on ARMv8-M has
two variants (`arch/arm/core/mpu/arm_mpu_v8_internal.h`):

* **`!CONFIG_MPU_GAP_FILLING`** (:424) — a pure **software walk** of the MPU
  region registers: `is_enabled_region()`, `is_in_region()`,
  `is_user_accessible_region()`.
* **`CONFIG_MPU_GAP_FILLING=y`** (:471) — **`arm_cmse_addr_range_read_ok()`**,
  i.e. the ARMv8-M **TT (Test Target)** instruction, plus `arm_cmse_mpu_region_get()`.

> ⭐ **NEITHER VARIANT TOUCHES MEMORY.** Both only *ask* the MPU/SAU what the
> permissions are. So this was never a difference about how a fault is taken —
> it is a difference in **what the two models answer when asked whether address
> 0 is readable from unprivileged code.**

| model | answer to the query | consequence |
|---|---|---|
| Renode | not user-readable | guard rejects the syscall; **no access is made** |
| QEMU | user-readable | guard passes; the code then reads address 0 and **bus-faults** |

**Renode's answer is the one that matches the intent of the guard.** Address 0 is
covered by no Zephyr user-mode MPU region on this part (the M33's code TCM is at
`0x0FFE0000`; `0x00000000` is the *M7's* ITCM base, not the M33's). A syscall
guard that says "yes, userspace may read NULL" has failed at its job, and the
hardware fault afterwards is the backstop firing, not the design working.

⚠ **Not asserted as a QEMU defect — reported as a finding.** Deciding it needs
their MPU/TT implementation and the config the `device` test actually compiled
with, both of which are theirs. What is established here: the divergence is an
**MPU/TT permission-query** difference, not a fault-delivery difference, and it
is therefore worth a look on their side.

> ⭐ **THIS IS THE TWO-MODEL METHOD PAYING OUT IN THE OTHER DIRECTION.** Four
> Cortex-M defects were found in Renode's core by running the oracle's corpus
> against it. This is the first candidate found in the *oracle* by running the
> same corpus against both — which is exactly why the second model exists:
> *each one's failures bound the truth from a different side.*

---

# 🐞 RENODE DEFECT #18 — the MPU alias registers are mis-indexed, and a region can vanish

Found while auditing tlib's MPU write path for the address-0 dig. It is **not**
that dig's cause (proven below), but it is a real defect on its own merits.

## The bug

ARMv8-M: `MPU_RBAR_A1/A2/A3` and `MPU_RLAR_A1/A2/A3` address the region
**`(RNR & ~3) | offset`** — the aliases reach the other three regions of the same
aligned group of four. tlib computed:

```c
index = cpu->pmsav8[secure].rnr;
if(region_offset > 0) index = (index << 2) + region_offset;
```

Those agree **only while RNR < 4**, which is why it survived. At `RNR=4` ARM says
region 5; tlib computes 17.

**Why that is fatal rather than cosmetic:** `MAX_MPU_REGIONS` is 256 so nothing
faults — but `pmsav8_get_region` scans only `0..number_of_mpu_regions-1`
(`helper.c:3194`). A region stored at index ≥ that bound is **written, retained,
and permanently invisible to matching.** Any guest programming regions through
the aliases with RNR ≥ 4 silently loses them.

The same wrong formula is on **both** the write and read paths
(`arch_exports.c:905/916` set, `:965/976` get) — which is precisely why a region
walk cannot reveal it: the getter mis-indexes in the same direction as the setter.

It is in the guest's real path, not just Renode's inspection API:
guest store → Renode NVIC `HandleMPUWriteV8` (`NVIC.cs:1970`) →
`cpu.PmsaV8RbarAlias1` → `tlib_set_pmsav8_rbar(val, 1, secure)`.

## Proved at the API level — no firmware, no Zephyr boot

```
cpu PmsaV8Rnr 4
cpu PmsaV8RbarAlias1 0xDEADBE01
cpu PmsaV8Rnr 5  ; cpu PmsaV8Rbar   ->  0x00000000   ARM says it lands here. It did not.
cpu PmsaV8Rnr 17 ; cpu PmsaV8Rbar   ->  0xDEADBE01   tlib put it past the 16 scanned.
```

## Mutation-proven, swapping only the `.so`

| `translate-arm-m-le.so` | region 5 | slot 17 |
|---|---|---|
| `834f9fbc` stock | `0x00000000` | `0xDEADBE01` |
| `c0580759` patched | **`0xDEADBE01`** | **`0x00000000`** |

ABI gate before install: **2604 symbols both sides, 0 missing, 0 extra**, built
through the `Cores` wrapper. Old library kept as `.pre-aliasfix` so the proof
stays re-runnable. Regression on the patched core: **M0 PASS, 45 PASS / 0 FAIL,
no row changed verdict, coverage 55 == 55.**

## ⭐ AND IT IS NOT THE ADDRESS-0 CAUSE — TESTED BEFORE CLAIMED

This was a *very* attractive hypothesis: the cross-model dig had concluded, by a
chain neither side could break, that tlib must have **dropped a region write**,
and here is a mechanism that drops region writes silently. Under the pattern of
the previous day I would have posted it as the answer.

Instead: dumped MPU slots 0..47 at the guard. **Populated 0..11 only; 16..47 all
zero.** Nothing stranded. Zephyr programmed via `RNR`+`RBAR` directly, the bug
never fired on this binary, and the address-0 divergence remains open.

> ⭐ **A MECHANISM THAT COULD EXPLAIN THE SYMPTOM IS NOT THE CAUSE OF IT.** The
> fit was excellent — right failure shape, right subsystem, right direction — and
> it was still wrong. The cheap check that settled it was dumping the slots the
> mechanism would have written to.

## Numbering

Filed as **#18**, not #17. I briefly claimed "#17 = tlib denies when the MPU is
disabled" on the fleet bus and **retracted it** — the premise (MPU disabled at the
guard) came from reading `MPUEnabled`, which reports `cp15.c1_sys`, a register the
Cortex-M33 does not use. Reusing the number would collide with a withdrawn claim
in the shared record, so #17 stays withdrawn.

---

## Address-0 divergence — RESOLVED on the Renode side (2026-09-25)

**Status:** Renode's behaviour is fully explained and appears architecturally
correct. The open question has moved to the QEMU side.

### What I had wrong (three retractions)

1. **"TT never executes on Renode."** Measured on the wrong instruction. I hooked
   `0x3801d900`, which objdump places inside `arm_cmse_addr_read_ok` (the
   *singular*-address function). The *range* variant reaches `ttt` at
   `0x3800b5b6` / `0x3800b5be` inside `cmse_check_address_range`. Retracted.
2. **"tlib's MPU_TYPE.DREGION reads 0"** (the other team's hypothesis, which I had
   not yet falsified). MEASURED: `MPU_TYPE` reads back `0x00001000` → DREGION = 16,
   observed 700x in a live run. Dead.
3. **The 16-iteration software walk is not running** — only 4 MPU region-register
   reads occur in the entire run (vs 716 `RNR` writes). The TT path is the one in use.

Two separate instruments returned a silent zero today: `cpu.R0` (not a valid
accessor) and `execfile()` inside a hook. Both threw inside the hook with no output.
**A Renode hook swallows Python exceptions silently.** Every hook now carries a
positive control — a plain `DebugLog` of a literal — so "hook didn't fire" is
distinguishable from "hook fired, body threw". `hasattr` beats try/except here
because it cannot throw at all:

```
cpu AddHook <addr> "cpu.DebugLog(\"PROBE attrs=\" + str([a for a in (...) if hasattr(cpu,a)]))"
```
Valid accessors on the ARM CPU object: `GetRegister`, `GetRegisterUnsafe`, `R`, `PC`, `SP`.

### The measurement

ELF `cm33-tests-kernel-device.elf`, sha256 `ec27f94f3f7cbb99f967308cd3d4878199344aa4235c12cb472d49a65dd8d8b8`.
Hooks at `0x3800b5b6` (before `ttt`, reading r0/r1/r2) and `0x3800b5ba` (after, reading r3).

| # | r0 (addr) | r1 (last) | TT_RESP | verdict |
|---|---|---|---|---|
| 1 | `0x140020e8` | `0x140020fa` | `0x004d000b` | permit |
| 2 | `0x14002118` | `0x14002124` | `0x004d000b` | permit |
| 3 | **`0x00000000`** | `0x00000000` | **`0x00400000`** | **DENY** |

`ttt` fires 3x; `0x3800b5be` fires only 1x — benign, not a short-circuit. The
preceding `cmp.w ip, #31` skips the second TT when the range fits inside one
32-byte granule (`bls.n 3800b556`).

### Decoded from the binary, not from the spec

The `read_ok` arm is `0x3800b5d6`:
```
3800b5d6:  lsls r3, r3, #13   ; N := bit 18 of TT_RESP
3800b5d8:  bpl  3800b53c      ; bit18 clear -> movs r0,#0 -> return false
```
- `0x004d000b`: MRVALID(16)=1, bit18=1, MREGION=11 → PASS
- `0x00400000`: MRVALID(16)=0, bit18=0, MREGION=0 → DENY

Corroborated afterwards against the authoritative `cmse_address_info_t` bitfield in
`arm_cmse.h` (little-endian): `[7:0] mpu_region`, `[16] mpu_region_valid`,
**`[18] read_ok`**, `[19] readwrite_ok`, `[22] secure`. The binary-derived decode and
the header agree — `0x004d000b` is region 11, valid, read_ok, readwrite_ok, secure;
`0x00400000` is secure only, with no valid region and not readable.

The `flags` argument `r2=0xc` also decodes from the binary's own jump tables:
`r2 & 0x14 = 4` → tbb byte `0x48` → target `0x3800b5b2` (**the `ttt`, i.e.
UNPRIVILEGED, block**); `(r2 & ~0x14) - 1 = 7` → jump-table[7] = `0x3800b5d7`
(**the readable check**). So Zephyr explicitly requested an *unprivileged
readability* check — it believes it is validating a user thread's buffer.

Causality is adjacent in the log, not inferred:
```
line 116  TTARG  r0=0x0 r1=0x0 r2=0xc
line 118  TTRESP r3=0x400000
line 119  E: syscall user_copy failed check: Memory region 0 (size 1) read access denied
```
`r1=0` is consistent with `addr+size-1 = 0+1-1 = 0`, matching "size 1".

### Why Renode looks right

`ttt` is the unprivileged variant. `PRIVDEFENA`'s default map applies to privileged
accesses only, so it cannot make address 0 readable to an unprivileged access. No
enabled region covers address 0 (all 11 measured earlier). `MRVALID=0, R=0` is
therefore the correct answer and the deny is correct.

That flips the burden: QEMU reports a BUS FAULT with `BFAR=0`, which means a
dereference actually happened, which means **their guard permitted**. Combined with
their measurement of zero TT queries on address 0, the leading hypothesis is that
this is **not an MPU divergence at all** but a privilege/control-flow divergence
upstream of the MPU — the guard never runs on their side. Asked them for (a) their
ELF sha256, (b) a positive control on their TT counter (does it see the two
`0x1400xxxx` queries?), (c) `CONTROL.nPRIV` at syscall entry.

### Correction, same session: no privilege divergence

I floated "unprivileged on Renode, privileged on QEMU" and then measured it, which
killed it. Hook at `0x3800b5b6` reading `cpu.Control`, all three TT sites:

```
r0=0x140020e8  CONTROL=0x2
r0=0x14002118  CONTROL=0x2
r0=0x00000000  CONTROL=0x2
```

`CONTROL=0x2` → SPSEL=1 (process stack), **nPRIV=0 → the CPU is PRIVILEGED** at the TT.

That is the correct picture, not an anomaly: the kernel runs privileged inside the
syscall and deliberately asks the *unprivileged* question — "would the user thread
have been allowed to read this?" — which is exactly what `ttt` + `r2=0xc` encodes.
`CONTROL.nPRIV` therefore cannot discriminate between the two models; the discriminator
is whether QEMU executes this `ttt` on address 0 at all, and what it returns.

**Renode hazard:** `cpu.GetRegister(16)` SIGABRTs the whole process (out-of-range index
into native tlib). `cpu.Control` is the safe accessor for CONTROL. Note the contrast in
failure modes — an invalid *Python attribute* (`cpu.R0`) throws silently inside the hook
and yields nothing, while an invalid *register index* aborts the process loudly. The
silent one is far more dangerous: it looks exactly like "the code never ran".

---

## Mechanism B — RESOLVED: architecturally UNKNOWN, not a fidelity gap (2026-09-25)

Earlier note: *"Renode's side looks the more physical one: a fault dump with pc = 0
and xpsr = 0 is implausible for a taken fault."* **That judgment was wrong** — it was
a plausible-sounding inference with no mechanism behind it. Resolved properly:

### First, the comparison was narrower than I had recorded

`cm7 arm_interrupt` emits **five** ESF dumps. Four are byte-identical between models.
In particular the `test_arm_esf_collection` dump — the one the test actually
*validates* — is identical on both, `pc = 0x0000f602` included:

```
E: r0/a1:  0x00000000  r1/a2:  0x00000001  r2/a3:  0x00000002
E: r3/a4:  0x00000003 r12/ip:  0x0000000c r14/lr:  0x0000000f
E:  xpsr:  0x01000000
E: Faulting instruction address (r15/pc): 0x0000f602
```

Those match the pattern `set_regs_with_known_pattern()` seeds (`r0=0,r1=1,r2=2,r3=3,
lr=15`). Both models get the checked case exactly right.

### The one that differs is the deliberately-broken stacking

The fifth dump is `ZEPHYR FATAL ERROR 2: Stack overflow`, provoked by
`arm_interrupt.c:421-448`:

```c
expected_reason = K_ERR_STACK_CHK_FAIL;
__disable_irq();
irq_controller_set_pending(i);
__set_PSP(_current->stack_info.start + 0x10);   /* almost at the bottom */
__enable_irq();
```

PSP is parked 0x10 above the stack base and an interrupt is fired. The 32-byte
exception frame descends to `stack_start - 0x10` — **into the MPU guard** — so the
*stacking itself* faults (MSTKERR; cm7 is ARMv7-M, so `CONFIG_HW_STACK_PROTECTION`
is an MPU guard region, not PSPLIM). The handler then reads an ESF **that was never
written**. Its contents are architecturally UNKNOWN.

| model | reads back |
|---|---|
| QEMU | `lr=0`, `xpsr=0`, `pc=0` — the denied read returns zero |
| Renode | `lr=0x000016c9`, `xpsr=0x01000000`, `pc=0x000016d8` — the read returns backing RAM |

Renode's values are **stale stack residue, not a modelled frame**: `nm` puts both
inside `_arm_interrupt_test_arm_interrupt_wrapper` (`0x159c`..`0x1738`), the very
function that provoked the fault, and `0x16c9` is odd — a Thumb return address, i.e.
a genuine leftover pushed by that function earlier.

### Verdict

**Not a fidelity gap, and not scoreable.** The value is UNKNOWN after a failed
stacking; the two models differ only in what a read of the guard region returns
(zero vs. backing store). Zephyr asserts only `expected_reason`, which is
`K_ERR_STACK_CHK_FAIL` on both — `Caught system error -- reason 2`, `PASS` on both.
Reclassified from "divergence, unexplained" to **UNSCOREABLE (architecturally
undefined)**.

### Also found: a dead results directory

`results/console-cm7delta/` is a **failed capture** — every `qemu_*.txt` in it is
0 bytes. A diff against it would have reported the entire Renode console as
"added lines" and could easily have been read as a total divergence. The live data
is `results/console-delta/`. Deleting the dead directory rather than leaving a trap.

---

# ⭐ ADDRESS-0 ROOT CAUSE: Renode does not fault on unmapped accesses (2026-09-25)

**This is a Renode-side defect, and it is mine to carry.** It also supersedes the
framing of the whole two-day dig: the answer was never in the MPU, the TT, privilege,
or a branch inside the guard.

## The caller dereferences before it validates, deliberately

`k_usermode_string_copy` @ `0x3801b9fc`:
```
3801ba0a:  bl  3800fca4 <arch_user_string_nlen>   ; DEREF FIRST
3801ba0e:  ldr r7,[sp,#4]                          ; err
3801ba10:  cbnz r7, 3801ba58                       ; faulted -> return 14, NEVER validates
...
3801ba3a:  bl  3801b93c <k_usermode_from_copy>     ; the TT validation lives HERE, SECOND
```

`arch_user_string_nlen` @ `0x3800fca4` is a *deliberate* fault-tolerant probe — the
symbol names say so:
```
3800fcae <z_arm_user_string_nlen_fault_start>:
3800fcae:  ldrb r5, [r0, r3]      ; r0 = NULL. THIS is the deref.
3800fcb0 <z_arm_user_string_nlen_fault_end>:
3800fcb0:  cbz  r5, strlen_done   ; byte == 0 -> length 0, err cleared
```
Zephyr *wants* this to fault and catches it with the fixup range
`fault_start..fault_end`.

## One instruction explains both traces

| model | `ldrb r5,[0]` | consequence |
|---|---|---|
| QEMU | **BUS FAULT**, BFAR=0 | fixup → `err=-1` → return 14 → `k_usermode_from_copy` never runs → **no `ttt(0)`** |
| Renode | **returns 0, no fault** | NULL looks like an **empty string** → `err=0` → falls through → `ttt(0)` → deny |

The oracle measured no `ttt(0)` in QEMU's entire run; I measured `ttt(0)` returning
`0x00400000`. Both fall out of that single difference. Nothing else is needed.

## Measured

```
(monitor) sysbus ReadByte 0x0
[WARNING] sysbus: ReadByte from non existing peripheral at 0x0.
0x00
```

Address 0 is unmapped on **both** models — we were both right about the map. The
difference is the *response*: QEMU raises a bus error, Renode logs a warning and
returns zero. Renode's default `sysbus UnhandledAccessBehaviour` is `Report`, i.e.
non-faulting. **Silicon returns a bus error here, so QEMU is right and Renode is wrong.**

My `ttt(0)` result is correct *and irrelevant* — that code should never have been
reached.

## ⚠ CORRECTION: the knob IS the right mechanism

I first wrote that `ThrowException` is *"not a guest bus fault — a host-side exception
that silently killed the CPU."* **Wrong, and retracted.** I inferred it from the
silence instead of reading the path or measuring the CPU.

`sysbus UnhandledAccessBehaviour` accepts `Report | ReportIfTagged | ReportIfNotTagged
| DoNotReport | ThrowException`, and `ThrowException` produces a genuine precise guest
BusFault:

```
SystemBus.ReportNonExistingRead  -> throw BusAccessException(AddressError)
TranslationCPU (catches it)      -> HandleBusAccessError(addr, width, op, err)
CortexM (override)               -> tlibRaisePreciseBusFault(addr)
tlib arch_exports.c              -> v7m.bus_fault_address = addr
                                    BFSR |= BFARVALID | PRECISERR
                                    raise EXCP_BUS_FAULT
```

That is the same thing QEMU produces. And the CPU was never killed — MEASURED:
`IsHalted = False`, `ExecutedInstructions = 0x2A993` (174,483), `PC = 0x3801e344`,
which `nm` places inside `arch_system_halt`:
```
3801e336 <arch_system_halt>:
3801e344:  b.n 3801e344      <- spinning here
```
The guest took a real BusFault, judged it fatal, and **halted itself** before console
init. *"No console output" was the guest halting, not the host aborting.* Reading
silence as a dead emulator is the same error as the hooks that threw quietly — third
time this session. **Silence is never evidence; measure the state.**

## The actual difference, and the oracle's design

`imxrt1180_soc.c:479`:
```c
create_unimplemented_device("imxrt1180.periph", IMXRT1180_PERIPH_BASE,
                            IMXRT1180_PERIPH_SIZE);   /* 0x40000000, 0x20000000 */
/* "Real models are mapped AFTER the catch-all so they override it by
    memory-region priority." */
```

So:

| access | QEMU | Renode (today) |
|---|---|---|
| inside `0x4000_0000..0x5FFF_FFFF` | catch-all returns 0, no fault | returns 0, no fault |
| outside it (address `0`) | **BUS FAULT** | returns 0, no fault |

The two models agree inside the window *by accident* and disagree outside it.

Census of every unmapped access in the cm33 device run — **9 accesses, 5 distinct
addresses**:
```
0x0                                            <- the deliberate NULL probe, MUST fault
0x443C0020 0x443C0024 0x443C0094 0x443C0098    <- IOMUXC_AON, inside the window
```

## The obstacle, stated before attempting

Renode's `ThrowException` check happens **before** the tag lookup in
`ReportNonExistingRead`, so `sysbus Tag` cannot serve as the catch-all — tagged ranges
fault too. And Renode errors on overlapping registration (`E39`), so a low-priority
blanket region under the real peripherals is not expressible the way memory-region
priority allows.

The fix is therefore most likely a small Renode patch making `ThrowException` respect
tags, giving Renode an exact equivalent of `create_unimplemented_device`. That is
upstreamable, and preferable to special-casing address 0 — which would game the test
rather than fix the model. **Not claimed closed until built and measured.**

## Scope before claiming a fix

- Very likely the **same cause** for `cm33-tests-kernel-threads-thread_apis` at
  `0xfffffff0` (same shape, also unmapped). One fix should close both remaining rows.
- A general "unmapped faults" mode would expose every remaining hole in my peripheral
  map that the non-faulting default currently papers over — the cm7 console alone shows
  unmapped hits on RTWDOG (`0x442D0000`, `0x442E0000`, `0x42490000`, `0x424A0000`,
  `0x424B0000`) and BLK_CTRL (`0x444F0080`, `0x44470010`). So this is **not** a one-line
  flag flip, and I will not claim it as closed until it is measured.
- tlib clearly *can* raise guest faults (cm7 `arm_interrupt` takes a genuine MPU
  stacking fault), so the mechanism exists; the question is routing an unmapped sysbus
  access to it.

## ⭐ The generalisable lesson (sharpened by the oracle)

I had framed a non-faulting unmapped-access default as a *passive* gap — "it papers
over holes in the peripheral map." The oracle pointed out the half I missed, and it is
the more important half:

> **A non-faulting unmapped default actively PERTURBS GUEST CONTROL FLOW** wherever the
> guest uses a deliberate probe-and-fixup idiom — `arch_user_string_nlen`,
> `k_usermode_string_copy`, anything that dereferences a pointer *in order to test
> whether it is reachable*.

So it is not a quiet omission that shows up as a missing feature. It produces
**wrong-but-plausible behaviour that surfaces two subsystems away from its cause**. We
spent two days inside the MPU because that is where the *symptom* appeared — every
fact either of us measured about regions, TT responses, DREGION, aliases and privilege
was correct, and all of it was about a path Zephyr's fault-fixup means should never
execute.

**Faulting on unmapped access is load-bearing for fidelity precisely because guests are
written to depend on the fault.**

### The tradeoff, with both signs observed

The oracle also confirmed my prediction that a faulting mode "will light up every hole
in the map" — from the other end of it:

> "my model bus-faults on unmapped by default, and that is exactly what drove my whole
> bring-up — every missing peripheral announced itself as a guest fault on first
> access, which is how the `-d unimp,guest_errors` loop found them one at a time."

So: **the faulting default costs you up front and buys correctness; the non-faulting
default is cheaper up front and bills you later.** Renode took the cheap side and this
defect is the invoice. Same shape as the TCM/XIP tradeoff we hit earlier, opposite sign.

This is a genuine authoring-effort finding for the experiment's third axis, not just a
bug note: Renode's default made M0 fast and made this class of defect invisible until a
guest that *depends* on faulting came along.
