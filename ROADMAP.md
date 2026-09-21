# Roadmap — functional equivalency with the RT1180 QEMU model

> **The mission, in Kyle's words:** *"I want to do the exact same things (run the
> same software) on a Renode implementation of rt1180 as we can on a qemu
> implementation of rt1180."*
>
> **The acceptance test, in Kyle's words:** *"holobench booting 1180 renode
> alongside qemu 1180 and not be able to tell a functional difference."*

That test is strictly harder than any corpus, and the difference matters: a corpus
asks *"does row N pass on both?"*, the swap test asks *"is there ANY observable
on which they differ?"* — including observables nobody wrote a row for. Every
milestone below is therefore gated on a **measured** comparison, not on a count of
features built.

**Scope decisions (Kyle, 2026-09-19):**
- Holobench observes **all four** surfaces: console + verdict, dual-core /
  inter-core, Ethernet I/O, audio streams. Nothing is out of scope.
- Holobench runs on **this tree**, including the patched `translate-arm-m-le.so`.
  Upstreaming Renode defects is therefore **optional** — a documented patch bundle
  is a valid deliverable, and upstream becomes a nice-to-have rather than a gate.

---

## Where we actually are

All MEASURED, on this box, this session or earlier:

| axis | state |
|---|---|
| Vendor SDK console corpus | **31/31** rows agree, `differ 0` — re-verified again after the XIP base-map change |
| Zephyr console delta study | **75/83 (90 %)** agree; every non-agreeing row has a named cause |
| Oracle value tests, motor/ADC group | **13/13 PASS** — their binaries, unmodified, on my models |
| Dual-core rows | **all closed** — `cm7wait`, `dualcore`, `mu`, `ele-corestart`, `edma-swstart-order` |
| Audio | `sai` **byte-exact at two operating points**, verdict rendered outside the guest |
| Renode defects found | **14**; #11–#14 mutation-proven fixed locally, patch bundle in `patches/` |
| Oracle value-test sweep | **45 PASS** of 55 (was 19) — **0 FAIL**, 0 NO-OUTPUT; the other 10 ship their own harness |
| Renode defects found | **15**; #11–#14 fixed in tlib and mutation-proven, #15 (LPSPI ignores `TCR[CONT]`) worked around with an own model |
| NETC | **4/7** — `ptp`, `fdb`, `fwd` with no frame backend; `rxfwd` via `IMACInterface` + RX ring |
| Timers | **4/4** — `lpit`, `tmr`, `timers2`, `fslaudit` (LPIT1/TMR1/GPT1/TPM1/LPTMR1 modelled) |
| Oracle test suite coverage | the full sweep now routes by structure; 6 rows reclassified `NEEDS-OWN-HARNESS` |

> **2026-09-20 — what changed overnight.** Defect **#14** (a Cortex-M sleeps inside its
> own interrupt handler: exception entry never clears `env->wfi`) found, root-caused,
> mutation-proven, and its mechanism corrected after I published a first version that my
> own evidence contradicted. The three "dual-core" rows turned out to have **three
> independent causes**, two of them mine. And auditing my own harness reclassified six
> audio/ethernet rows that had been recorded as failures of *somebody else's model*
> while never actually being run. Detail in [`EXPERIMENT.md`](EXPERIMENT.md).

**The 50 unattempted tests are the roadmap.** They are not speculative work items —
they are binaries that exist, with goldens that exist, that the other tool already
passes. That is the best possible backlog: every item is pre-specified and
pre-verified by an independent implementation.

| group | tests | do I have the blocks? |
|---|---:|---|
| MISC-CM33 | 31 | mostly yes — FlexSPI, LPI2C, MECC, MU, USB, eDMA, LPUART all modelled |
| DUAL-CORE | 7 | yes — `mimxrt1189_m2.repl` already boots M33+M7 (M2 rung) |
| ETHERNET | 7 | NETC modelled **at register level only** — no `IMACInterface`, so no frame has ever crossed it |
| AUDIO | 5 | SAI now has a file-only WAV sink and passes `sai`; ASRC's resampler is not yet a bandlimited FIR |

---

## M-A — Ship the patched core as the baseline  ⟵ *IN PROGRESS, 2026-09-19*

**Status:** defect #13 **implemented and verified** overnight
(`patches/tlib-defect13.patch`, 166 lines). `arm_interrupt` now 83/83 lines with
`SUITE PASS`, differing only in three 1 ms duration roundings; `arm_thread_swap`
still byte-identical 29/29 with all three patches stacked. ABI-verified 2564
symbols (0 missing / 0 extra), M0 positive control green. Remaining: install as
the default library and re-cut the delta study + equivalency corpus on it.

**Why first:** holobench runs on this tree, so the patched tlib **is** a
deliverable, not a side experiment. Right now every published number is measured on
`ppbpatch-baseline` — stock 1.17.0 plus only the patch without which the M7 cannot
boot — while `d11d12-verified` (defects #11 + #12 fixed, ABI-verified, M0 positive
control) sits beside it unused. That was the right call while "what does a Renode
user get?" was the question. It is the wrong call now that the answer is "this
tree."

1. Fix **defect #13** (ARMv8-M `MSPLIM`/`PSPLIM` stored but never enforced; no
   `CFSR.STKOF`). Needs frame-size precomputation before the FP/base pushes, the
   limit-selection rule, the `CCR.STKOFHFNMIGN` exemption, and routing through
   tlib's derived-stacking-fault path.
2. Promote the patched library to the installed default; keep all three side by
   side with the provenance table intact.
3. **Re-cut** the delta study and the equivalency corpus on it.

**Gate: MET.** `arm_thread_swap` → **IDENTICAL**. `arm_interrupt` → **83/83 with
`SUITE PASS`**, 3 changed lines, all printed durations. Six already-identical rows
stayed identical (no regressions). Delta study on the patched core: **75/83 (90 %)**
vs 73/83 on the baseline, published in `results/PATCHED-CORE.md` with the library
named. Remaining: promote the patched library to the installed default once no
measurement is in flight against the baseline.

**Estimate (DERIVED, not measured): 1–2 days**, most of it #13.

---

## M-B — Measure the backlog  ⟵ *DONE, 2026-09-19. AND IT CORRECTED ME.*

`scripts/run_value_sweep.sh` ran all 54 attemptable oracle binaries across three
platform groups, on the **baseline** library so the measurement was not split
across two cores mid-flight.

**MEASURED result:**

| group | PASS | FAIL | NO-OUTPUT | note |
|---|---:|---:|---:|---|
| MOTOR / ADC | **13** | 0 | 0 | 3 of those via dedicated runners (below) |
| DUAL-CORE | 1 | 2 | 4 | M7 semihosting gap identified |
| broad CM33 | **3** | **20** | **10** | the real work |

> ⭐⭐ **MY OWN ESTIMATE IN THIS DOCUMENT WAS WRONG, AND THAT IS THE POINT OF THE
> MILESTONE.** M-D says of the CM33 group: *"probably the highest yield per hour…
> Expect a meaningful fraction to pass on the first run."* **Three of thirty-three
> passed.** The blocks are modelled and were built from the oracle's own C models,
> and that still did not carry them. Had M-C→M-F been sequenced on that guess, the
> schedule would have been wrong by roughly an order of magnitude on the largest
> group — which is precisely why this milestone runs before the ones it sizes.

**A harness limitation, marked as one rather than as a miss.** `motor-load`,
`motor-sat` and `motor-thermal` are *parameter sweeps*: QEMU passes
`-global imxrt1180-motor.{init-mrads,load-fan-unms,sat-isat-ma,thermal,…}` per run
and Renode takes them at construction, so each config needs its own `.repl`. The
generic sweep cannot express that, so those rows read **`PASS(runner)`** citing
`run_motor_load.sh` (5 configs) and `run_motor_sat.sh` (4 configs), where they pass
against closed-form goldens. A harness that could not set a test up has not
measured it, and scoring it as a miss would understate the model on a row already
proven.

**Next actions, from the data rather than from a plan:**
1. `mimxrt1189_m2_value.repl` wires semihosting only on the M33, but `cm7boot`,
   `cm7-mpu` and `cm7-systick` are **M7 images** (`cm7boot.elf` entry `0x9` =
   ITCM@0, the M7's local view) — their output has nowhere to go. Cheap fix, then
   re-run the 7 DUAL rows.
2. The 20 CM33 `FAIL`s are the useful kind: the test **ran and disagreed**. Those
   are real model gaps with a golden attached, and they are now the bulk of M-D.
3. The 10 CM33 `NO-OUTPUT`s need triage — short budget vs missing platform feature.

Run **all 50** remaining oracle tests on the platforms that already exist, scoped by
their own platform map. Do not build anything first.

**Why this is milestone 2 and not an afterthought:** today's session is the
argument. The cm7 FOC row sat at `NOT-ATTEMPTED` behind an estimate that was wrong
in *both* directions — it named `TMR1` and missed `EQDC1` and `CMP3` entirely, and
it treated "define what proof means" as unbuilt work when the proof already existed
as binaries on the other tool. Four walls fell in an afternoon once they were
measured one at a time instead of estimated all at once.

**Gate:** a `results/value-tests.tsv` covering all 63, every row PASS / FAIL /
OUT-OF-SCOPE with a reason. **No row may read FAIL because of a platform I did not
build** — scope is declared, never discovered by running everything (that mistake
was made and caught today).

**Estimate (DERIVED): 1 day.** Cheap, and it converts the four milestones below
from estimates into measurements.

---

## M-C — Dual-core equivalency (7 tests)  ⟵ *MEASURED, 2026-09-19*

**Result of the first run, after adding per-core semihosting:**

| test | result |
|---|---|
| `ele-corestart` | **PASS** — kick-CM7 → `0xE1D20206` + `0xD6` |
| `cm7wait` | **FAIL** — `M7 never ran after CPUWAIT was cleared` |
| `edma-swstart-order` | **FAIL** — `DONE set but payload wrong` |
| `cm7boot`, `cm7-mpu`, `cm7-systick`, `dualcore` | no output — *because the M7 never runs*, same root cause as `cm7wait` |

> ⚠ **RETRACTED WITHIN THE HOUR — IT WAS MY HARNESS, NOT THE MODEL.**
> I first read `cm7wait`'s *"M7 never ran after CPUWAIT was cleared"* as a
> divergence between the SDK's release sequence and the minimal one, and wrote that
> here (and sent it to @rt1180emulator) before checking. **Then I turned the
> logging on**, and the model's own messages said the opposite:
>
> ```
> src: SRC.SCR.BT_RELEASE_M7 set -- gate 1 open (M7 reset released, still held by CPUWAIT)
> blkctrl_s_aonmix: M7_CFG.WAIT cleared -- gate 2 open (INITVTOR=0x303C0000)
> src: BOTH GATES OPEN -- starting the Cortex-M7 at vector table 0x303C0000
> ```
>
> **Both gates opened and the M7 started, at the right vector.** The two-gate
> release is honoured exactly as the minimal test drives it. What was missing was
> the M7's *code*: `cm7wait` and `dualcore` ship **two** ELFs and QEMU runs them as
> `-kernel m33.elf -device loader,file=m7.elf`, while my sweep loads one image per
> test. The M7 started and executed zeros, so it never stamped the magic the M33
> polls for — and the test reported precisely what it saw.
>
> ⭐⭐ **A TEST THAT NAMES A MECHANISM MAKES A WRONG DIAGNOSIS SOUND RIGHT.** The
> failure string named the exact gate I knew was the hardest part of the port, so
> it read as confirmation of a plausible story. It took ten minutes to check and I
> had already published the claim. *The better the error message, the more it is
> worth verifying before repeating it.*

> ⚠⚠ **AND THE RETRACTION WAS ALSO WRONG.** Loading both images did *not* fix it.
> Nor did loading the M7 image as raw data (`objcopy -O binary` +
> `sysbus LoadBinary @0x303C0000`), which removes any chance of the loader touching
> CPU state. `cm7wait` still fails.
>
> **Two diagnoses, both wrong, on one row, inside an hour.** What is actually
> measured, and nothing more:
> - the CPUWAIT bit at `0x444F0080` **is** wired and **is** the trigger — gate 1
>   alone leaves the M7 held; clearing CPUWAIT is what starts it;
> - the M7 is started with `SP=0x30440000 PC=0x303c0008`, read from the M7's own
>   bus context, matching `m7.elf` exactly;
> - and it **does not execute** — its four instructions write a magic to
>   `0x20490000`, which stays `0`.
>
> So the open question is narrow and *mine*: does `cpu1` actually resume when
> `IsHalted` goes false, and does it fetch from `0x303C0000` in its own context.
> That is a Renode core/platform question, not a gate question.
>
> ⭐⭐ **THE RULE I BROKE TWICE: A WELL-WRITTEN FAILURE STRING IS EVIDENCE ABOUT THE
> FIRMWARE'S VIEW, NOT ABOUT WHICH SIDE IS AT FAULT.** `cm7wait`'s message named
> the exact gate I believed was fragile, so it read as confirmation twice running.
> After the second wrong diagnosis the correct move is to stop diagnosing and
> report only what is measured — which is what the bullets above are.

**Three rows split off as harness-capability, confirmed by @rt1180emulator:**
`cm7boot`, `cm7-mpu`, `cm7-systick` use their machine's **boot-cm7 auto-detect** —
the machine boots the M7 directly from a cm7 ELF, holding the M33, with *no guest
release sequence in the image at all*. That is a deliberate convenience on their
side, not silicon (on real silicon a cm7-only image cannot run without the M33
releasing it). Same class as my XIP-linked and per-core-semihosting rows. They need
an auto-boot-cm7 feature or a two-gate wrapper here — **not** a CPUWAIT fix.

**Dual-core column, corrected:** 1 PASS (`ele-corestart`), 1 open item (`cm7wait`,
narrowed to cpu1 resume/fetch), 1 real gap (`edma-swstart-order`), 3
harness-capability, 1 to re-run (`dualcore`).

**First action for M-C:** determine whether `cpu1` resumes and fetches correctly
after `IsHalted = false` — a two-line probe, done rested.

### ⭐⭐⭐ M-C RESOLVED, 2026-09-19 — and the shared cause never existed

The probe was done, and the answer to *"does `cpu1` resume and fetch?"* was **yes,
it always did.** The three rows that failed in lockstep had **three independent
causes**, and the one everybody (me, and @rt1180emulator's prediction) was chasing
was not among them.

| row | cause | layer | fix |
|---|---|---|---|
| `cm7wait` | M7 was **never held** — `IsHalted` read False *before machine start*, PC=0, and a pre-loaded image free-ran from the ITCM alias at `0x0` within 0.01 s virtual | **platform description** (mine) | `init: IsHalted true` on `cpu1` |
| `dualcore` | it was **passing all along** — `SemihostingHandler` is per-CPU and I captured only `cpu`'s backend, so the M7's line went to a file I never read | **instrument** (mine) | capture both backends |
| `mu` | exception entry does not clear `env->wfi`; the core sleeps **inside its own handler** | **CPU core** (tlib) | defect #14, mutation-proven |

**Dual-core column, final: 8/8.** `ele-corestart` · `cm7wait` · `dualcore` · `mu` ·
`edma-swstart-order` · `cm7boot` · `cm7-mpu` · `cm7-systick` — **all PASS**. Plus
`multicore_manager` and `rpmsg_lite_pingpong` still passing (51 ping-pong round trips).

The last three were parked as "harness-capability" — their machine auto-detects a cm7
ELF and boots the M7 directly, a convenience on their side and not silicon. I mirrored
it instead of pleading incapacity, and the detection is **structural**: a cm7-linked
image enters at `0x9`/`0x35`/`0x75` (the M7's local ITCM view) where every CM33 image in
this corpus enters near `0x0ffe0011`, so the sweep reads the ELF header rather than
guessing from a name. Loading them into `cpu` — the M33, where those addresses are
nothing — is why all three read NO-OUTPUT for weeks.

`cm7boot` then failed its fourth clause, "background view", because the dual-core
platform had **no ADC1** for the M7's background alias to reach. A true statement about
a machine with no ADC in it. ADC1/2 added (bases and IRQs from the oracle's SoC wiring);
the row passes.

> ⭐⭐ **"THEY FAILED TOGETHER" IS A HYPOTHESIS ABOUT CAUSES, NOT EVIDENCE OF ONE.**
> Three rows moved in lockstep through two separate fixes and *still* had three
> independent causes. Correlated symptoms are exactly how a shared **observer**
> looks — not just a shared defect. And the observer is the one component nobody
> thinks to probe, because it is the thing you are probing *with*.

**Two of the three were mine, not Renode's.** That ratio is the finding worth
carrying into M-D through M-H: when a cluster fails together, the instrument and the
initial conditions deserve the first probe, not the last.

### Original scope

`cm7boot`, `cm7wait`, `cm7-mpu`, `cm7-systick`, `dualcore`, `ele-corestart`,
`edma-swstart-order`.

This is the **original M2 payoff** and Renode's home turf — native multi-core
versus QEMU's socket-glued approach was the headline question of the whole
experiment. The platform exists and boots both cores. These tests pin the parts a
console banner cannot: CPUWAIT/two-gate release ordering, per-core SysTick rates
(now 240 / 792 after today's clock work), the M7's own MPU, and eDMA software-start
ordering.

**Gate:** 7/7, plus `multicore_manager` and `rpmsg_lite_pingpong` still passing.

---

## M-D — The broad CM33 surface (31 tests)

`flexspi`, `lpi2c`(+dma), `lpspi`(+dma ×2), `lpuart-fifo`, `mu`, `mecc`, `lpit`,
`tmr`, `timers2`, `usb`, `usdhc`(+migrate), `flexcan`, `xip`, `ele`, `edma`,
`dmareq`, `wm8962`, `misc1`, `m33-usagefault`, `uartlink`, `hello`, `fslaudit`…

Largest group, and probably the **highest yield per hour**: most of these blocks
are already modelled and were built against the oracle's own C models. Expect a
meaningful fraction to pass on the first run — and expect the failures to be
**wiring between correct blocks**, which is the defect class that showed up four
separate times today (`pwmadc`, `pwm-dma`, `lpadc-dma`, the XBAR trigger chain) and
which a per-block corpus is structurally blind to.

Some entries here are *meta*-tests of the oracle's own tree (`corpus`,
`reset-values`, `blindspots`, `clocktree`, `zephyr`). Those are not equivalency
rows; triage them out explicitly rather than letting them read as failures.

**Gate:** every non-meta test PASS, or a named defect with the mechanism located.

---

## M-E — Audio (5 tests)

`sai`, `sai-dma`, `sai-audiopll`, `asrc`, `asrc-async`. SAI, ASRC and the WM8962
codec are modelled and `sai/edma_transfer` is already one of the byte-identical
two-tool agreements. Needs a CM33 audio platform and the AUDIO PLL path through the
CCM (`sai-audiopll` will exercise exactly the clock-tree fidelity that the `pwm`
test just proved matters).

### ⭐⭐⭐ Scoped properly, 2026-09-19 — and three of these five were never run

Reading the tests instead of their results changed what this milestone is.

**`sai`, `sai-audiopll` and `sai-dma` ship their own harness** (`check.py`) and
render the verdict **outside the guest, from the samples in a WAV file**. The
firmware *refuses to run* unless the harness pokes a rate selector at
`0x20001000` (`{magic "SAI1", div}`), and its Makefile says so in as many words:

> *"Launching the ELF bare (no `-device loader`) makes the firmware correctly FAIL
> 'rate selector never armed': a diagnostic, not the test."*

**My sweep launched all three bare and wrote `FAIL` into the results table.** Those
were not model verdicts. They were verdicts about a configuration that was never set
up — the `fslaudit` sin and the `dualcore` sin a third time, and this time pointed at
*somebody else's model*, which is the direction I have the least standing to get
wrong. They are now `NEEDS-OWN-HARNESS`, out of the FAIL bucket entirely, and
`scripts/run_sai_value.sh` + `scripts/check_sai.py` drive them the way they were meant
to be driven.

### And the real gap the proper harness exposes

With the harness correct, the model's actual defect is visible — and it is the exact
one their test was written to catch:

```csharp
txFifo.Dequeue();            // no audio sink; see the header note
```

My SAI computed a *correct* sample rate, paced a *correct* FIFO drain, raised FRF and
FEF correctly — and **threw every sample away**. A block that accepts every write and
emits nothing passes every in-guest test ever written against it.

> ⭐⭐ **THE ORACLE'S WORD IS NOT THE ORACLE.** A verdict computed inside the guest
> cannot distinguish a working device from one that swallowed the data. `snd_pcm_writei()`
> succeeds beautifully against a device faithfully clocking zeros. Only something
> outside, *looking at the samples*, can tell the difference — which is why this
> milestone's assertion is a WAV file and not a console string.

`IMXRT1180_SAI.cs` now has a **file-only** sink (`CreateWavBackend`) — never a host
device, so the capture *is* the mute and *is* the evidence. The header is written from
the block's **own** registers on the first sample, which makes the rate an assertion
rather than a harness input: a model that ignores `TCR2[DIV]` renders both sweep points
at the same rate and is caught at the second one.

**Two operating points, `DIV ∈ {14, 29}` → 25 000 Hz and 12 500 Hz.** One is not a test:
a rate-ignoring model reproduces the golden perfectly at whichever single rate you
happened to pick.

**Gate:** all three SAI rows byte-exact at *both* operating points, verdict rendered
outside the guest. `asrc` / `asrc-async` have no external harness, so their failures are
in-guest verdicts and are real model work.

### ✅ `sai` CLOSED, 2026-09-20 — byte-exact at both operating points

```
--- div=14 ---   SAI: PLAYED 4096 samples (the WAV is the assertion)
    ok  DIV=14 -> 25000 Hz: 4096 samples, byte-exact, peak 32761, 4095 non-zero
--- div=29 ---   SAI: PLAYED 4096 samples (the WAV is the assertion)
    ok  DIV=29 -> 12500 Hz: 4096 samples, byte-exact, peak 32761, 4095 non-zero
```

Two rates a clean 2× apart, both exact, so `TCR2[DIV]` is genuinely honoured — the
blind spot a single-rate test structurally cannot see. It cost exactly **one** model
change beyond the sink: `TCSR.SR` was not momentary (`FR` self-cleared, `SR` did not),
so the firmware's init handshake never settled. The oracle's model clears both on write
*and* on read.

### ✅✅ M-E COMPLETE, 2026-09-20 — 5/5, all six SAI operating points byte-exact

| test | result |
|---|---|
| `sai` | **PASS** ×2 — 25 000 / 12 500 Hz |
| `sai-dma` | **PASS** ×2 — mem → eDMA → SAI FIFO → sink |
| `sai-audiopll` | **PASS** ×2 — 48 000 / 24 000 Hz off the **Audio PLL** (24.576 MHz MCLK) |
| `asrc` | **PASS** — unity DC gain + Nyquist rejection |
| `asrc-async` | **PASS** — 1:2 driven purely by the SAI2/SAI1 clock-source ratio |

`sai-audiopll` passing at two rates is the clock-tree fidelity row this roadmap predicted
would bite, and it validates the CCM's Audio-PLL derivation
(`24e6*(32+768/1000)/2 = 393.216 MHz`, SAI1 root ÷16) end-to-end against sample values.

**What it actually cost:** the WAV sink, `TCSR.SR` made momentary, ASRC `ATSA` pair
re-init, the ASRC true-async source factor, `TxBitClockHz()`, and SAI2/3/4 added with
**per-instance `PARAM`** (the oracle's own note: giving all four the same value made
"every instance wrong"). Two of the five failures were not model bugs at all — one was
the harness never arming the test, one was my checker hardcoding `NSAMPLES`.

**One of Kyle's four holobench observables is now closed.**

---

## M-F — Ethernet (7 tests)

`netc-fdb`, `netc-fwd`, `netc-rxfwd`, `netc-portfwd`, `netc-flood`, `netc-ptp`,
`netc-lab3`. The NETC switch model exists and already produces byte-identical
consoles on the SDK example. The new work is **frame I/O**: the oracle's tests use
a QEMU netdev backend, so this needs Renode's network backend wired so real frames
cross the boundary — which is also what holobench will observe.

`netc-ptp` is the one to expect trouble from: PTP is a *timestamp* test, and
timestamps are where two time models diverge first.

### Scoped properly, 2026-09-19 — the split is 3 and 4, not 7

Same audit as M-E, same result: **three of the seven ship their own harness** and
were never actually run by my sweep.

| test | harness it ships | what my sweep recorded |
|---|---|---|
| `netc-flood` | `flood.py` + `run.sh` | `NO-OUTPUT` |
| `netc-portfwd` | `portfwd.py` + `run.sh` | `NO-OUTPUT` |
| `netc-lab3` | `wire-check.py` + a pinned `.elf.pin` | `NO-OUTPUT` |
| `netc-fdb`, `netc-fwd`, `netc-rxfwd`, `netc-ptp` | none — in-guest verdict | `FAIL` (real) |

The three harnessed rows are now `NEEDS-OWN-HARNESS`, not failures. They need a
**frame backend** before any verdict about them means anything, and their verdicts —
like the SAI ones — are rendered outside the guest, against frames on the wire.

> The pattern across M-E and M-F is worth stating once: **every test in this corpus
> whose subject is a stream rather than a register ships an external checker.** That
> is not incidental. A register handshake can be asserted from inside; a stream
> cannot, because the guest cannot hear itself. When I meet a test with a `.py` beside
> it, the `.py` is the test.

### The actual work, measured 2026-09-20

`IMXRT1180_NETC.cs` (741 lines) declares:

```csharp
public class IMXRT1180_NETC : IDoubleWordPeripheral, IKnownSize
```

**It does not implement `IMACInterface`.** There is no `ReceiveFrame`, no `FrameReady`,
no MAC — the block is register-level only, which is exactly why the SDK console row
passes and no frame has ever crossed it. **This is the SAI defect again, one block
over:** correct at the register level, moves no data, and invisible to any test whose
verdict is a console string.

So M-F is not "wire up a backend". It is:

1. implement `IMACInterface` on the NETC (frame in/out + the BD rings the driver walks);
2. give the switch its three wire ports — the oracle drives `netc-flood` with three
   point-to-point UDP sockets (`-nic socket,udp=…` + two `-netdev socket,udp=…`) and
   `flood.py` injects on one port and verifies the broadcast reaches the other two;
3. render the verdict **outside the guest**, against frames on the wire.

### The last three NETC rows are the regression guard, not just the last count

@rt1180emulator's framing, and it changes why they matter. `netc-flood`, `netc-portfwd`
and `netc-lab3` are **payload-consuming** tests: their oracle receives the frame on the
egress port's socket and byte-compares it. So they are precisely the tests that would
catch a regression of the `EmitToWire`-never-called bug I just fixed — the one that
**passed `netc-fwd`**, because a counter cannot distinguish "forwarded" from "accounted
for".

They audited their own model against that class and showed the receipts rather than
asserting: their emit is `qemu_send_packet(...)` **then** the counter bump (send-then-count,
not count-then-drop), and their corpus carries both observables deliberately —
`netc-fwd` tests the forwarding *decision*, `netc-portfwd`/`flood`/`rxfwd` consume the
*payload*. My bug ported into their model would pass their `netc-fwd` and fail their
`netc-portfwd`.

**What I already have:** `netc-rxfwd` consumes the payload, so the TX emit is guarded for
the CPU-delivery path today. What is *not* guarded is egress to a third party on another
wire port — which is exactly what flood/portfwd cover. So they get built as
payload-consumers, never as counter-checkers.

Anchors received for when they start: split-horizon flood (all member ports **except**
ingress), point-to-point sockets rather than shared multicast (the mechanical form of the
EchoWire scoping), and a sentinel barrier so "the frame arrived" is distinguishable from
"the run ended".

**Two of Kyle's four holobench observables live here and in M-E** (ethernet, audio),
so these two milestones are the acceptance test's critical path, not the tail. M-E is
now **complete (5/5)**; M-F has one row closed of seven, and that is the honest gap.

### ✅ `netc-ptp` CLOSED, 2026-09-20 — and it was never a network problem

*"clock did not advance when enabled"* is a **clock** bug in a block whose name says
Ethernet. The IEEE-1588 timer (TMR0 at NETC + `0xB80000`) was simply unmodelled, so a row
I had shelved behind "M-F needs a frame backend" needed **no frame I/O at all**. Modelled
as a DDS derived from virtual time — `count = base + elapsed_ns × (addend / nominal)` —
because the test's second assertion is that *doubling the addend doubles the advance*, and
a callback-driven counter advances at the callback's rate, not the addend's.

### Anchors held for the wire block (NETC flood/portfwd/lab3 + `dmareq`)

`imxrt1180-dmareq` belongs here, not with the UART rows: it is **payload-consuming with
an echo peer** — the same shape as `netc-portfwd`. Bytes leave memory by DMA, cross a
socket, are echoed, and land back in memory by DMA, then are compared byte-exact.

- **LPUART2 @ `0x44390000`**, bound to a socket chardev with an echo peer.
- eDMA3 request sources **TX = 18, RX = 19** (table-backed from `PERI_DMA4.h`:
  `kDma3RequestMuxLPUART1Tx = 16`/`Rx = 17`, so LPUART2 is 18/19) — *not* the earlier
  guess.
- Level-held, same wiring shape as LPI2C: TX request is TDRE-driven, RX is RDRF-driven,
  gated by `BAUD[TDMAE]`/`[RDMAE]`.
- ⚠️ **The RX request DROPS when the DMA read empties the holding register** (RDRF
  clears). That self-lowering level is what prevents *"copying the same stale byte CITER
  times"* — the trap the re-arm has to respect on the UART. My LPI2C re-arm already has
  the right shape for it, because the data-register access **is** the handshake.

### ⚠️ FORWARD ANCHOR for the wire ports — logged before it costs a day

From @rt1180emulator, 2026-09-20, so it is not rediscovered the hard way:
**QEMU hardcodes `IP_MULTICAST_LOOP=1`** (`net/socket.c`). A node therefore sees its
own multicast echoed back.

- That is a **feature** for `netc-rxfwd`: its oracle *needs* the self-echo, and gets it
  free from `-nic socket,mcast=…`.
- It is a **trap** for `netc-flood` / `netc-portfwd` on a switched fabric: a switch
  flooding onto a shared multicast group **re-ingests its own flood and storms**. That
  is a genuine loop, but not the topology under test — it would look like a forwarding
  defect and be a backend artefact.

So the backend choice is per-test, not global: **multicast for `rxfwd`** and the
shared-hub lab; **point-to-point sockets** (`socket,udp=…/localaddr=…`) for the switched
`flood`/`portfwd`/`lab3` tests, where the switch must not hear its own egress. Their
tests link each wire point-to-point for exactly this reason.

**Lesson for the rest of this milestone, and it generalises:** I grouped seven rows by
the peripheral's name and inherited one blocker for all of them.

> ⭐⭐⭐ **A BLOCKER INHERITED BY A GROUP IS A HYPOTHESIS ABOUT EVERY MEMBER, AND YOU OWE
> EACH MEMBER THE ONE-LINE READ BEFORE YOU WRITE THE GROUP OFF.** Third instance tonight:
> `fslaudit` guessed into META by its name, `netc-ptp` shelved behind a frame backend it
> never needed, and all seven NETC rows sharing one blocker that applied to four.

Scope by what each test *does*. The remaining
six split as: `netc-fdb` / `netc-fwd` / `netc-rxfwd` need frame movement (`netc-fdb` also
needs learning from an injected frame), and `netc-flood` / `netc-portfwd` / `netc-lab3`
need frame movement **plus** their own external wire harnesses.

---

## M-G — Determinism, and the differential harness

Equivalency is not a property of one run.

1. **Determinism on each tool separately** — the same image twice must produce the
   same bytes. The oracle ships `tools/determinism-check.sh` and reports
   `imxrt1180-motor` printing `di=8340 pos=256` identically every run; Renode needs
   the same check, and its deterministic-quantum time model should make it
   achievable.
2. **One harness that runs both tools over every artifact** — console corpus, Zephyr
   corpus and value tests in a single differential pass, with today's hard-won
   guards intact: content-based completion checking, per-tool wall budgets, and a
   short run scored `truncated` rather than as a verdict.

**Gate:** three consecutive identical runs per tool per artifact; the differential
harness green end-to-end.

> ⚠ **The harness is the instrument the mission is judged on, and it has been the
> single largest source of false findings in this project** — six silent failures in
> one afternoon, two wall-clock budgets that penalised the slower tool, and an audit
> that could not see truncation manufacturing agreement. Budget real time here.

---

## M-H — The swap test

The actual acceptance criterion: holobench boots both, driving and observing all
four surfaces, and cannot tell them apart.

1. Define the observable set concretely — console bytes, run verdict, inter-core
   message sequence, Ethernet frames out, audio samples out.
2. Run holobench against QEMU, then against Renode, capturing that set.
3. Diff. Classify every difference as **fidelity** / **nondeterminism** /
   **harness**, as the delta study already does.

**Gate:** no *fidelity*-class difference on any observable. Nondeterminism
(scheduler interleave, timestamps, stack canaries) is expected and named — it
already accounts for 25 of 83 rows in the Zephyr study, and two truncated captures
agreeing perfectly taught us that "identical" is not automatically the goal.

---

## The honest risks

| risk | why | mitigation |
|---|---|---|
| **PTP / timestamps** (`netc-ptp`) | two different time models; the place they diverge first | treat as its own investigation, not a row |
| **Time model under load** | Renode is deterministic-quantum, QEMU is `-icount`; today a cm7 second cost ~3 min of host time | measure host cost per virtual second early; it bounds what holobench can do at all |
| **Defect #13 is a real core change** | frame-size precomputation + derived-fault routing in a CPU core I don't own | M0 positive control + ABI verification, exactly as #11/#12 were done |
| **The remaining 50 may hide a structural gap** | 13/13 on the group I chose is a biased sample — I picked the group I had just built | which is precisely why **M-B measures before planning** |

---

## What this roadmap deliberately does not claim

No dates, and every estimate above is labelled **DERIVED** — none of them is
measured, because none of that work has been run. The one honest schedule statement
is that **M-B converts the rest of this document from estimate to measurement**, and
until it runs, the sizes of M-C through M-F are guesses dressed as a table.
