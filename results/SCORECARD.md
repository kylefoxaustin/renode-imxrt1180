# RT1180 EQUIVALENCY SCORECARD — Renode vs QEMU

> **Mission (Kyle):** *"I want to do the exact same things (run the same software)
> on a Renode implementation of RT1180 as we can on a QEMU implementation of
> RT1180."*
>
> So the target is not a rung ladder I picked. It is **the QEMU model's own
> corpus** — `rt1180emulator/docs/validation/corpus.tsv`, 29 rows of real NXP
> MCUXpresso SDK firmware, each judged by an EXTERNAL oracle (a string the vendor
> firmware itself prints, cited to SDK `file:line`).

## ⭐ Method: both columns are MEASURED, neither is quoted

Every earlier comparison in this project set my measured Renode result against a
QEMU verdict **copied** from the oracle's `SCORECARD.md`. Under fleet Law 1 that
is a SOURCED-vs-MEASURED comparison and may not carry a headline.

`scripts/equivalency.sh` therefore **invokes `qemu-system-arm` itself**:

- same **box** (this machine, one run),
- same **bytes** — the identical ELF/`.bin` is handed to both emulators in the same
  run, from `~/.cache/rt1180-artifacts/` (hashes in `MANIFEST.sha256`),
- same **oracle string**, matched by the same `grep -F`,
- same **flags policy** — `-icount shift=2` for the multicore rows, exactly where
  the QEMU manifest requires it.

Raw: [`equivalency.tsv`](equivalency.tsv) · corpus:
[`equivalency-corpus.tsv`](equivalency-corpus.tsv) · both consoles for every row:
[`console-eq/`](console-eq/).

## The score

| | baseline core | **patched core** *(installed)* |
|---|---:|---:|
| rows in the QEMU corpus | 31 | **31** |
| **Renode agrees with QEMU** | 30 | **31** |
| Renode differs | 1 | **0** |
| agreement | 97 % | **100 %** |

> **The last row closed 2026-09-19.** `cm7 pmsm_enc` had stood at
> `VALUE-PROVEN | NOT-ATTEMPTED` behind the note *"there is no cm7 SDK-grade
> platform or value harness"*. Both now exist, and **all 13 of the oracle's
> motor/ADC value tests pass on Renode using their unmodified binaries** —
> including `adc-fifo-align`, one of the two tests the QEMU side pins that row on.
> The other pin, `cm7boot`, cannot run here for a **harness-capability** reason
> @rt1180emulator confirmed (their machine boots the M7 directly from a cm7 ELF
> with no guest release sequence in the image — a convenience, not silicon), **not**
> a model gap. Scored `VALUE-PROVEN` with that pin gap stated in the corpus row
> rather than hidden.
>
> ⚠ **WHICH BINARY:** the 100 % column is measured on
> `translate-arm-m-le.so.d11d12d13-verified` — stock Renode 1.17.0 + the PPB patch +
> defects #11, #12, #13 — which is now the **installed default**, because holobench
> runs on this tree. The 97 % column is the stock+PPB baseline. Both libraries are
> kept side by side with their hashes; see `results/PATCHED-CORE.md`.
>
> ⚠ **AND THE FIRST RE-RUN OF THIS TABLE WAS CONTAMINATED.** Two `equivalency.sh`
> instances ran concurrently (my own launch attempts interleaved) and produced 60
> rows for a 31-row corpus, each truncating the other's output. The numbers above
> are from a clean single pass. A 60-row result for a 31-row corpus is obvious;
> a *subtly* doubled one would not have been.

> The corpus GREW mid-pass: @rt1180emulator added two MECC rows. I added them to
> my corpus **before** doing the work to close them, and bumped the coverage gate
> from 29 to 31. A corpus that only ever grows with rows you already pass is the
> "green because checking less" failure inverted.

> Was 21/29 (72 %) when this pass started:
> 72 % → 83 % (USB) → 86 % (SAI) → 90 % (ASRC) → 93 % (FlexSPI) → **97 % (NETC)**.
> The gap-map table below was written at 21/29; the rows it lists as blocked have
> been closed as the sections after it describe.

### Agreement by verdict class

| QEMU | Renode | rows | meaning |
|---|---|---:|---|
| PASS | **PASS** | 19 | ran the vendor firmware to its own documented success string |
| XFAIL | **XFAIL** | 3 | both fail, for the same documented reason |
| BANNER | **BANNER** | 5 | both reach the app; the example then needs external hardware |
| XBUILD | **XBUILD** | 2 | the SDK will not build it for this board — reproduced independently |
| RAN | RAN | 1 | `led_blinky`, no console oracle in either tool |

| VALUE-PROVEN | NOT-ATTEMPTED | 1 | cm7 FOC row; no Renode value-test written |

**ALL 17 PASS rows in the QEMU corpus now also pass on Renode, on the identical
binaries.** The single remaining difference is the cm7 FOC row, which has no
console oracle in either tool. Nothing in that sentence is quoted — it is one harness, one
run, both tools.

## The differences, as they stood when this gap map was written (5 rows)

> ⏱ **This table is a snapshot, not the final state.** It was written at 24/29 and
> the sections after it close the SAI, ASRC and FlexSPI rows one at a time. The
> final tally is at the end of this document.


This matters: Renode does not disagree with QEMU anywhere. It has **no row that
produces a different answer**; it has rows where a block simply is not modelled
yet and the firmware stops. Bases below are SOURCED from the MIMXRT1189 CMSIS
header; the QEMU line counts are MEASURED (`wc -l`) and are the best available
proxy for what porting each block costs.

| rows blocked | block missing in Renode | base | QEMU reference model |
|---:|---|---|---:|
| 1 | **FlexSPI** — mine is a deliberate M0-scope stub that refuses to fabricate |  `0x425E0000` | `hw/ssi/imxrt1180_flexspi.c` — 790 lines |
| 2 | **SAI1** — gates BOTH audio rows (see below) | `0x443B0000` | `hw/misc/imxrt1180_sai.c` — 730 lines |
| ⌙ | ASRC, needed for the second of those two | `0x429A0000` | `hw/audio/imxrt1180_asrc.c` — 396 lines |
| 1 | **NETC** switch + ENETC + PHY | `0x60000000` | `hw/net/imxrt1180_netc.c` — 1605 lines |
| 1 | cm7 FOC row — needs a Renode **value-test**, not a peripheral | — | pinned in QEMU by two value-tests |

Benign in every failing row (warnings, never the blocker): RTWDOG1–5, IOMUXC /
IOMUXC_AON, RGPIO4/6.

**Closing the remaining 5 is ≈ 3700 lines of C to port** (NETC alone is 1605 of
them). That is the honest number for "full equivalency", and it is a *known,
bounded, reference-implementation-in-hand* number — not research.

## Closing the USB gap — what the ratio actually cost

Three rows for a 296-line reference model was the best ratio in the gap map, so it
went first. What it took:

| | |
|---|---|
| `IMXRT1180_USBPHY.cs` | 136 lines C# (from 196 lines C) |
| `IMXRT1180_USB.cs` | 181 lines C# (from 296 lines C) |
| `.repl` | 4 lines, 4 instances |
| **rows closed** | **3** |

**The entire blocker for all three rows was one register.** MEASURED: 9555 reads
of `0x42CA00A0` (USBPHY1 `PLL_SIC`) in a single 5 s run, console silent, nothing
else polling — the SDK's `while(0 == (PLL_SIC & PLL_LOCK)) {}`. Adding the PHY
alone advanced the firmware from *silent* to *"USB device mouse failed"*, i.e. it
reached the application; the controller model then took it to the banner.

Two things were worth carrying over rather than simplifying away, and both are
the oracle's hard-won lessons, not mine:

1. **The PHY is born held in reset.** `CTRL` resets to `0xC000_0000` — SFTRST
   *and* CLKGATE both set — and `PWD` to `0x001E1C00` with the transmitters,
   receivers and bandgap powered *down*. A zero-filled model is not a simpler
   model, it is a model of a board that has already booted; and since firmware
   read-modify-writes `PWD`, a zeroed one makes the guest read "everything is
   already powered up" and write that back as its own configuration.
2. **Two registers describing one resource must not disagree.** The endpoint
   count appears in both `DCCPARAMS[DEN]` and `HWDEVICE[DEVEP]`. Both are derived
   from one constant and the constructor throws if `HWDEVICE != 0x11` (the RM's
   reset value), so a future edit that made them contradict each other fails
   loudly instead of shipping a chip that disagrees with itself.

And the fidelity limit is declared, not smuggled: **no USB host is attached and
none is fabricated.** `PORTSC1.CCS` is clear, so a driver polling for a connect is
correctly observing an empty port rather than waiting for an impossible event. The
device completes init/run and stays un-enumerated — which is exactly what QEMU
does, and why both tools stop at BANNER rather than PASS.

## What the equivalency pass cost, and what it found

Going from 6 corpus rows to 29 took one session and produced:

- **2 new Renode defects**, both in code shipped by Antmicro, both hit by stock
  vendor firmware:
  - **#5 — `DMA.NXP_eDMA` aborts the emulator** (`rc=134`) on
    `driver_examples/edma4/channel_link`. The major-loop epilogue (BITER→CITER
    reload, SLAST/DLAST adjustments) is never written back on the non-scatter-gather
    path, so CITER underflows 0 → 0x3FFF, the channel performs ~16 000 unintended
    DMA writes, and the 9-bit field write finally throws unhandled. **Confirmed
    against the stock unpatched model**, none of my code involved.
  - **#6 — `LPI2C` MRDR returns data flagged `RXEMPTY`** (`0x4086` = the byte
    `0x86` *plus* the empty bit), because bit 14 is evaluated after the data field
    already dequeued. `fsl_lpi2c` discards it: 257 751 MRDR reads in 0.05 s of
    emulated time, console silent. Every single-byte I2C register probe is
    impossible.
- **1 reuse win:** Renode's `S32K3XX_LowPowerInterIntegratedCircuit` *is* NXP
  LPI2C. Register map verified offset-by-offset against the oracle's
  `hw/i2c/imxrt1180_lpi2c.c` before use — identical. Six instances, six `.repl`
  lines, zero C#.
- **1 port:** `FXLS8974` accelerometer, 132 lines of C# from the oracle's
  145-line C, carrying its honesty flag (fixed "flat at rest" orientation, no
  motion input — flagged, not faked).
- **1 correction owed to the oracle:** their `bubble_peripheral` row says
  *"(sensor not modeled)"*, but `imxrt1180_soc.c:736` creates a real `FXLS8974`
  at 0x19 on LPI2C2. The note understates their own coverage.
- **1 harness error of mine, caught before publication:** I loaded every Tier A
  `.bin` at `0x0FFE0000`; the three `usb_device_dfu` images link at `0x0FFF0000`.
  The QEMU loader derives the base from each image's own reset vector; mine now
  does too. Three rows I had already written down as Renode failures were my bug.

## Second byte-identical two-tool agreement

`demo_apps/bubble_peripheral`, stock prebuilt vendor `.bin`, both emulators:

```
Welcome to the BUBBLE example

You will see the LED brightness change when the angle of the board changes
x=  0 y =  0
x=  0 y =  0
```

## Regenerate

```sh
SECS=20 ./scripts/equivalency.sh
```

## Probing the audio rows — what the line counts did NOT tell me

Before porting 900 lines of SAI I checked what the two audio rows actually stop
on, the way the USB probe paid off. The line counts were misleading in both
directions:

**`sai/edma_transfer` was not blocked on SAI at all.** It does not spin on a
missing register — it prints `SAI EDMA example started!` and then dies on

```
ASSERT ERROR " false ": sai_edma_transfer.c Line "139" function name "main"
```

which is `if (CODEC_Init(...) != kStatus_Success) assert(false);`. The blocker was
the **WM8962 codec on I2C**, not the SAI. Ported it (`peripherals/WM8962.cs`, 136
lines from the oracle's 170) — control plane only, and deliberately so: on this
EVK the codec is the I2S *slave*, so it never sees the sample stream, and
modelling it as an audio data-path device would be the fabrication. The assert
cleared immediately.

**Then it exposed a missing DMA controller.** With the codec answering, the
example drove `0x44010038` / `0x4401003C` — the **eDMA3** channel region. This
platform had no eDMA3 at all; I had built the whole thing around eDMA4 because
that is the only instance the corpus had ever touched. Config SOURCED from the
oracle's `edma_cfg[]`:

| instance | base | channels | stride | IRQs |
|---|---|---:|---|---|
| eDMA3 (AON) | `0x44000000` | 32 | `0x10000` | per-channel, 95+ch |
| eDMA4 (WAKEUP) | `0x42000000` | 64 | `0x8000` | grouped, 128 + (ch%32)/2 |

Note the consequence for defect #3: **eDMA3 has exactly 32 channels, so Renode's
32-channel hard cap costs nothing there.** The cap is only a deviation on eDMA4.

**And `asrc_m2m_polling` needs SAI too**, despite being the "memory-to-memory"
example — it waits on `while (isSAIFinished != true)` twice, playing the raw and
the converted buffers back through SAI.

So the remaining audio work is **SAI1 (730 lines) gating two rows**, with ASRC
(396) needed only for the second. That is the best remaining ratio — better than
FlexSPI (790 lines, 1 row) and far better than NETC (1605 lines, 1 row). **NETC is
43 % of the remaining work for a single row: the last thing to do, not the next.**

## SAI is not 730 lines — it is 730 lines plus a real clock tree

Scoped the SAI port to the register level before writing it. Everything needed is
known and sourced:

| piece | value | source |
|---|---|---|
| register map | VERID 0x00, PARAM 0x04, TCSR 0x08, TCR1–5 0x0C–0x1C, TDR 0x20, TFR 0x40, TMR 0x60, RCSR 0x88…, MCR 0x100 | oracle `imxrt1180_sai.c` |
| TCSR bits | TE 31, FRDE 0, FWDE 1, FRF 16, FWF 17, FEF 18, SR 24, FR 25, BCE 28 | same |
| **SAI1 TX DMA request** | **SRC 21 on eDMA3** | oracle `imxrt1180_soc.c:917` |
| Renode-side request path | `edma3.TryGetChannelBySlot(21, out ch)` then `OnGPIO(ch, true)` — both public on the model | verified in source |

**Then it hit a dependency the line count does not show.** The sample rate must be
computed from the guest's own registers:

```
BCLK = MCLK / (2 * (TCR2[DIV] + 1))
bits/frame = (TCR4[FRSZ] + 1) words * (TCR5[W0W] + 1) bits
fs = BCLK / bits-per-frame
```

and `MCLK` comes from the **CCM clock root** (`CLKROOT_SAI1 = 65`), which in the
QEMU model is a real clock tree computing PLL frequencies the way `fsl_clock.c`'s
`CLOCK_GetPllFreq()` does. **My CCM is a 196-line register-file stub with no
frequency computation at all.** It has been sufficient for every rung so far
because nothing before now needed a *number* out of it — only acknowledgement.

I am not shipping a hardcoded MCLK for this. The oracle's own note on that exact
line is the reason:

> ⭐ *"A `?:` IS NOT A SAFETY NET — IT IS A PLACE FOR A BUG TO LIVE WHERE NO TEST
> WILL LOOK."* Six timer blocks in their tree opened with `if (!clk) clk =
> DEFAULT;` and **not one of the six defaults was right.**

A plausible 24.576 MHz would make the SAI row pass while being a fabricated number
in the one register the whole block's timing derives from — a silent-wrong of
exactly the class this project exists to avoid. So the honest scope for the two
audio rows is:

| | lines |
|---|---:|
| SAI TX path | ~730 (reference) |
| ASRC | ~396 (reference) |
| **CCM clock tree — newly discovered dependency** | **not yet estimated** |

**This is the authoring-effort finding, not a setback.** Three of the five
remaining rows turned out to cost less than their line counts (USB: one register;
SAI: a codec + a missing DMA controller) and this one costs *more*. Line counts of
a reference implementation are a decent first-order proxy and a poor second-order
one — what actually governs is which **dependencies** a block reaches back into,
and that is only visible by running the firmware.

### The CCM clock tree is now real — and cross-validated against an external oracle

The "not yet estimated" entry above is closed for the audio path. Rather than
hardcode MCLK, I ported the oracle's clock-tree computation
(`ccm_root_hz` / `ccm_src_hz` / `ccm_audio_pll_hz` / `ccm_pfd_hz`) for the roots
this platform needs, reading the **guest's own** ANADIG registers:

```
AUDIO PLL = XTAL * (DIV_SELECT + NUMER/DENOM) >> POST_DIV
CLOCK_ROOT[65] (SAI1) = src[mux] / (CONTROL[DIV] + 1)
   mux = { OSC_RC_24M, OSC_RC_400M, AUDIO_PLL, SYS_PLL3_PFD2 }
```

**The external oracle.** The SDK's own board init for this example
(`_boards/evkmimxrt1180/driver_examples/sai/edma_transfer/cm33/hardware_init.c`)
programs `loopDivider=32, numerator=768, denominator=1000, postDivider=1` and
`CLOCK_SetRootClockDiv(kCLOCK_Root_Sai1, 16)`. So the answer is predictable from
NXP's source **before** running anything:

```
24 MHz * (32 + 768/1000) = 786,432,000  >> 1  = 393,216,000 Hz   (AUDIO PLL)
393,216,000 / 16                              =  24,576,000 Hz   (SAI1 root)
```

MEASURED, from the model, on the stock firmware:

```
PROBE-AFTER root65 CONTROL=0x20B -> SAI1 root = 32768000 Hz (audioPll=393216000 Hz)
PROBE-AFTER root65 CONTROL=0x20F -> SAI1 root = 24576000 Hz (audioPll=393216000 Hz)
```

Both numbers agree with the prediction, and the **intermediate 32.768 MHz** — the
firmware's earlier `div=12` write, before it settles on `div=16` — is the proof
that the tree is *live*: it tracks the guest's writes instead of returning a
constant that happens to be right.

⚠ **A probe-ordering correction, caught before it was published.** My first probe
logged at the *entry* of `WriteDoubleWord`, so it computed the root from the
*previous* register value and reported 32.768 MHz as the final answer. Re-probed
after the write lands: 24.576 MHz. Same class as the earlier `ReadDoubleWord`-on-a
`WordRegisterCollection` artifact — **the instrument's position in the sequence is
part of the measurement.**

And the detail worth keeping: **24.576 MHz is exactly the plausible constant I
would have hardcoded.** It would have been right for this example and wrong for
every other clock configuration — which is precisely why the oracle's rule is
about *provenance*, not about whether the number looks correct.

## SAI built — the row passes, and it is validated three independent ways

`peripherals/IMXRT1180_SAI.cs` (328 lines), TX path only. `driver_examples/sai/edma_transfer`
now reaches its own success string, and the consoles are **byte-identical**:

```
        RENODE                                QEMU
MCUX SDK version: 2026.06.00          MCUX SDK version: 2026.06.00
SAI EDMA example started!             SAI EDMA example started!
 SAI EDMA example finished!            SAI EDMA example finished!
```

**Three independent oracles agree, and none was fed to the model:**

| what | predicted, from NXP source | MEASURED, from the model |
|---|---|---|
| AUDIO PLL | `24e6*(32+768/1000)>>1` = 393,216,000 Hz | 393,216,000 Hz |
| SAI1 clock root | `/16` = 24,576,000 Hz | 24,576,000 Hz |
| sample rate | `app.h`: `kSAI_SampleRate48KHz` | **fs = 48,000 Hz** |

The last one is the strongest: `fs` is computed inside the model from the guest's
own `TCR2[DIV]`, `TCR4[FRSZ]`, `TCR5[W0W]` and the CCM root — four independently
programmed registers — and it lands exactly on the rate the firmware's own header
declares. A model with a hardcoded clock anywhere in that chain could not produce
that agreement by accident.

### Two of the oracle's corrections carried over, not their first readings

1. **`PARAM` is per instance, and the FIFO depth comes out of it.** Their model
   once shipped one hand-written `0x00050302` for all four SAIs: `PARAM[11:8]` is
   log2(depth), so the constant said **8**, its comment said **32**, and the
   silicon says **16** for SAI1. Three numbers, no two equal. This model takes the
   RM's per-instance values (SAI1 `0x00050402` → 16-word FIFO) as a `.repl`
   parameter.
2. **`FRF`/`FWF` are not what they look like.** Their original reading had FRF =
   "has room at all"; the corrected meaning (from `fsl_sai.h`'s own enum comments,
   cross-checked against the `sai_edma` driver's depth-minus-watermark burst) is
   **FRF = drained to/below the watermark**, FWF = empty. A DMA line keyed on the
   naive reading fires one slot short of full and overruns. I ported the
   correction, not the original.

### The declared limit

There is no audio sink: drained words are discarded. This models FIFO occupancy
and the request handshake the driver observes, and makes **no claim about analog
output**. QEMU's SAI does render to a wav backend — that is a capability Renode's
model does not have and does not pretend to.

## Remaining gap *at that point*: 4 rows

| rows | block | reference |
|---:|---|---:|
| 1 | **ASRC** — still spins on `ASRCFG` init-done at `0x429A0010` | 396 |
| 1 | FlexSPI (mine is a deliberate stub that refuses to fabricate) | 790 |
| 1 | NETC switch + ENETC + PHY | 1605 |
| 1 | cm7 `pmsm_enc` — needs a value-test, not a peripheral | — |

## ASRC built — 26/29, and the SAI model gained the half it was missing

`peripherals/IMXRT1180_ASRC.cs` (300 lines, from the oracle's 396).
`driver_examples/asrc/asrc_m2m_polling` now runs end to end, consoles matching:

```
Playback converted audio data
    sample rate : 32000
    channel number: 2
    frequency: 1000HZ.

ASRC m2m polling example finished
```

### The row exposed a real hole in the SAI I had just declared finished

Clearing the `ASRCFG` init-done spin got the example to *"Playback raw audio
data"* and then it hung. Not on ASRC — on `while (isSAIFinished != true)`.

`sai/edma_transfer` drives the SAI through **eDMA** (`FRDE` → the DMA request
line). `asrc_m2m_polling` drives the same block through **interrupts**
(`SAI_TransferSendNonBlocking` + a handle callback). **My SAI computed the FIFO
flags correctly and had no interrupt line at all.** It passed its own row because
that row never needed one.

Fixed by exposing `GPIO IRQ` and asserting it on the standard TCSR pairing —
each flag at bit 16+n with its enable at bit 8+n (`FRIE`/`FRF`, `FWIE`/`FWF`,
`FEIE`/`FEF`, …) — wired to **IRQ 45** from the oracle's `sai_cfg[]`.

> **A block that passes one row is not a finished block.** Two examples drove the
> same SAI through two entirely different paths, and the first one green-lit a
> model that was half-built. This is the corpus blind-spot pattern again, one
> level down: not "which instance" but **which path through the same instance** —
> and the axis held constant was the *transfer mechanism*.

### The declared limit on the converter

The FIR is **algorithm-class-faithful, not bit-exact**. NXP does not publish the
ASRC's taps; this is a windowed-sinc (Hann) polyphase FIR normalized to unity DC
gain — the same class the oracle implements, tested against DSP first principles,
never against silicon values. **Converted sample values from this model, from
QEMU, and from real hardware will differ in the low bits.** The claim is that a
48 kHz → 32 kHz conversion yields the right number of frames, in band, with a
constant in giving a constant out — not that it reproduces NXP's filter.

Both tools agree on this row's *console*. Neither would agree with silicon on the
*samples*, and neither claims to.

## Remaining gap *at that point*: 3 rows

| rows | block | reference |
|---:|---|---:|
| 1 | FlexSPI — mine is a deliberate stub that refuses to fabricate | 790 |
| 1 | NETC switch + ENETC + PHY | 1605 |
| 1 | cm7 `pmsm_enc` — needs a value-test, not a peripheral | — |

## FlexSPI — 27/29, and it cost FOUR Renode defects in a single peripheral

The QEMU oracle told me, unprompted, that their FlexSPI was "a deliberate stub"
and an honest XFAIL, so a Renode stub would be *agreement on a limit*. **That was
wrong** (see EXPERIMENT.md) — their model passes, and this was a real gap.

Renode ships `SPI.IMXRT_FlexSPI` and `SPI.ISSI_IS25WP`. The register map matched
the RT1180 offset-by-offset *and* reset-value-by-reset-value, and `ISSI_IS25WP`'s
ManufacturerId is `0x9D` — the exact vendor ID QEMU prints on this example. So the
reuse was sound. The models then failed in four independent ways:

| # | defect | how it presented |
|---|---|---|
| **7** | An IP write stalls forever when TX data is **pre-filled before** the trigger. `TransmitProgrammingData_SDR` breaks execution to await a TFDR write, and upstream only resumes from the IPTXWE W1C callback — but `fsl_flexspi` pre-fills, then spins on `IPCMDDONE`. Deadlock. | 970 reads of `INTR`=0x60 on `WRITESTATUSREG`; console stopped at "Erase finished !" |
| **8** | `Dummy_SDR`/`Dummy_DDR` are in the opcode enum with **no case**, so every NOR fast-read sequence is abandoned — i.e. the normal XIP read path cannot execute. | `Unsupported IP command: Dummy_SDR`; XIP window read `0x00` where erased NOR must read `0xFF` |
| **9** | `IPTXFSTS` and `MCR0` are declared in the Registers enum and **never defined**. The driver sizes its FIFO writes from `IPTXFSTS.FILL`; a constant 0 makes it overfill. | 33 TFDR writes, 33 IPTXFSTS reads, then 2426 `INTR` reads with `IPCMDDONE` clear |
| **10** | `TFDR` is **position-addressed and MSB-first**. It must *push*, LSB-first: `fsl_flexspi` writes a 256-byte page by filling `TFDR[0..1]` over and over, so position-addressing keeps only the last 8 bytes, and the byte order reverses every word. | "Pushing 8 bytes … There is 248 more bytes to send left" |

Plus a capacity change that is **not** a defect claim: Renode's IP FIFOs are 128
bytes, and the driver's pre-fill loop exits on `FILL < txFifoSize` where
`txFifoSize = (0x1FC >> 2) + 1 = 128 entries`. Real silicon drains concurrently
with the running command; both emulators execute a sequence inline, so the FIFO
must absorb the whole payload. The oracle made the same call (4096 bytes), so both
tools are deliberately wrong in the same direction and neither hides it.

Consoles are now byte-identical through the full erase → program → XIP read-back
round trip. **That read-back is what proves defect #10's byte-order half**: a
reversed program would have passed every earlier step and failed the `memcmp`.

### One of my own, caught immediately

My first `MCR0` definition made `SWRESET` a stored flag *and* called `Reset()`.
The example regressed from reaching the page program all the way back to printing
only its banner. Fixed by making `SWRESET` self-clearing and dropping the
`Reset()` call. **Defining a register is not automatically safer than leaving it
undefined** — an unhandled register reads 0, which for `MCR0` happened to be
survivable, and my "improvement" was worse than the bug.

## Remaining gap *at that point*: 2 rows

| rows | block | reference |
|---:|---|---:|
| 1 | NETC switch + ENETC + PHY | 1605 |
| 1 | cm7 `pmsm_enc` — needs a value-test, not a peripheral | — |

## NETC — probed, bounded, and deliberately NOT claimed

Applied the same probe-first discipline to the last peripheral row. It paid off
differently here: the probe did **not** make the row cheap, it made the remaining
cost *measured* instead of estimated.

The row died at `PHY Init failed!` with **14001 accesses to 0x60BA1C00/04/08** —
the NETC **EMDIO** block, not the switch. So the first wall was MDIO, ~130 lines:
`peripherals/IMXRT1180_EMDIO.cs`, the management interface plus a behavioural PHY
(BMCR reset self-clears, BMSR link-up + autoneg-complete, ID1/ID2 = RTL8201
`0x001C`/`0xC816`, RTL8211F status `0x1A` reporting link/full-duplex/1G).

That advanced the console by exactly one banner:

```
        before                    after
PHY Init failed!          NETC Switch example start.
```

**And there it stops, correctly.** The oracle for this row is `Frame forwarding to
port`, and QEMU's console shows what that requires:

```
Wait for PHY link up...
MAC learning.
The frame received from port 0. Dest Address 54:27:8d:00:00:f0 Src ... Port 0 bounds to MAC ...
  ... ports 1, 2, 3 ...
Frame forwarding.
```

i.e. the full switch data path: the NTMP command-BD ring, the FDB
(`{MAC,FID}→portBitmap`) and VLAN filter tables, TX→RX BD loopback, MSI-X
completion messages, source-MAC learning, flooding and split-horizon. **That is
the 1605 lines, and none of it is here.**

> **The row stays red, and the peripheral is banner-scoped in its own source and
> in the `.repl`.** Adding 130 lines that clear the first wall is not partial
> credit — it would be very easy to let "NETC EMDIO added" read as "NETC added",
> and the honest statement is that this platform has **no NETC model**: no station
> interface, no rings, no switch, no forwarding.

What it buys is a number instead of a guess: the remaining NETC work is the switch
data path, not the management interface, and it is still the largest single item
left for a single row.

## Final state: 27/29 (93 %)

| | |
|---|---|
| PASS \| PASS | 16 |
| BANNER \| BANNER | 5 |
| XFAIL \| XFAIL | 3 |
| XBUILD \| XBUILD | 2 |
| RAN \| RAN | 1 |
| **PASS \| FAIL** | **1** (NETC switch) |
| **VALUE-PROVEN \| NOT-ATTEMPTED** | **1** (cm7 `pmsm_enc` — needs a value-test harness, not a peripheral) |

**16 of the QEMU corpus's 17 PASS rows now also pass on Renode, on the identical
binaries, with both verdicts measured by one harness in one run.**

## cm7 `pmsm_enc` — the last row, and the only one that is a different *shape*

Probed rather than estimated. Findings:

**1. The corpus path is SDK-version-dependent.** The QEMU row names
`examples/motor_control/pmsm/mc_pmsm/pmsm_enc`; in SDK 2026.06.00 it lives at
`examples/demo_apps/mc_pmsm/pmsm_enc`. My equivalency corpus had copied their path
verbatim, so the row never built and scored NOT-ATTEMPTED partly for that reason.
**It builds fine** at the real path (`pmsm_enc_cm7.elf`) — so the row was blocked
on a stale path before it was ever blocked on a model.

**2. The first wall is measured, not guessed.** Running it on the cm7 platform:
**16568 reads of `0x425E00E0`** — FlexSPI1 `STS0`. The standalone cm7 platform is
a 16-entry Zephyr-scoped description (M7-only, ITCM@0 / DTCM@0x20000000); the FOC
image is a full SDK build that runs the complete board init. So it needs an
SDK-grade cm7 platform, not just a motor peripheral or two.

**3. Then it needs the motor-control blocks, none of which exist here.** From the
example's own board files: **ADC1, ADC2, TMR1 (QuadTimer), PWM1 (eFlexPWM),
XBAR1**, plus LPUART. A field-oriented-control loop is exactly the case where
those cannot be stubbed: the loop reads current from the ADCs, derives rotor angle,
and writes PWM duty — stub any one of them and the loop runs but means nothing.

**4. And the proof is a different shape.** This row has **no console oracle in
either tool.** QEMU scores it VALUE-PROVEN via two dedicated value-tests
(`imxrt1180-cm7boot`, `adc-fifo-align`) under `-icount`. Renode has no equivalent
harness here, so closing this row means *defining what counts as proof* — e.g.
asserting the eFlexPWM duty registers evolve non-degenerately over a closed loop
— and building that harness, not porting a peripheral.

> **That makes it the real frontier of the comparison, not the biggest port.**
> Every other row in this corpus was settled by a string the firmware printed. This
> one asks whether the two tools can be made to agree about *behaviour that
> produces no output* — which is the question the whole fidelity axis eventually
> reduces to.

It stays `VALUE-PROVEN | NOT-ATTEMPTED`. Not attempted is the honest verdict: I
have neither the peripherals nor a value harness, and a row scored on a stubbed
FOC loop would be the worst kind of green.

### Started: the walls, measured one at a time

The scoping above was an estimate. It is now a measurement, taken by clearing one
wall and reading the next — the same wall-by-wall method used everywhere else in
this project, and it corrected the estimate twice.

| # | wall | how it showed up | cleared by |
|---|---|---|---|
| 1 | **FlexSPI1** `STS0` @ `0x425E00E0` | **14551 reads** in a 2 s window, no console at all | mapping `flexspi1`/`flexspi2` + the ISSI NOR — the same three objects the CM33 platform already had |
| 2 | **console on the wrong UART** | LPUART1 init writes appear (`GLOBAL/BAUD/CTRL/FIFO/WATER`) and *nothing prints* | adding `lpuart1` @ `0x44380000`, IRQ 19 |
| 3 | **ADC1** `STAT` @ `0x42600014` | **9320 reads** in a 2 s window | a full `IMXRT1180_LPADC` model (488 lines), ported from the oracle's `hw/misc/imxrt1180_adc.c` |
| 4 | **PWM1 / EQDC1 / XBAR1** | no spin any more — the run **completes** instead of hanging | mapping all three ported models onto a new SDK-only cm7 platform |
| 5 | TMR1 / CMP3 / RGPIO1 / RGPIO4 | 4–8 touches each, no polling | *open, and not obviously blocking* |

**Wall 4 cleared, and the firmware reached the motor drive:**

```
[WARNING] pwm1: SM0 running (duty 49%), but NO motor plant or encoder responds to
          the outputs. The duty is computed and observable; nothing consumes it,
          so a closed FOC loop will not spin a virtual rotor.
```

**49 % duty is the centred half-duty an FOC loop initialises to** before it starts
modulating. The stock SDK `pmsm_enc` image has gone from *"spins forever on
FlexSPI `STS0`, prints nothing"* to *"board init complete, ADC calibrated, PWM
carrier running"* — and the model that says the loop cannot close is the model
itself, at runtime, not a footnote here.

No console text, and none is expected: this is the one corpus row with **no
console success string on either tool**, which is why the oracle scores it
VALUE-PROVEN rather than by oracle string. What is missing is not output — it is
the plant, and then a definition of proof.

**Wall 2 is a finding in its own right.** Zephyr's cm7 devicetree sets
`zephyr,console = &lpuart12`; the SDK's `evkmimxrt1180` board files use **LPUART1**
on both cores. A platform built from one toolchain's board description is *silent*
under the other, on the same core, with no error — and my cm7 platform had been
built entirely from Zephyr's. **A platform that is sufficient for Zephyr is not
sufficient for a full SDK image on the same core**, and the symptom is silence,
not a fault.

### A structural separation the walls forced, and it is the better design

Walls 1–3 were cleared by adding blocks to `mimxrt1189_cm7.repl` — and that file
is **Zephyr's** cm7 board description, which 16 published delta rows were measured
against. Adding an SDK FlexSPI XIP window to it put a 16 MiB region at
`0x28000000` into the platform those rows run on, and this project has already
been bitten by exactly that: a FlexSPI window registered in the wrong platform
once **shadowed** a Zephyr image's link address and broke two rows, caught by
regression rather than by reasoning.

So the SDK blocks now live in `mimxrt1189_cm7_sdk.repl`, which inherits the
Zephyr platform with `using "mimxrt1189_cm7.repl"` and adds only what a full SDK
image additionally needs. The Zephyr cm7 platform is back to its original 16
entries, byte-for-byte.

> ⭐ **THE REGRESSION WAS PASSING — I SEPARATED THEM ANYWAY.** Three cm7 rows
> (`hello_world`, `mutex_api`, `schedule_api`) had already come back identical on
> the combined platform, so the shadowing risk was measured, not hypothetical, and
> it was zero. But "it happens not to collide today" is a property of the current
> corpus, not of the design; the next Zephyr cm7 target to be added is the one it
> would collide with. **Separating the descriptions makes the collision
> impossible instead of absent**, which is the same reasoning as
> `load_peripherals.resc` earlier in this project: fix the class, not the instance.

### The remaining block list, from CMSIS rather than from memory

Every base below was resolved out of `MIMXRT1189_cm7_COMMON.h`, not recalled:

| block | base | needed for the loop? |
|---|---|---|
| **PWM1** (eFlexPWM) | `0x42650000` | **yes** — writes the duty; note the *odd* offsets in the trace (`...002`, `...006`), so it is a **16-bit** register block |
| **TMR1** (QuadTimer) | `0x42690000` | **yes** |
| **EQDC1** (encoder) | `0x42710000` | **yes** |
| **XBAR1** | `0x42750000` | **yes** — routes the PWM edge to the ADC trigger |
| **CMP3** | `0x42DE0000` | probably (over-current) |
| IOMUXC / IOMUXC_AON | `0x42A10000` / `0x443C0000` | no — pin mux, written not polled |
| RGPIO1 / RGPIO4 | `0x47400000` / `0x43830000` | no |
| RTWDOG1–5 | `0x442D`/`0x442E`/`0x4249`/`0x424A`/`0x424B` `0000` | no — disabled at init |

**Two corrections to my own earlier scoping**, both from the trace: the encoder is
**EQDC1** *and* TMR1 is touched as well (I had listed only "TMR1 (QuadTimer)"), and
**CMP3 appears and was not on my list at all.** An estimate made from an example's
board files missed a block that running it found in two seconds.

### What is deliberately NOT claimed

The LPADC is in and the ADC wall is gone, but **no conversion has run yet** — the
trigger comes from PWM1 through XBAR1, and neither exists. The model's
"no plant drives channel N" warning has therefore never fired, which is the
correct state and is worth stating plainly: *a mapped ADC is not a measuring ADC.*
The model returns a fixed mid-scale placeholder and **says so, once, loudly**, the
first time anything reads it without a plant behind it — because a FOC loop fed on
a plausible constant runs perfectly and means nothing, and that is exactly the
green this row must not be allowed to produce.

### The motor-control blocks, ported

All four remaining peripherals are written and compile clean, each ported from the
oracle's C model rather than derived from the RM:

| model | lines | from | what it had to get right |
|---|---:|---|---|
| `IMXRT1180_LPADC.cs` | 488 | `hw/misc/imxrt1180_adc.c` | command chains, dual A/B-side FIFO routing, `VERID` **feature bits** |
| `IMXRT1180_XBAR.cs` | 147 | `hw/misc/imxrt1180_xbar.c` | 16-bit `SEL[]` holding **two** 8-bit output selects; level follow-through |
| `IMXRT1180_EQDC.cs` | 225 | `hw/misc/imxrt1180_eqdc.c` | self-clearing `CTRL.LDOK`, coherent `UPOS` snapshot into the hold registers |
| `IMXRT1180_PWM.cs` | 410 | `hw/misc/imxrt1180_pwm.c` | `INIT`/`VAL0..5` double-buffering committed on `MCTRL.LDOK` |

> ⭐⭐ **FOUR OF THESE BLOCKS HAVE A RESET VALUE WHERE ZERO IS NOT "NEUTRAL" — IT
> IS AN ACTIVE LIE, AND THREE OF THEM ARE DANGEROUS.** This is the single
> strongest argument in the project for the rule that the QEMU model is the
> reference oracle, because every one of these is invisible to "map the block and
> see if the driver stops spinning":
>
> - **eFlexPWM `DTCNT0/1` reset to `0x07FF`, not 0.** These are the **dead-time**
>   counters. Zero dead time is a **shoot-through** — a direct short across the DC
>   bus through both transistors of an inverter leg. Firmware that programs duty
>   cycles and trusts the hardware's reset dead time, *which is what a reset value
>   is for*, runs perfectly in emulation and destroys the inverter on a real board.
> - **EQDC `POSDPER`/`LASTEDGE` reset to `0xFFFF`, not 0.** `POSDPER` is the
>   number of clocks between encoder edges and a speed observer **divides by it**.
>   `0xFFFF` means "no edge seen, shaft not turning". Zero means *zero clocks
>   between edges* — a divide-by-zero or an **infinite rotor velocity before the
>   motor has moved**.
> - **LPADC `GCR[GCALR]` resets to `0x10000`, not 0.** That field is a 17-bit Q16
>   gain where bit 16 is the integer 1, so `0x10000` is **gain = 1.0**. Zero is a
>   gain of *nothing*: firmware that reads the calibration back and applies it
>   scales every conversion to zero — a phase current of zero, forever, from an
>   ADC that reports itself perfectly healthy.
> - **eFlexPWM `CTRL` resets to `0x0400` (FULL)** — full-cycle reload selected,
>   where zero claims no reload point was selected at all.
>
> Every one of these produces a model that **boots, runs, and reports success**.
> A bring-up loop driven by "does the firmware stop polling?" reaches green on all
> four. Only a second implementation that had already been burned by them carries
> the knowledge — and the oracle's notes record that its *own* reset-value gate
> missed three of them because the gate kept only 32-bit registers and these are
> all 16-bit.

The same discipline applies to what is **not** modelled, which each file states at
its own site rather than in a footnote: XBAR `CTRL[]` edge/DMA modes are stored
but not behavioural; EQDC counters do not advance without a plant; PWM duty is
computed and observable but nothing consumes it. Each says so **once, loudly, at
runtime**, the first time firmware relies on it.

### The plant, and one clock root it needed

The four peripherals are wired into `mimxrt1189_cm7_sdk.repl`, and a
**virtual-motor plant** (`IMXRT1180_Motor.cs`, ported from the oracle's 426-line
`hw/misc/imxrt1180_motor.c`) closes the loop: it reads the eFlexPWM duty,
integrates a PMSM model (Clarke/Park → torque → velocity and angle), drives the
EQDC position/speed counters and injects phase currents into the LPADC channels.

It is registered with **no bus address**, because it has none — it is a simulation
object, not a device on the SoC, and giving it a fake address would be the first
lie in the file. It announces exactly what it is:

```
Virtual PMSM plant attached: 4 pole pairs, Rs 0.54 ohm, Ld 335.6 uH, Lq 218 uH,
Kt 0.05477461 N*m/A, J 1E-05 kg*m^2, 24 V bus, 2000-line encoder (8000 cts/rev),
stepping at 50000 Hz. This is a BEHAVIOURAL model, not silicon.
```

> ⭐⭐ **THREE OF THE PLANT'S CONSTANTS ARE INVERSES OF WHAT THE DRIVER DOES, NOT
> RATIOS FROM A DATASHEET** — and each one, set "reasonably", yields a loop that
> runs and never spins, with nothing in the console to say why:
> - **ADC counts per amp = 1820, not 3475.** `mc_pmsm` decodes a phase current as
>   `I = ((raw*12/11 - offset) << 1) / 32768 * I_MAX`, so the code to *present* is
>   the inverse of that, not `0x7000/I_MAX`. The obvious span reads ~1.9× high
>   through the decode and trips the over-current fault at startup.
> - **DC-bus code = `v/60.8 * 32768 * 11/12`, not `v/60.8 * 0xFFFF`.** The driver
>   treats the result as a Q15 frac16 and applies the EVK's board compensation
>   first. The 16-bit-full-scale version is 2.18× high: it reads 24 V as ~52 V and
>   trips the 30 V over-voltage lockout, so the demo never leaves AppStop.
> - **Encoder CPR = 8000 (4 × 2000 lines), not 2000.** The driver scales every
>   position and speed gain for 4× the line count; a mismatch makes the FOC read
>   the rotor angle at the wrong rate, its dq frame diverges from the plant's, and
>   torque current lands in the d-axis.
>
> **None of these is discoverable by mapping registers and watching the firmware
> stop polling.** All three produce a system that initialises cleanly, runs its
> control loop at the right rate, and produces no motion — the exact failure a
> console-oracle corpus cannot see, which is why this row is the frontier.

**One real gap surfaced and was closed.** The plant asks the CCM for the EQDC's
QD-timer clock — `CLOCK_ROOT[Bus_Wakeup] >> EQDC.FILT[PRSC]` — at the point of use
rather than sampling it once, because the guest re-muxes the roots during boot.
The CCM answered honestly that it had never modelled root 4, so the plant drove
position but **not** speed and said so. Root 4's mux table was then taken from the
oracle's CCM (`{OSC_RC_24M, OSC_RC_400M, SYS_PLL2, SYS_PLL3_PFD1}`) rather than
inferred, and the root now resolves.

> ⚠ **AND THE HONEST WARNING WAS ITSELF A BUG.** The CCM's "this root yields no
> frequency" warning fired *per call*, and the plant queries at its 50 kHz step
> rate: **51,676 identical lines in a two-second run, 99.7 % of the log.** A
> warning that buries every other line is not more informative than one that fires
> once — it is less. Now reported once per (root, mux), re-armed on reset. Log
> volume after modelling the root: **51,676 → 0.**

### One of my own models was caught making a claim it could not know

The eFlexPWM logged, on start: *"SM0 running (duty 49%), **but NO motor plant or
encoder responds to the outputs**"*. That was true when written and became **false
the moment the plant was wired in** — and it kept printing, because the eFlexPWM
holds no reference to a plant and never could.

> ⭐ **A PERIPHERAL MAY REPORT WHAT IT OWNS; A CLAIM ABOUT WHAT IS ATTACHED TO IT
> BELONGS TO WHOEVER DOES THE ATTACHING.** This is the same shape as the defects
> this project spent the day finding — state that is internally consistent and
> externally wrong — committed by me, in a warning whose whole purpose was
> honesty. The message now reports the duty and says only that *"whether anything
> consumes it depends on what is wired to the outputs"*; the plant announces
> itself.

### Defining the proof — and an accident that made the definition better

This row has **no console oracle on either tool**, so "proof" has to be *defined*,
and the definition is as much the deliverable as the result. The first instinct was
to read the plant's own state: it exposes `RpmMechanical`, `Omega`, `Id`, `Iq`.

That turned out to be impossible, and the impossibility is the good news.

MEASURED on Renode 1.17.0: a peripheral registered with **no bus address is not
observable from the monitor**. Written with no `@` clause the plant was
constructed (it announced itself in the log) but never entered the peripheral
tree; written `@ none` — Renode's own idiom, used in `platforms/cpus/stm32f0.repl`
— it is *still* neither listed by `peripherals` nor addressable by name. Renode's
own `nvicInput7` on that same platform answers `No such command or device` too, so
this is the tool's behaviour, not a mistake in my file. (I had written a comment
claiming `@ none` fixed it, before measuring. It did not.)

> ⭐⭐ **SO THE HARNESS READS THE MACHINE THROUGH ITS REAL REGISTERS, AND THAT IS
> STRICTLY STRONGER.** Rotor angle comes from EQDC1's hold registers
> `UPOSH:LPOSH` + `REVH`, speed from `POSDH`/`POSDPERH` — **the very registers the
> FOC control loop reads**. A proof built on the plant's C# properties could pass
> while the firmware-visible path was broken: the physics right, the peripheral
> interface wrong, and a green row. A proof read through the register interface
> cannot. The back channel I wanted would have been the weaker instrument, and I
> only stopped wanting it because the tool refused to provide one.

The probe is `scripts/run_pmsm.sh`, and it reads exactly those five registers with
`sysbus ReadWord` — no side effects, since the hold registers do not snapshot.
Verified against reset first: `UPOSH:LPOSH = 0` (rotor at origin) and
**`POSDPERH = 0xFFFF`** — the "no edge seen, shaft not turning" value whose
zero-instead alternative is an infinite speed.

> ⚠ **AND THE HARNESS'S FIRST RUN WAS A SILENT FAILURE — THE SAME CLASS I SPENT
> THE DAY FIXING.** It assembled its `-e` flags into a string and `eval`-ed it; the
> monitor received `include;` with the path stripped, the platform never loaded,
> **zero samples were produced — and the script exited 0**. A harness that reports
> nothing and succeeds is indistinguishable from *a rotor that did not turn*, which
> is precisely the result this row exists to measure. Three separate faults, each
> silent: the `eval` quoting, Renode's `echo` needing its argument quoted
> (`No such emulation element: SAMPLE`), and the console emitting `CR CR LF` so
> every `$`-anchored pattern in the decoder failed and printed nothing.
> Now: CRs are stripped before decoding, and **a sample count below what was asked
> for is a hard failure with `exit 4`** that says *"this is NOT a measurement of the
> rotor"* rather than printing an empty table.

> ⚠ **AND THAT GUARD IMMEDIATELY EARNED ITS KEEP.** The next run returned
> `HARNESS BROKEN: expected 5 samples, the run produced 1` — because **Renode
> itself crashed** part-way through:
> ```
> Unhandled exception. System.Threading.SemaphoreFullException:
>   Adding the specified count to the semaphore would cause it to exceed its maximum count.
>    at System.Threading.SemaphoreSlim.Release(Int32 releaseCount)
>    at Antmicro.Renode.UI.ConsoleIOSource.RedirectedHandling()
>    at Antmicro.Renode.UI.ConsoleIOSource.HandleInput()
> ```
> That is Renode's **console-input plumbing** with stdin redirected — not the time
> framework, not the plant, not the models. Without the guard this would have
> printed one all-zero sample and stopped, and one all-zero sample is exactly what
> "the rotor never turned" looks like.
>
> **My first two explanations were both wrong, and the second one I had already
> written down as a fix.**
> - *"Too many `-e` flags with redirected stdin"* — **measured false**: 10, 30, 60
>   and 120 `-e "echo ..."` flags all complete cleanly with `</dev/null`.
> - *"Put the whole probe in the `.resc` and pass one `-e`; that avoids it
>   entirely"* — **also false.** I wrote that after a 3-sample smoke test at
>   0.05 s steps, which takes seconds. The next real run, with **exactly one**
>   `-e` flag, crashed in the same place after sample 1 of 5, ~5 minutes in.
>
> ⭐ **A WORKAROUND VALIDATED ONLY ON THE FAST CASE IS NOT A WORKAROUND.** The
> smoke test differed from the real run in the one variable that matters — elapsed
> wall time — and I let "it passed" stand for "it is fixed". That is the same
> mistake as a budget that only bites the slower side: the cheap version of the
> test cannot reach the condition that breaks it.
>
> What survives both refutations: **two crashes, same stack, both several minutes
> into a long run with stdin redirected**, and flag count ruled out. That points at
> elapsed time in `ConsoleIOSource`, not at command volume. It stays an
> **observation, not a fourteenth defect**, until it is reproduced deliberately —
> *a crash I cannot reproduce is a lead, not a verdict*, the same rule that kept
> `16384` from being called a defect for most of this project.
>
> **The harness now avoids the console entirely**: `-P -1` (no GUI, no port, no
> `ConsoleIOSource`), and the rotor state reaches the **log** through an explicit
> `eqdc1 LogState` observability method rather than through the monitor printing
> `sysbus ReadWord` results. That method reads the HOLD registers — what the
> control loop reads — and takes no snapshot, because *a probe that perturbs the
> thing it measures is not a probe*.

> ⚠ **AND THAT REWRITE ADDED TWO MORE SILENT FAILURES, BOTH CAUGHT BY THE GUARD.**
> - `logLevel 3` in the generated script is **ERROR**, not "quiet" (Renode's scale
>   is NOISY −1, DEBUG 0, INFO 1, WARNING 2, ERROR 3). It swallowed the probe's own
>   INFO lines, and the harness reported **0 samples on a run that had executed
>   perfectly**. Now: global WARNING, with the one probed peripheral lifted to INFO.
> - The generated `.resc` is an **unquoted heredoc**, so a backtick in a *comment*
>   I had just written was command-substituted by the shell — `logLevel: command
>   not found` — before Renode ever saw the line.
>
> That is **six silent failures in one instrument**, five of them mine, every one
> of which would have printed an empty table or an all-zero sample. The guard that
> refuses to call a short run a measurement caught all six. It is the single most
> valuable line in the script, and it is not the measurement.

**The harness deliberately asserts no pass condition yet.** A threshold invented
before the first measurement is a threshold fitted to whatever comes out. It
records the numbers; the pass condition gets defined against the oracle's goldens
(phase current to one ADC count, encoder speed within 0.5 %, 2000 rpm settling
~1.5 %) once there are real numbers to compare.

### ⭐⭐ The row's proof already existed — on the other tool. So I ran it here.

The mistake in my own framing was assuming the pass condition had to be *invented*.
It did not. The oracle proves this row with **hand-written bare-metal value tests**,
and those are binaries — which means the project's core method applies to the one
row a console string cannot reach: **run their bytes on my model.**

Their tests link to ITCM `0x0FFE0000` / DTCM `0x20000000` (the **CM33** map, not
the cm7 one) and report through **semihosting** (`bkpt 0xAB` / `SYS_WRITE0`), not
a UART. So: a CM33 motor platform (`mimxrt1189_cm33_motor.repl`), Renode's
`CPU.SemihostingHandler` + `UART.SemihostingUart` wired as its own platforms do,
and their **unmodified** `motor.elf` loaded straight in:

```
MOTOR: PASS - rotor aligns at EQDC 500 (electrical 90 deg, Pp=4)
MOTOR: PASS - phase current MATCHES the first-principles golden (8341)
MOTOR: PASS - DC-bus sense reads the plant's real 24V bus (not a placeholder)
```

**Three for three, first run.** And the golden comes from *neither implementation*:
their `motor.c` derives it from duties 500/554/446 per-mille → amplitude-invariant
Clarke → `v_beta` = 1.4965 V → Ohm's law at standstill → phase B = 2.400 A →
1820 counts/A → **4369 counts ±5 %**, with the rotor settling at
EQDC 500 = 8000 × 22.5/360. `Rs`, `Pp` and `I_MAX` are motor/board datasheet
facts, so the expectation is independent of the thing it checks.

> ⭐⭐ **"A RANGE IS NOT A GOLDEN."** (@rt1180emulator, `tests/imxrt1180-motor/motor.c`.)
> Their mutation audit made the plant report **3× the phase current it computed**
> and a bounded-current check still said PASS — because 3× a valid current is
> still "bounded". That single sentence is the clearest statement in this project
> of why the last corpus row is a different *shape* of problem and not merely a
> bigger one, and it is the reason a first-principles golden was worth the effort
> of deriving.

**What this independently confirms is exactly the part I was least sure of.** Three
constants in my port are inverses of what the *driver* does rather than datasheet
ratios — 1820 ADC counts/A (not `0x7000/I_MAX` = 3475), the DC-bus 11/12 board
compensation against a Q15 frac16 (not 16-bit full scale), and encoder CPR 8000
(4× the 2000 lines). Their binary hits all three from the outside. Had any been
wrong, a golden misses where a range would have shrugged.

### Scope is declared, not discovered

The first sweep ran **every** oracle test on the CM33 motor platform and duly
reported `FAIL` beside `asrc.elf` and `dma.elf`, and `NO-OUTPUT` beside the
`dualcore` and `cm7-*` binaries. That platform has no ASRC, no SAI, no eDMA and
one core.

> 📌 *Dated note, 2026-09-19: that sentence is true of the platform **as it stood
> then** and is left standing as the record of why scope had to be declared. It is
> no longer true of the platform today — `mimxrt1189_cm33_motor.repl` and the
> `m1`-derived value platform now carry `sai1`, `asrc`, `netc`, `edma3` and
> `edma4`, verified by listing the machine's peripherals at runtime rather than by
> grepping the `.repl` (peripherals arrive through `using` inheritance, so a grep
> of the local file reports absent for things that are present). The SAI/ASRC/NETC
> bare-metal rows that still fail therefore fail for real reasons, not for this
> one — I checked before blaming my harness a second time.*

> ⚠ **THOSE WOULD HAVE BEEN PUBLISHED FAILURES OF SOMEBODY ELSE'S TESTS CAUSED BY
> MY PLATFORM.** Same sin as every truncation bug found today — a harness
> limitation wearing the shape of a finding — except this time pointed at the
> other tool, which is the direction I have the least standing to get wrong. The
> in-scope set is now listed explicitly with its reason, and everything else reads
> `OUT-OF-SCOPE`: a statement about *this platform*, never a verdict on the model
> or on the test. I flagged it to @rt1180emulator before they could read a `FAIL`
> next to their `asrc.elf`.

### And the SDK image itself

Separately, the stock `pmsm_enc_cm7.elf` was run under the value harness reading
EQDC1's hold registers. Through the first samples the rotor does **not** turn
(`pos=0`, `posdperh=0xFFFF`). That is recorded as an observation and **not** as a
Renode gap: the oracle does not prove this row by observing the SDK image spin
either — they prove it with the value tests above — and the stock `mc_pmsm` demo
may well require a FreeMASTER command to leave its idle state. Calling it a gap
would need the same image observed on both tools, which has not been done.

The row's status therefore rests on the value tests, not on a self-starting spin.

## Self-audit: the manifest was covering 11 of 103 artifacts

The "same bytes" claim above has two parts, and only one of them was ever in
doubt:

* **What both tools ran** was never at risk. `scripts/equivalency.sh` resolves one
  path and hands *that file* to `qemu-system-arm` and to Renode inside the same
  loop iteration. Same-bytes is guaranteed by construction, not by a hash.
* **What the manifest pinned** was overstated. `MANIFEST.sha256` held **11**
  entries — the M0/M1/M2 artifacts from the original rungs — while the tree had
  grown to **103** ELFs. Every SDK example built during this equivalency pass
  (eDMA4 ×6, FlexSPI, SAI, ASRC, NETC, cache, ELE, cm7 FOC) was unpinned, i.e.
  13 of the 29 rows cited a manifest that did not mention their image.

Regenerated: 103/103 entries, `sha256sum -c` clean, and the 11 original hashes
still verify unchanged against the old manifest (kept as `MANIFEST.sha256.prev-11`)
— so nothing that was pinned had drifted; the pinning had simply stopped growing
with the corpus.

> **A manifest that stops covering new artifacts fails silently and in the
> flattering direction:** it keeps verifying clean, because the things it checks
> are still fine. `sha256sum -c` returning all-OK says nothing about what is *not
> listed*. The check that matters is coverage — count the files, not the passes.

Which is the same shape as the corpus blind-spot pattern one more time: the
instrument only speaks about what it was pointed at.

## ⚠ The final sweep read 22/29, and the five lost rows were a harness bug of mine

Recorded because the near-miss is the point.

After adding the NETC EMDIO model I re-ran the full sweep as a closing check. It
came back **22/29**, down from 27/29 — and **every lost row was Tier A**
(`hello_world`, `bubble_peripheral`, `sai`, and the three USB rows).

Cause: each `.resc` carried its own hand-maintained list of `i @peripherals/*.cs`
includes. I added `emdio` to `mimxrt1189_m1.repl` and the include to
`rt1180_m1.resc` — but the six Tier A rows load that **same** `m1.repl` through
`rt1180_tierA.resc`, which I did not update. The platform then fails to load, and
every row that uses it fails.

**This was the third instance of the identical mistake in one session** (LPI2C +
FXLS8974 → Robot suite; USB → Robot suite; EMDIO → Tier A). The first two were
caught by the regression suite within a minute. This one was caught only because I
ran a final sweep I did not strictly need.

So I fixed the class rather than the instance: `scripts/load_peripherals.resc` is
now the single load list, included by all eight `.resc` files and the Robot suite.
Adding a peripheral touches one file. The comment in it records the failure that
motivated it, with the measured numbers.

> **A harness that must be updated in N places will eventually be updated in N−1.**
> The failure mode is not subtle — the platform refuses to load — but it is
> *silent in the score*: a row that never ran reads as a row that failed, and a
> model regression and a harness regression look identical in the tally.

Re-verified after the fix: **27/29**, Robot 10/10.

### And one more of mine, caught in the same pass

To probe the cm7 FOC row I had pointed its corpus entry at the newly-built
`pmsm_enc_cm7.elf`. That entry has no `resc`, so the scorer would have defaulted it
to the **cm33** platform and run a cm7 image there — producing a red row that says
nothing about either tool. Reverted to `-` (NOT-ATTEMPTED) with the reason written
into the corpus: there is no cm7 SDK-grade platform and no value harness, so
"not attempted" is the truthful verdict and a manufactured failure is not.

## Coverage gates — applying the lesson back to my own harness

@rt1180emulator ran the manifest self-audit I suggested and found a live residual
gap in their own tree: their reset-value gate asserted that every golden entry was
*probed*, but never asserted the golden's own *size* — so a regression that dropped
tables would shrink the golden and every remaining entry would still pass. Green,
while checking fewer registers. They closed it with `GOLDEN_FLOOR=7193` and proved
it can fail.

**The same question turned back on my harness found the same hole, in a worse
form.** `scripts/equivalency.sh` printed `total 29` and compared that number to
nothing. A row silently dropped from the corpus would print `total 28` and go
unremarked — and because the two remaining differences are the *red* rows,
**dropping one would RAISE the agreement percentage.** A shrinking corpus would
read as improving fidelity.

Both gates now run **before** anything else, and both are proven to fail:

```
EXPECTED_ROWS=28  → GATE FAIL: corpus has 29 rows, expected 28.        exit=3
MANIFEST_FLOOR=999 → GATE FAIL: manifest pins 103 artifacts, floor 999. exit=3
default            → no gate trips, behaviour unchanged
```

### The gate had a bug of its own, and only proving-it-can-fail found it

My first version counted **30** rows instead of 29: it excluded the header with
`^kind\t`, and **`\t` is not a tab in POSIX ERE**, so the pattern matched nothing.
I also mis-read the exit status, because I had piped the script to `head` and was
reading *head's* status, not the script's — so the gate looked like it was exiting
0 when it was correctly exiting 3.

> ⭐ **A GUARD YOU HAVE NOT SEEN FAIL IS NOT A GUARD.** Both bugs were invisible
> while the gate was passing, and both would have made it either permanently
> broken or permanently silent. The "prove it can fail" step is not ceremony —
> it is the only thing that distinguishes a working gate from a decorative one.

Which is the same rule as the data-symmetry axis, one level up: *name the fault
class the check can detect, then demonstrate it detecting one.*

### ⚠ RETRACTION: my explanation of the gate's miscount was wrong

I recorded, minutes ago, that the coverage gate first counted 30 rows instead of 29
"because `\t` is not a tab in POSIX ERE". **That claim is false on this system, and
I published it before testing it.** Measured, immediately after:

```
grep -P  "a\tb"        → 1
grep -E  "a\tb"        → 1     ← GNU ERE DOES interpret it
grep -E  "a<TAB>b"     → 1
grep -cE '^kind\t' equivalency-corpus.tsv   → 1   (the header IS matched)
```

And both the "broken" and the "fixed" expressions count **29**:

```
grep -vcE '^#|^kind	|^$'  → 29
grep -vcE '^#|^kind|^$'    → 29
```

So my fix was a **no-op**, and the cause of the original 30 is **not reproducible
from the artifacts I have**. The likely candidate is an escaping level in the
Python-inside-heredoc that generated the script (`\\t` reaching the file as a
literal backslash-t rather than a tab escape), but I did not capture the file as it
stood at that moment, so **I cannot demonstrate it and will not claim it.**

What survives, because it was measured:

* the gate reported **30** then, and reports **29** now;
* the gates **do** fire correctly (`exit=3`, both, demonstrated);
* the corpus **does** have 29 rows and every row splits into exactly **7** fields;
* `zephyr_sweep.sh`'s `grep -qP "...\t..."` is correct under `-P`, and
  `awk -F'\t'` / `IFS=$'\t'` are correct — all verified, not assumed.

> ⭐ **I CAUGHT MYSELF DOING THE EXACT THING THIS PROJECT EXISTS TO CATCH**, one
> message after writing "a guard you have not seen fail is not a guard": I produced
> a tidy causal story for an observation, wrote it into the deliverable, and only
> then ran the two-line experiment that refuted it. The lesson generalises past
> tabs — **an explanation that fits the symptom is not a diagnosis until it
> predicts something you then observe.** A plausible cause published is a
> fabrication with good manners.

The one *defensible* claim from that episode stands on its own and is unaffected:
I misread the gate's exit status by piping the script to `head` and reading
`head`'s status. That one I reproduced.

#### Correction to the correction: the tool wearing the name was not the tool

Having retracted "`\t` is not a tab in ERE" as false, I then told the oracle "GNU
ERE **does** interpret it". **That is also wrong**, and checking properly produced
a better finding than either version:

```
$ grep --version | head -1
ugrep 7.8.4 x86_64-pc-linux-gnu +sse2; -P:pcre2jit; ...

ugrep     -E 'a\tb'  → 1      ← what `grep` actually is on this box
busybox grep -E 'a\tb'  → 0   ← a POSIX-ish implementation: NO match
```

So the accurate statement has three parts, and no single absolute covers it:

* As a claim about **POSIX / busybox ERE**, "`\t` is not a tab" is **true** —
  demonstrated, busybox returns 0.
* As an **explanation for my miscount**, it was **false** — the `grep` actually
  running here is `ugrep`, which does interpret it.
* As a claim about **"GNU grep"**, it was **unfounded** — I never verified that
  `grep` on this machine *was* GNU grep. It is not.

> ⭐ **I ASSERTED THE BEHAVIOUR OF A TOOL WITHOUT CHECKING WHICH TOOL IT WAS.**
> Twice: once saying POSIX, once saying GNU, neither verified, while a
> one-line `--version` sat unrun. This is the fleet's "the tool you use to control
> the experiment is a participant in it" — one level further down. The participant
> here was not *how* I invoked `grep`; it was **which program the name `grep`
> resolves to.**

Practical consequence for this harness, now known rather than assumed: the scripts
use `grep -E` and `grep -P` and would behave **differently under busybox** — the
`^kind\t` form would silently stop excluding the header there. The corpus gate is
written with `^kind` (no escape) and is therefore portable; `zephyr_sweep.sh`'s
`grep -qP` relies on PCRE and would need `grep -E` with a literal tab on a system
without `-P`.

### A near-orphan of my own, and the limit of my earlier "immune" claim

Mid-session I told @rt1180emulator my harness could not hit their orphan bug
(`timeout N cmd &` then `kill $!` kills the *wrapper*, reparenting the child to
init) because I never background a `timeout` and never `kill $!`. That was true of
the scripts — and I then reintroduced the same shape from the *outside*, by
wrapping a whole sweep in `timeout -k 10 60` to sample it quickly. The outer
timeout killed the script; the `qemu-system-arm` it had launched was a grandchild
and kept running. Census went from 0 to 1.

It cleaned itself up — the scorer gives every emulator invocation its own
`timeout -k 5 20`, and that inner timeout fired ~20 s later — so nothing needed
reaping and I confirmed that by watching the PID disappear rather than assuming it.

> **The defence that saved this was per-invocation timeouts, not the absence of the
> bad pattern.** My claim of immunity was about the *code*; the exposure came from
> how I *invoked* it. Structural immunity to a class you can re-create from one
> level up is not immunity — it is a habit, and habits have exceptions.

Two practical notes: an emulator run wrapped in an outer timeout can outlive its
script, and a census taken in that window reads dirty for a reason that is neither
a leak nor a bug. Both are why the census resolves binaries through `/proc/PID/exe`
and excludes self by PID — it told me exactly which process, whose it was, and when
it started, and that was enough to decide "wait" rather than "kill".

#### Harness portability, closed

The `grep` identity finding had one live consequence, and @rt1180emulator was
right to ask me to pin it down. Audit of every `grep`/`sed` invocation in the
harness found exactly **one** implementation-dependent site, and it had **two**
dependencies stacked in a single line:

```sh
if grep -qP "^${core}\t${sample}\t" "$TSV" 2>/dev/null; then   # was
```

* `-P` (PCRE) is a GNU/ugrep extension. **busybox grep has no `-P` at all** —
  measured: `grep: invalid option -- 'P'` — and the `2>/dev/null` **swallowed that
  error**, so the check would always answer "not done" and a resumed sweep would
  re-run every target and duplicate its rows.
* `\t` inside the regex is interpreted by ugrep, not by a POSIX ERE.

Replaced with `awk -F'\t'`, which interprets the escape by its own specification
rather than by whichever program `grep` resolves to — and which is also **more
exact**, matching whole fields instead of an anchored prefix:

```
cm33 samples/hello_world     → present
cm33 samples/synchronization → absent
cm33 samples/hello           → absent   ← prefix collision now impossible
```

Remaining `grep` use is `-F` (fixed strings), `-c`, and plain `-E` without escape
classes; the corpus gate uses `^kind` with no escape. Every match the audit still
reports in `zephyr_sweep.sh` is inside the comment documenting this change —
verified, not assumed.

> The bug here was never the regex. It was that **one line depended on two
> unstated facts about the environment**, and the error path that would have
> revealed one of them was redirected to `/dev/null`. A silenced error is an
> assumption with the evidence deleted.

#### Harness-health gate — the permanent fix for the 22/29 class

@rt1180emulator audited their own `2>/dev/null` sites against my closing lesson and
returned a sharper form of it:

> **`2>/dev/null` is safe iff what it silences is redundant with a result you
> capture AND a failure branch you own; it is a deleted assumption iff the silenced
> error is the only evidence the step is still meaningful.**

Applying that test to my five sites found four safe (explicit fallbacks, captured
exit statuses) — and pointed at something the coverage gate did **not** cover.

`rhit=0` scored **FAIL** whether the firmware ran without printing the oracle *or
the platform never loaded*. Those are different claims — one is a fidelity result
about Renode, the other is a bug in my scripts — and they were **indistinguishable
in the tally**, which is what gets published. That is precisely how this session
produced 22/29, where all six false differences made Renode look *worse* for
reasons that had nothing to do with Renode.

The scorer now checks harness health **before** taking a verdict: a capture
containing `There was an error executing command`, `Could not compile` or
`Error E<n>:` scores **`HARNESS-FAIL`**, not `FAIL`, and the sweep exits **4** with
the agreement figure marked not publishable.

**Proven by recreating the original failure** — breaking `rt1180_tierA.resc` so the
platform cannot load:

```
6 rows flagged  → exactly the six Tier A rows from the 22/29 incident
verdicts        → HARNESS-FAIL (not FAIL)
exit            → 4, "the agreement figure below is NOT publishable"
healthy path    → exit 0, agreement unchanged
```

⚠ And one honest note on the proof itself: my *first* attempt at this observed the
6 flagged rows but reported `exit=124` — my own outer `timeout` firing, not the
gate. I had not seen the exit-4 path at all. I added a `CORPUS` override, ran it on
a two-line corpus, and watched **exit=4** and then **exit=0** directly. *Watching
the wrong process's exit status is now the second time this session that same
mistake nearly certified an unproven guard.*

> A run that silently degrades into fewer valid rows is the "green because checking
> less" failure one level further in: the coverage gate counts the rows the corpus
> **has**; this counts the rows that **actually ran**.

#### The last one: a verification step that cried catastrophe

The closing end-to-end check reported **"manifest mismatches: 103"** — i.e. every
pinned artifact corrupt — on a tree where the full sweep had just passed 27/29 and
Robot 10/10. Nothing was wrong.

`MANIFEST.sha256` stores **relative** paths, so it must be checked from the
artifact root. I ran `sha256sum -c` from the repo directory, every entry reported
`FAILED open or read`, and my one-liner `grep -cv ': OK$'` counted all 103 of those
as **checksum mismatches**. Verified from the correct directory: **103/103 OK, 0
mismatches.**

> **A check that cries catastrophe when all is well is as broken as one that
> reports success when nothing is — and it is the one you are more likely to act
> on.** The false-alarm direction feels safe, so it gets less scrutiny; had I
> trusted it I would have "fixed" a manifest that was already correct.

Replaced with `scripts/verify_artifacts.sh`, which:

* `cd`s to the artifact root explicitly (proven cwd-independent by running it from
  `/tmp`),
* asserts **coverage before integrity** (the 11-of-103 lesson),
* and counts **MISSING** and **MISMATCHED** *separately*, because "the file isn't
  there" and "the file changed" are different claims that my one-liner had
  conflated.

Proven on all three paths: healthy → `exit 0`; `MANIFEST_FLOOR=999` → `exit 3`;
run from `/tmp` → identical output.

That makes **five** instrument faults found in the closing stretch — 22/29, the
manifest coverage, the `\t` misdiagnosis, the `grep` identity, and this — against
zero new model faults. The models had stopped surprising me; the instruments had
not.

#### Anchor audit — the last cwd dependency

@rt1180emulator noted their gates are cwd-independent *by construction*
(`check.py` resolves `HERE = dirname(abspath(__file__))`, `scorecard.sh` uses
`dirname($0)/..`), so none of theirs can cry catastrophe from the wrong directory
the way my `sha256sum -c` on relative paths did. I had proven that for the new
`verify_artifacts.sh` and **never checked the other eleven scripts.**

Audited all 12. One real exception:

* `compare_consoles.sh` had **no anchor at all**, and `OUT` defaulted to the
  relative `results/DELTA-STUDY.md`. Run from anywhere but the repo root it would
  silently write the delta study into a `results/` directory it created *wherever
  you were standing* — and report success. Fixed; proven by running it from `/tmp`
  and confirming no stray `/tmp/results/` appears and the repo's copy is untouched.
* `zephyr_sweep.sh` **looks** like the same bug (`resc=scripts/rt1180_cm7.resc`)
  and is not — it is consumed as `"$HERE/$resc"`. Checked before leaving it alone;
  *not every relative-looking path is a bug, and "fixing" working code on a pattern
  match is its own error.*

All 12 scripts now anchor from `$0`. The general form of the whole closing stretch:

> **Every instrument fault here was the instrument disagreeing with its own
> context** — a manifest checked from the wrong directory, a gate counting rows the
> corpus no longer had, a verdict that could not tell "didn't run" from "ran
> wrong", an assertion about a tool nobody had identified. None was a hard bug in
> the thing being measured. **The apparatus was the least-verified component in a
> project whose only output is a measurement.**

#### Closing the orphan class in my own tree — and declining to half-build the better fix

@rt1180emulator's closing note contained the sharpest operational point of the
exchange, against themselves: `tools/bounded.sh` — a *correct*, documented
process-group wrapper for exactly the orphan bug they hit — was **sitting in their
tree the whole time**, and they reached for the broken `timeout … &` idiom anyway.

> ⭐ **A CORRECT INSTRUMENT YOU DO NOT REACH FOR IS WORTH AS LITTLE AS A WRONG ONE
> YOU TRUST.** The apparatus is not only under-verified; sometimes it is
> under-*used*.

That applies to me directly. My sweep had **no trap at all**: killed from outside —
which I did, wrapping a run in `timeout -k 10 60` — bash tears down the `( cd … &&
timeout … )` subshell and the emulator underneath keeps running. It self-cleared
that time only because every invocation also carries its own inner `timeout -k 5`.
**I was saved by a second defence, not by this one existing.**

Added a cleanup trap on INT/TERM, and proved it: started a sweep, killed it from
outside after 6 s, waited, and counted emulator processes — **0 orphans**, sweep
exits 130.

**And one thing I deliberately did not ship.** My first attempt added a
`run_bounded()` helper implementing their `setsid` + `kill -TERM -$pgid` group
discipline — the *better* fix — but the call sites still used the old form, so the
helper was **defined and never invoked**. That is worse than nothing: protection
that looks active and is not, which is precisely the "instrument you don't reach
for" failure it was meant to close. Wiring it through would mean re-quoting a
cd-then-exec with a dozen `-e` arguments, at the end of a session, in a harness
currently producing a verified 27/29. So I removed the dead helper, shipped the
additive trap that changes no control flow, and recorded the group rewrite as
**specified future work** rather than half-built decoration.

Re-verified after the change: sweep **exit 0 · 27/29**, Robot **10/10**, artifacts
**103/103**, orphans **0**.

## NETC — the last peripheral row, closed. 28/29.

The row I had twice called "the last and largest" and declined to half-build.
Consoles now **byte-identical** through the whole example: MAC learning across
four ports, then frame forwarding to each.

It took **four layers, each hidden behind the one before it** — the clearest
demonstration in the project of why line counts are a poor second-order estimate:

| layer | what it was | what it bought |
|---|---|---|
| 1 | bring-up scope: register-backed aperture + self-clearing FLR/SWR + capability values (183 lines) | `PHY Init failed!` → `NETC Switch example start.` |
| 2 | switch data path: management-TX ring, source-MAC learning, NTMP command ring, FDB table (~420 lines) | doorbells fired — but nothing visible changed |
| 3 | **MSGINTR** — a *message-interrupt router*, an entirely separate peripheral (85 lines) | NETC does not raise a wire; it performs an MSI-X **write**, and the target was a block this platform did not have |
| 4 | endpoint TX + the forwarding decision + **egress port counters** | `MAC learning.` → the full forwarding output |

### Three findings worth keeping

**1. A network controller's interrupt was a memory write to a different
peripheral.** The MSI-X entry the driver programmed carried
`addr=0x428A0000 data=0x1` — MSGINTR1. Without that router the write landed in a
hole, the TX-completion callback never ran, and **exactly one of four management
frames was sent.** The console said `MAC learning.` and stopped, which looks
nothing like "your interrupt controller is missing."

**2. The MSI-X write had to be a single 32-bit write.** I used
`sysbus.WriteBytes`, which decomposes into four byte writes — and MSGINTR is an
`IDoubleWordPeripheral`, so every one was refused. The target said so plainly:

```
Attempted Byte write isn't supported by the peripheral. Offset 0x0, value 0x1.
```

**The machine was reporting the exact fault, in the log, the whole time.** An
MSI-X message *is* a doubleword write; decomposing it is not an implementation
detail.

**3. ⭐ THE FORWARDING RESULT IS OBSERVED AS A COUNTER, NOT AS A DELIVERED FRAME.**
The demo never receives the forwarded frame. It polls the *egress port MAC's*
transmit counters (`PMn_T1023N` at `ETH_LINK+0x290`) and reports whichever port's
counter moved. A model that switched the frame perfectly and did not bump that
counter would show the demo **nothing**. The observable the test actually reads is
not the one the feature is named after — which is the same lesson as the FlexSPI
read-back, from the other direction: *find what the test can see, not what the
hardware does.*

## MECC — two rows the corpus gained mid-pass, closed. 30/31.

`@rt1180emulator` modelled MECC on their side and their corpus went 29 → 31. I
added both rows to mine **before** attempting them (the gate went 29 → 31, the
manifest floor 103 → 105), then closed them: both pass, first try after the model
landed.

`peripherals/IMXRT1180_MECC.cs`, 285 lines from the oracle's 359.

### The design point that made it work

**MECC owns OCRAM2; it is not a register block beside it.** An injected bit-flip
is baked into the *stored codeword* on write and only surfaces on a *later* read —
a plain `MappedMemory` plus a register peripheral cannot express that, because
nothing sits between the CPU and the bytes. So the peripheral takes the data
window as a second registration region and owns the backing store:

```
mecc2: Miscellaneous.IMXRT1180_MECC @ {
        sysbus 0x42930000;
        sysbus new Bus.BusMultiRegistration { address: 0x20500000; size: 0x40000; region: "ocram" }
    }
```

Single-bit errors are corrected on read (the clean data is returned, status and
one-hot bit position reported); double-bit errors return the **corrupted** data
with the multi-error status set. `SINGLE_ERR_POS_*` is a one-hot **mask**, not a
bit index — the driver takes log2 of it with a shift loop.

**Declared limitation, carried from the oracle for the same reason:** only OCRAM2
is intercepted. OCRAM1 stays plain memory, so a MECC1-on-OCRAM1 example would not
be supported — intercepting the M33/M7 shared window would turn it into an IO
region. Both tools have the identical restriction and both say so.

### Convergent evidence on a shared trap

Their cm7-ztest halt turned out to be **QEMU's `cortex-m7` defaulting to 8 MPU
regions where the real core has 16** — Zephyr's `CONFIG_USERSPACE` sized its
memory domains off `DREGION`, ran out, and panicked before the console came up.

**This project independently hit the same defect class in Renode**, recorded
earlier in `EXPERIMENT.md`: *"Renode's `CortexM` defaults to 8 MPU regions; the SDK
programs region 12."* Two independent emulator codebases, the same wrong default,
found by two different firmware stacks.

> ⭐ **A DEFAULT THAT IS WRONG FOR THE PART IS NOT A GAP, IT IS A TRAP** — it
> produces a working-looking machine that fails only the software which counts the
> resource. Neither tool's authors chose 8 for this SoC; both inherited it, and
> both had to be told by firmware.

---

# 📊 ZEPHYR DELTA STUDY — FINAL, 83 targets, both tools, byte-exact

The study Kyle asked for at the outset. Same pinned ELF to both tools, same box,
same run, **raw UART bytes** on both sides, consoles compared — no pass/fail
oracle, because a delta study asks "same bytes → same console?" and needs no notion
of what a target was *supposed* to print.

## Distribution (83 targets: 67 cm33 + 16 cm7)

| verdict | rows | meaning |
|---|---:|---|
| **identical** | **48** | byte-identical consoles |
| **timing/nondet** | **25** | differ ONLY in durations, timestamps, tick/cycle counts, timer drift, or stack canaries |
| len+content | 7 | different capture length *and* content |
| CONTENT | 2 | equal length, differing content |
| partial | 1 | prefixes match but overlap too small to call |

**73 of 83 (88 %) agree once differences that are not fidelity differences are
classified as such.**

> ⭐ Revised from 71/83 (86 %) — and **both recovered rows were my instrument, not
> either model.** The delta harness bounded each tool with a wall-clock `timeout`,
> and a run killed that way exits cleanly on both tools, so a truncated capture is
> indistinguishable from a finished one.
> • **Renode side** (75 s guard): `schedule_api` was cut at 13.93 s of guest time.
> Full re-run — 217 lines vs QEMU's 217, differing only by 1 ms of rounding in
> three printed durations. `len+content → timing/nondet`.
> • **QEMU side** (25 s guard, found by looking for the mirror image rather than
> waiting to be bitten): `cm7-mutex_api` was cut at 30 lines. Full re-run — **68
> lines, byte-identical to Renode's 68**. `partial → identical`, and another
> byte-identical two-tool agreement recovered from a harness artifact.
> Each side now has its own guard (`RSECS`, `QSECS`) because virtual-time-bounded
> and host-time-bounded runs are not comparable quantities, and a `timeout`-killed
> Renode run is scored `truncated` — never agreement, never disagreement. Details
> in EXPERIMENT.md.

> ⭐⭐ **AND THE AUDIT ITSELF HAD A BLIND SPOT, WHICH IS THE MORE IMPORTANT
> FINDING.** That audit asked every **DIFFER** row "were you cut?" — and never
> asked the `identical` rows anything. But **two truncated captures agree with
> each other perfectly**: `cm7-tests-kernel-sched-schedule_api` had been cut on
> *both* sides at nearly the same point (qemu 139 lines, renode 140, both stopping
> mid-`test_slice_scheduling`) and therefore scored `identical` — *inside* the
> agreement count. Found only because an unrelated cm7 platform change made it
> come back `CHANGED old=140 new=217` in a regression run.
> Re-run properly: **217 lines vs 217, byte-identical.** The row was `identical`
> before and is `identical` now, so **73/83 does not move — but it had been right
> by coincidence.** A correct total can be assembled from a row that was never
> actually measured, and checking the total would never have revealed that.
> Completion is now checked from the **content** (a ztest capture must reach
> `PROJECT EXECUTION SUCCESSFUL/FAILED`, with guest halts such as
> `ZEPHYR FATAL ERROR` explicitly counted as complete so the real model
> differences are not swept into the harness bucket), because both clock-based and
> exit-code-based proxies fail in *both* directions: on the very same re-run, QEMU
> exited 124 — killed by its guard — **after** printing all 217 lines.

### Every remaining non-agreeing row, accounted for

The delta study is now **closed** in the sense that matters: there is no row left
whose cause is unknown. All 10 rows that do not score as agreement:

| row | cause | whose |
|---|---|---|
| `cm33-tests-arch-arm-arm_interrupt` | EXC_RETURN.SPSEL leaks `CONTROL.SPSEL` on a nested exception — **defect #11**, located to `tlib/arch/arm/helper.c:1864` | **Renode** |
| `cm33-tests-arch-arm-arm_thread_swap` | `CCR.USERSETMPEND` STIR exemption missing on the PMSAv8 path — **defect #12**, located to `helper.c:3325` vs `helper.c:2861` | **Renode** |
| `cm33-tests-arch-arm-arm_mpu_wt` | coherent-memory emulation cannot satisfy a stale-cache premise; **both tools fail**, confirmed from two sides | shared limit |
| `cm33-tests-kernel-device` | ARMv8-M TT/CMSE fault-layer: `BUS FAULT BFAR 0x0` vs `user_copy check denied region 0x0`. **Both tools PASS the test** — the consoles differ, the verdicts do not | neither |
| `cm33-tests-kernel-threads-thread_apis` | same TT/CMSE fault layer, address `0xfffffff0` | neither |
| `cm7-tests-arch-arm-arm_interrupt` | ESF contents after a stacking fault are architecturally UNKNOWN (QEMU's `v7m_stack_write` sets MSTKERR, skips the store, `&&` short-circuits, unwritten words keep memory contents) | nondeterminism |
| `cm33-tests-kernel-early_sleep` | `k_sleep() ticks: 5000` vs `5001` | tick quantisation |
| `cm33-samples-philosophers` | scheduler **interleave** — same lines, different order — plus free-running length | nondeterminism |
| `cm7-samples-philosophers` | free-running demo, neither side terminates; compared over common prefix | by construction |
| `cm33-samples-subsys-logging-logger` | microsecond **timestamps** | nondeterminism |

**Two are Renode defects. None is an unexplained disagreement.** That is the
distinction the whole study exists to draw, and it took the QEMU model to draw it:
a single implementation can tell you a test failed, but only a second one can tell
you whether that is *your* defect, *everyone's* limit, or nobody's at all.

### Both Renode defects are MUTATION-PROVEN, and a third was found underneath

Both fixes were applied to tlib, rebuilt through `Cores/CMakeLists.txt`,
**ABI-verified** against the shipped library (2564 symbols, 0 missing, 0 extra),
installed, and the tests re-run with M0 as a positive control. Patch in
`patches/tlib-defect11-defect12.patch`.

| | before | after |
|---|---|---|
| `arm_thread_swap` | dies on `MPU FAULT / MMFAR 0xE000EF00` | **29/29 lines byte-identical to QEMU**, `SUITE PASS 100.00%` |
| `arm_interrupt` | 22-line matching prefix, 30-line capture | **52-line matching prefix**, 78-line capture; `k_oops`→3, `k_panic`→4, `__ASSERT` all arrive intact |

Fixing #11 then exposed **Renode defect #13**: ARMv8-M `MSPLIM`/`PSPLIM` are
stored and readable/writable but **never compared against SP anywhere**, and
`CFSR.STKOF` is not even defined among tlib's `USAGE_FAULT_*` bits. A register
that accepts writes and changes nothing is worse than an unimplemented one — the
firmware's `MSR PSPLIM` succeeds, the `MRS` reads it back, and ARMv8-M hardware
stack protection is silently inert.

**#13 is now FIXED and verified** (2026-09-19, `patches/tlib-defect13.patch`).
With all three patches stacked, `arm_interrupt` is **83 lines vs QEMU's 83 with
`SUITE PASS - 100.00%`**, differing only in three printed durations rounding by
1 ms — the row moves from `DIFFER` to `timing/nondet`. `arm_thread_swap` stays
byte-identical 29/29. ABI-verified 2564 symbols (0 missing / 0 extra), M0 as the
positive control.

| build | matching prefix vs QEMU's 83 lines | verdict |
|---|---:|---|
| stock 1.17.0 + PPB | 22 | `FAIL`, crash reason 16384 |
| + defect #11 | 52 | `FAIL`, stack-overflow sub-test |
| + defect #13 | **83** | **`SUITE PASS`**, 1 ms rounding only |

> ⭐⭐ **THE FIRST #13 BUILD FAILED FOR DEFECT #11'S OWN REASON, COMMITTED BY ME
> WHILE FIXING #11.** The fault fired with QEMU's exact text but on the *idle
> thread*, because the limit was selected with `is_using_process_sp_current()` —
> which consults `in_handler_mode()`, and by that point in exception entry the NVIC
> has already been acknowledged, so the CPU already counts as Handler mode while
> the frame is still going onto the pre-exception stack. The mode said *main* while
> `regs[13]` held PSP, so every check compared the thread's SP against `MSPLIM`.
> **Do not consult the mode after the mode has changed** — the same window, one
> layer down, by the person who had just written the retrospective on it.

> ⚠ **The patched results above are NOT folded into any headline number.** Every
> published figure in this scorecard was measured on
> `translate-arm-m-le.so.ppbpatch-baseline` — stock Renode 1.17.0 plus only the
> PPB patch without which the M7 cannot run the corpus at all. The fixes are not
> upstream, so they are not what a Renode user gets, and mixing them in would
> flatter the numbers with work nobody else has.

> ⭐ **A RAW "DIFFER" COUNT MATERIALLY OVERSTATES REAL DELTAS.** 35 rows differ
> byte-for-byte; **25 of them differ only in numbers that are supposed to vary.**
> `test_canary_value` differs *by design* — the stack canary is meant to be
> unpredictable. Classifying by **what** differs, not **that** something differs,
> is the difference between a scary number and a true one.

## The candidate deltas, all triaged

| row | verdict |
|---|---|
| `philosophers` | scheduler **interleave** — same lines, different order |
| `logging-logger` | microsecond **timestamps** |
| `early_sleep` | `k_sleep() ticks: 5000` vs **5001** |
| `timer_monotonic` | **timer drift**, both PASS: QEMU +0.0037 %, Renode +0.0049 % against the firmware's own 240,000,000 expectation |
| `kernel-device`, `thread_apis` | **ARMv8-M TT/CMSE fault-layer** — see below |
| `arm_interrupt` (cm33) | QEMU passes, Renode fails → **Renode defect #11**: tlib hands the *nested* SVC an EXC_RETURN of `0xFFFFFFF5` (Mode = Handler **and** SPSEL = Process, architecturally impossible), so Zephyr's `tst lr,#4` reads the frame off PSP — the outer IRQ-238 frame — instead of the correct one tlib really did push on MSP. Root cause `tlib/arch/arm/helper.c:1864`: `CONTROL.SPSEL` is never cleared on exception entry and leaks into EXC_RETURN. See EXPERIMENT.md. |
| `arm_thread_swap` | QEMU passes, Renode fatal exception → **Renode defect #12** (gap predicted before the oracle's run): an unprivileged store to **NVIC STIR** `0xE000EF00` takes an MPU DACCVIOL. tlib has the `CCR.USERSETMPEND` exemption on the **PMSAv7** path (`helper.c:2861`) but not on the **PMSAv8** path the M33 uses. See EXPERIMENT.md. |
| `schedule_api` | ~~QEMU runs tests Renode stops short of → **Renode gap**~~ **RETRACTED — my instrument, not Renode.** The delta harness's 75 s wall-clock guard killed the Renode run at 13.93 s of guest time (Renode exits on SIGTERM *cleanly*, so the log looked like a completed run). Re-run with a 420 s guard: **217 lines vs QEMU's 217**, differing on 3 lines, all 1 ms of rounding in a duration the firmware prints itself. Reclassified `len+content → timing/nondet`. |
| `arm_mpu_wt` | **both** fail → **shared limit**, confirmed from two sides |
| `arm_interrupt` (cm7) | **new, QEMU-side candidate** — see below |

### The one systematic model difference — and it is not in either machine model

Two unrelated addresses, identical divergence:

```
kernel-device  QEMU: BUS FAULT BFAR 0x0          Renode: user_copy check denied region 0x0
thread_apis    QEMU: BUS FAULT BFAR 0xfffffff0   Renode: user_copy check denied 0xfffffff0
```

@rt1180emulator traced the mechanism in Zephyr's source rather than inferring it:
`arch_buffer_validate → arm_core_mpu_buffer_validate → mpu_buffer_validate`, which
on ARMv8-M uses `arm_cmse_mpu_region_get` (the CMSE/TT lookup); Zephyr also programs
a `CONFIG_NULL_POINTER_EXCEPTION_DETECTION_MPU` region at 0x0 with no access. Both
cases are one root: **a privileged access to an MPU-denied address.** Real ARMv8-M
raises a MemManage permission fault — Renode's layer. QEMU lets it reach the bus.

Scoped to **QEMU-core `target/arm`**, not the RT1180 machine model. **Both tools
PASS**, so it is invisible to any pass/fail scorecard.

> ⭐ **AFTER 83 TARGETS, THE TWO RT1180 *MACHINE MODELS* HAVE ZERO LOCATED DELTAS
> BETWEEN THEM.** Every real difference is either a CPU-core issue, a time-model
> difference, or a gap on one side. For two independently-built models of the same
> SoC, that is the headline.

### New, QEMU-side: `cm7-tests-arch-arm-arm_interrupt` (q=83, r=83, both complete)

The fault register dump differs:

```
QEMU:   r14/lr: 0x00000000   xpsr: 0x00000000   pc: 0x00000000
Renode: r14/lr: 0x000016c9   xpsr: 0x01000000   pc: 0x000016d8
```

**Renode reports the faulting context; QEMU reports zeros.** A fault ESF should
carry the context that faulted, so Renode looks the more faithful here — the
mirror image of the cm33 row, where Renode is the one that fails. Same test, two
cores, opposite verdicts.

## What it cost to make this measurement trustworthy

Three harness bugs, all mine, all repeats of lessons already in this record: a
blanket ztest oracle applied to samples; a common-prefix rule that inflated
agreement; and a log-scraped capture that dropped whitespace and under-captured
162 lines to 1. **The third was about to publish a measurement artifact as a
fidelity delta.**

### Reclassified: `cm7-arm_interrupt` ESF is **nondet**, nobody's defect

I reported Renode as "more faithful" here (it shows a faulting context where QEMU
shows zeros). @rt1180emulator first read it the other way — zeros are faithful
because the stacking fault prevents the write — then **conceded with a citation
from QEMU's own implementation**:

`target/arm/tcg/m_helper.c:v7m_stack_write` does the MPU/SAU lookup *first*; on
failure it sets `CFSR.MSTKERR` and **skips the store**. `push_stack` chains the
eight writes with `&&`, stopping at the first faulting word. Unwritten words are
**not zeroed** — they retain whatever the stack held (zeros here, not guaranteed).

So QEMU embodies *UNKNOWN*, which is the ARM ARM's own word for stacked register
values after a stacking fault. Renode's partial frame (high words written, low
words not) and QEMU's all-zeros are **both valid**. Both set MSTKERR, both classify
reason 2, both PASS.

> ⭐ This row belongs with `test_canary_value`: **an unconstrained value that is
> supposed to be able to differ.** Neither tool is wrong, and I was wrong to call
> Renode "more faithful" from one side — the same "the value differs ≠ the value is
> wrong" error I had corrected myself on for `kernel-device`, made in the opposite
> direction by each of us in turn.

**Corrected residue of the whole study:** CONTENT deltas = **one** QEMU-core CMSE
class (2 rows, both pass, upstream `target/arm`). Three Renode gaps. One shared
limit. **Still zero deltas between the two RT1180 machine models.**

---

# 🔧 Renode core defect #14 — the core sleeps inside its own interrupt handler

Found via the oracle's bare-metal `mu` test after the dual-core rows were
reopened. Full write-up and mutation proof in
[`../EXPERIMENT.md`](../EXPERIMENT.md); patch and build recipe in
[`../patches/`](../patches/README.md).

## The measurement

Everything I had built was correct. The probe sequence eliminates each layer in
turn, and the last row is the finding:

| probe | value | eliminates |
|---|---|---|
| `MUB.RCR` | `0x00000001` | M7 is running and armed RIE0 |
| `MUB.RR0` | `0xCAFE0001` | MU cross-wiring `MUA.TR → MUB.RR` |
| `MUB.RSR` | `0x00000001` | receive-full status (RF0) |
| `nvic1 IABR` | bit 21 **set** | IRQ routing *and* acceptance |
| `cpu1 SP` | `0x3043FFE0` | stacking — a full 32-byte frame was pushed |
| `cpu1 LR` | `0xFFFFFFF9` | EXC_RETURN formation |
| `cpu1 PC` | `0x303C0098` | vector fetch — this is `mu_isr` |
| `ExecutedInstructions` | **10, frozen** | ← **nothing in the handler ever ran** |

## The mechanism

`cpu_has_work()` gates a sleeping Cortex-M on whether an interrupt is **pending**.
Taking the exception makes it **active** — and active is, correctly, not pending.
So `env->wfi` is never cleared and `cpu_exec()` returns `EXCP_WFI` forever.

> **The one event that would have cleared the flag is the very event that
> consumed it.**

Fix: clear `env->wfi` / `env->wfe` on exception entry, after the vector loop so
it covers both the vector-taken and lockup exits. Armv8-M ARM: WFI completes when
an interrupt is taken, so this is unconditional, not a heuristic.

## Mutation proof — only the `.so` changes

```
8c119501a29a  d11d12d13-verified     -> mu: FAIL
84e63202059e  d11d12d13d14-verified  -> mu: PASS
8c119501a29a  d11d12d13-verified     -> mu: FAIL
84e63202059e  d11d12d13d14-verified  -> mu: PASS
```

ABI-verified before install: 2564 symbols both sides, 0 missing, 0 extra. M0
positive control passes.

## Scope — stated as narrowly as the evidence allows

`mu` is the **only** test in the in-scope corpus that executes `WFI` (`netc-flood`,
`netc-portfwd`, `uartlink` also do, all still `block-absent(renode)`). So this moves
**one row** today — while disabling the idle path of every RTOS tomorrow.

**What is NOT shown:** that the M33 is immune. Zephyr `kernel.common` passing 394
lines *implies* it wakes from WFI via SysTick, but that is an **INFERENCE**, and it
is tagged as one rather than promoted to a result. `WfiAsNop` reads `False` on both
cores, so there is no configuration difference to hide behind.
`probes/m33-wfi-wake/` exists to replace that inference with a measurement, and the
verdict that will count is **the flip between the pre-fix and post-fix libraries**,
not a single PASS.

> **Outcome, 2026-09-19: the measurement did not arrive, and the inference is still
> an inference.** The M33 wakes normally from WFI on *both* libraries (with a working
> negative control: no sender → it never proceeds). But three attempts to reproduce
> the defect's actual window — an interrupt already pending when `WFI` executes —
> were each defeated by a different lost-wakeup race in **my own probe firmware**,
> and two of the three failed in the direction that *looks like a finding*. Details
> in [`../EXPERIMENT.md`](../EXPERIMENT.md). Recorded as OPEN. The mechanism is not
> core-specific and nothing in the patched path is, but "it should follow" is not a
> measurement.
>
> **Update, 2026-09-20 — @rt1180emulator built a race-free M33 probe, and it PASSED on
> BOTH libraries.** The easy read is "M33 unaffected, closed". It is wrong: instrumenting
> the core shows their test takes its exception with `entry wfi=0` (the safe path), where
> defect #14 lives only at `entry wfi=1` — measured on the M7 under `mu`. Their design
> deliberately prevents the IRQ being pending when WFI executes, because on QEMU that
> makes WFI no-op and would give a false PASS; **on tlib that is the trigger**, since
> `HELPER(wfi)` sets the flag unconditionally. A correct test, correctly validated, that
> is blind to this defect on this core — and it fails *green*.
>
> **CLOSED, 2026-09-20 — THE M33 IS AFFECTED.** Built a discriminating test
> (`probes/m33-wfi-window/`): STIR-pends an IRQ while it is disabled, then enables and
> WFIs in **one translation block** (verified in the disassembly: `str` at `ffe0100`,
> `wfi` at `ffe0102`). Window entry witnessed first — `D14PROBE exception=46 entry
> wfi=1` — then the flip: **pre-fix HUNG** with a 32-byte frame pushed, PC at `test_isr`
> and the handler marker still `0xBADF11A6` (zero handler instructions), **post-fix
> PASS**. Identical signature to the M7 under `mu`. Defect #14 is **not core-specific**;
> the scope is every Cortex-M on this tlib, i.e. every RTOS idle path.

---

# 🔊 AUDIO GROUP — 5/5, six SAI operating points byte-exact

Verdicts rendered **outside the guest**, against the samples in a WAV file.
Runner: `scripts/run_sai_value.sh` · checker: `scripts/check_sai.py` · raw:
[`sai-value/`](sai-value/).

| test | operating points | result |
|---|---|---|
| `imxrt1180-sai` | DIV 14 / 29 → 25 000 / 12 500 Hz | **PASS** — 4096 samples, byte-exact |
| `imxrt1180-sai-dma` | DIV 14 / 29 → 25 000 / 12 500 Hz | **PASS** — 2048 samples, byte-exact, mem→eDMA→SAI FIFO→sink |
| `imxrt1180-sai-audiopll` | DIV 7 / 15 → 48 000 / 24 000 Hz | **PASS** — 4096 samples, byte-exact, **Audio-PLL MCLK 24.576 MHz** |
| `imxrt1180-asrc` | — | **PASS** — unity DC gain + Nyquist-tone rejection |
| `imxrt1180-asrc-async` | — | **PASS** — ratio driven purely by the SAI2/SAI1 clock-source ratio |

## Why "byte-exact at two points" and not "it played something"

A model that ignores `TCR2[DIV]` renders **both** sweep points at the same rate and
reproduces the golden perfectly at whichever single rate you happen to pick. The second
point is the entire test. Likewise `asrc-async`: every m2m example selects one clock
source for both sides, so the source factor cancels and **a model that ignores `ASRCSR`
passes every ordinary test.**

> ⭐ **A FORMULA THAT IS CORRECT AT THE POINT YOU TESTED IT IS NOT A FORMULA YOU HAVE
> TESTED.**

## The rate is a claim the model is held to, not a harness input

The oracle pins QEMU's backend rate externally and lets `mixeng` resampling expose a
wrong rate. I had no mixer, so the sink writes its WAV header **from the block's own
registers** on the first sample, and `check_sai.py` asserts that header against the RM's
formula independently. A harness that pins the rate makes the file agree with the
harness; this one makes it agree with the RM.

## Provenance of the fixes

| row | cause | side |
|---|---|---|
| `sai` | no audio sink (`txFifo.Dequeue()` into nowhere) + `TCSR.SR` not momentary | **model** (mine) |
| `asrc` | `ASRCTR.ATSA` pair re-init ignored — the FIR was always correct, just never restarted | **model** (mine) |
| `asrc-async` | `ASRCSR` source factor unmodelled; SAI2 absent | **model** (mine) |
| `sai-audiopll` | nothing — the CCM Audio-PLL path was already right | — |
| `sai-dma` | nothing in the model; my checker hardcoded `NSAMPLES=4096` where the test writes 2048 | **harness** (mine) |

The sink is a **file and never a host device** — no path through `IMXRT1180_SAI.cs`
reaches a speaker, so the capture *is* the mute and *is* the evidence.

---

# 🔗 DUAL-CORE GROUP — 8/8

The M2 rung was the headline question of this whole experiment: *was M33↔M7
co-simulation easier in Renode than QEMU's socket-glued approach?* The group is now
complete on the oracle's own bare-metal binaries.

| test | result |
|---|---|
| `imxrt1180-ele-corestart` | **PASS** — S3MU kick → `0xE1D20206` + `0xD6` |
| `imxrt1180-cm7wait` | **PASS** — M7 held while CPUWAIT high, ran only after WAIT cleared |
| `imxrt1180-dualcore` | **PASS** — both cores' semihosting lines present |
| `imxrt1180-mu` | **PASS** — MU round-trip `0xCAFE0001` → `0xCAFE0002` |
| `imxrt1180-edma-swstart-order` | **PASS** |
| `imxrt1180-cm7boot` | **PASS** — M7 booted its own ITCM vector; ITCM/DTCM/background view all live |
| `imxrt1180-cm7-mpu` | **PASS** — 16 MPU regions (matches MIMXRT1189 silicon) |
| `imxrt1180-cm7-systick` | **PASS** — SysTick works on the M7 |

Plus `multicore_manager` and `rpmsg_lite_pingpong` still passing (51 ping-pong round
trips) in the SDK corpus.

## Where the eight actually came from

Only **one** of the five rows that were failing was a Renode core defect:

| row | cause | layer |
|---|---|---|
| `cm7wait` | M7 was never held at reset — `IsHalted` read False *before machine start* | my platform |
| `dualcore` | passing all along; I captured one core's semihosting backend | my instrument |
| `mu` | **defect #14** — exception entry never clears `env->wfi` | **tlib** |
| `cm7boot` / `cm7-mpu` / `cm7-systick` | cm7-linked images loaded into the M33 | my harness |
| `cm7boot` (then) | no ADC1 for the M7's background alias to reach | my platform |

> ⭐⭐ **FOUR OF THE FIVE WERE MINE.** When a cluster of tests fails together, the
> instrument and the initial conditions deserve the first probe, not the last — "they
> failed together" is a hypothesis about a shared *cause*, while a shared *observer* is
> a fact.

The three cm7-only rows had been parked as *harness-capability* — the oracle's machine
auto-detects a cm7 ELF and boots the M7 directly, a convenience on their side and not
silicon. Mirroring it is equally a convenience, so those rows are **comparable because
both tools now provide it**, not because either model earned them. Detection is
structural: a cm7-linked image enters at `0x9` / `0x35` / `0x75` (the M7's local ITCM
view) where every CM33 image in this corpus enters near `0x0ffe0011`, so the sweep reads
the ELF header rather than guessing from a name.

---

# 🌐 NETC — 4/7, and three of the four needed no frame backend

| test | result | what it needed |
|---|---|---|
| `netc-ptp` | **PASS** | IEEE-1588 timer as a DDS off virtual time — a *clock* bug in a block named Ethernet |
| `netc-fdb` | **PASS** ×6 stages | NTMP VLAN filter table (18) + the ENETC0 SI0 ring-0 doorbell |
| `netc-fwd` | **PASS** ×3 | unknown-unicast/broadcast **flooding** + split-horizon |
| `netc-rxfwd` | **PASS** | `IMACInterface`, the RX BD ring, and a harness echo wire |
| `netc-flood` / `netc-portfwd` / `netc-lab3` | open | multiple independent wire ports + their own external harnesses |

I had recorded **all seven** as blocked on "M-F needs a frame backend". Three were not.
Grouping by the peripheral's name inherited one blocker for the whole set.

## The defect I built and then caught

`EmitToWire` was written and **never called**: the TX path incremented the egress port
MAC's transmit counter and dropped the frame. **That passed `netc-fwd`** — whose
observable *is* that counter.

> ⭐⭐⭐ **A TEST THAT OBSERVES A SIDE EFFECT CANNOT DISTINGUISH THE EFFECT FROM THE
> ACT.** A counter is the right observable for a forwarding *decision*, and it cannot
> tell "forwarded" from "accounted for". Nothing in the corpus could, until a test
> demanded the payload. This is the ELE-ack and the swallowed-SAI-sample one block over,
> built by me, hours after I wrote up both of theirs.

## Renode tool facts learned here

- The three network types live in **three** namespaces:
  `IMACInterface` → `Peripherals.Network`, `MACAddress` → `Core.Structure`,
  `EthernetFrame` → `Network`. `using Antmicro.Renode.Network` resolves the frame and
  **not** the interface.
- A peripheral registered `@ none` is **not monitor-addressable**, so
  `connector Connect` cannot name it.

## The echo wire's scope is a hazard, and is confined

`EchoWire` is a **backend**, not a device: it reproduces QEMU's hardcoded
`IP_MULTICAST_LOOP=1`, which `netc-rxfwd`'s sentinel-barrier oracle requires. The same
loopback on a **switched** fabric makes the switch re-ingest its own flood and storm — a
real loop, but not the topology under test, and it would read as a forwarding defect
while being a backend artefact. It is confined to a `WIRE` sweep group that only
`netc-rxfwd` routes to.

---

# 🔗 LPSPI — 3/3, one Renode defect found, and a gap map entry settled by measurement

| test | instance | result |
|---|---|---|
| `imxrt1180-lpspi` | **LPSPI4** @ `0x42560000` (PIO) | **PASS** — `m0=0x1f d1=0x47 d2=0x01` |
| `imxrt1180-lpspi-dma` | **LPSPI1** @ `0x44360000` (eDMA3, TX=11 RX=12) | **PASS** — both gates + DMA read |
| `imxrt1180-lpspi3-dma` | **LPSPI3** @ `0x42550000` (eDMA4, TX=12 RX=13) | **PASS** — both gates + DMA read |

Two traps, both called in advance by @rt1180emulator: the three tests use **three
different instances** (the PIO row is LPSPI4, which my notes had as LPSPI1 — it would
have been the third wrong-instance mistake of the night), and **eDMA3 and eDMA4 have
separate source-number spaces**, so `12` means LPSPI1-RX on one controller and LPSPI3-TX
on the other. One shared source table would have *half*-worked.

## Renode defect #15 — `SPI.IMXRT_LPSPI` ignores `TCR[CONT]`

Renode ships an LPSPI model, so reuse was tried first, per the brief. Measured:

```
lpspi4: Pushing a command: 0x00200007     <- CONT (bit 21) IS set by the guest
lpspi4: Starting a new SPI xfer, frame size: 1 bytes
lpspi4: Sending 0x9F to the device
lpspi4: SPI transfer not initialized
lpspi4: Starting a new SPI xfer, ...      <- a NEW xfer for byte 2
lpspi4_flash: Command decoding failed on byte: 0x0
```

A new transfer per frame drops chip-select between bytes; the flash resets its decoder
and every byte after the opcode decodes as a fresh command. **Invisible to any
single-frame test** — the fifth instance tonight of a never-varying parameter collapsing
two behaviours into one observation. Own model written, honouring CONT.

## The LPSPI-CS axis — direction decided against measurement, not assertion

I flagged that my model **drops CS on CONT-clear** while theirs reportedly does not, and
said so rather than banking it. They then built a two-RDID variant and ran it:

```
first RDID :  1f 47 01   (correct)
second RDID:  00 00 00   -> their flash does NOT reframe
```

**Their side is the permissive one here; mine is faithful.** And the mechanism is
tonight's recurring class landing on them: their LPSPI controller *does* drive CS
correctly on CONT-clear — the SoC never **routes** `cs_lines` to the runtime-attached
flash, so the transition is computed and delivered where nothing observes it.

> ⭐⭐⭐ **A SIDE EFFECT COMPUTED AND LANDED WHERE NOTHING OBSERVES IT.** The same shape
> as my `EmitToWire`-never-called and their ELE ack — **sixth instance tonight**, across
> crypto, audio, switch fabric, I2C, DMA and now SPI. Their single-RDID test cannot
> distinguish "the controller drove CS" (the act) from "the slave saw CS" (the effect),
> which is the *exact* distinction that caught my own bug hours earlier.

Tagged `map-choice(rt1180emulator, permissive)` on this axis, documented by them as debt
with the measurement and a fix path, since no corpus test currently does a second
command. **Recorded, not banked** — the row is not a win for either model until a test
demands it.

---

# 💡 RGPIO — the row that was green because both sides were silent

`demo_apps/led_blinky` scored **RAN / RAN → agree = YES** for as long as this
table has existed. The QEMU corpus asserts something real for that row —
*"RGPIO4[27] toggle observable in PDOR"* — and this side could not assert it,
because **there was no RGPIO block at all**. The weakest observable always
agrees.

> ⭐ **A ROW WHERE BOTH SIDES REPORT "IT RAN" IS NOT AGREEMENT. IT IS TWO
> SILENCES THAT HAPPEN TO MATCH.** Found in this very table, not in a model.

`scripts/run_rgpio_value.sh` now measures **both emulators in one run** — Renode
through the monitor, QEMU through QMP `human-monitor-command` — on the same
pinned binary (`led_blinky_cm33.bin`, sha `5852b95c…`):

```
  renode PDOR bit: 000000000011111111110000
  qemu   PDOR bit: 000000000011111111110000
  PDDR   renode=0x08000000 qemu=0x08000000   (bit 27 = EVK user LED)
  RGPIO PASS - observed BOTH high and low on BOTH emulators (measured, not quoted)
```

Not merely "both toggled": **the same phase and duty, sample for sample.**

**The assertion is a TOGGLE, not a level** — one sample of PDOR proves nothing,
since bit 27 reads 0 both when the LED is off and when the register does not
exist. The bit must be seen high *and* low, on each emulator independently.

**Mutation-proven both ways.** Move RGPIO4 off its base and the Renode column
goes red while QEMU stays green.

> ⚠️ And the mutation run exposed a defect in the CHECKER, which is the more
> useful finding. Renode echoes a failing command, and those echoes carry
> 0x-prefixed 8-digit tokens that look exactly like samples: the checker scraped
> `0x43830054` — the PDDR **address** — and printed it as the PDDR **value**. It
> did not flip that verdict, but a parser that can mistake an address for a
> reading can lie in the other direction. It now drops the queried addresses and
> demands ≥20 of 24 samples, reporting a short window as a **harness failure,
> explicitly not as a statement about the model**.

# 🔌 The wire block — NETC 7/7, driven by the oracle's own harnesses unchanged

| test | result | how |
|---|---|---|
| `netc-flood` | **PASS** | *"a broadcast on wire port 1 was flooded out BOTH wire port 0 and wire port 2 (byte-exact); split-horizon kept it off the ingress wire"* |
| `netc-portfwd` | **PASS** | *"a frame ingressing wire port 1 was routed out wire port 2 (byte-exact)"* |
| `netc-lab3` | **PASS** | all eight phases, through @rt1180emulator's own `wire-check.py` with every assertion unchanged: *"the node is interoperable -- it emits the AGREED body, judges only its own protocol, condemns only peers that proved they could do better, and its detectors CAN fire."* |

Both passing rows are **payload-consuming**: their oracle receives the frame on the
egress port's socket and byte-compares it. These are the tests that would catch a
regression of the `EmitToWire`-never-called bug, which a counter-based test structurally
cannot.

## What it took

**Three independently observable ports.** `IMACInterface` is per-*peripheral* — one MAC,
one `FrameReady`. "Flooded out 0 and 2 but not 1" is unstateable with a single interface,
so each wire port became its own connectable node, with the switch keeping all
FDB/VLAN/split-horizon policy in one place.

**A UDP wire bridging to the host**, polled from the emulation's own clock rather than a
socket callback thread — a callback would deliver frames at an arbitrary point in another
CPU's quantum, which is non-determinism in a project whose entire value is that two
implementations can be compared byte-for-byte.

## Two bugs found on the way, both mine

**1. A correct guard in the wrong scope.** `RBMR.EN` (the CPU receive ring is armed)
gated *all* switch ingress, so wire-to-wire forwarding did not work at all. The gate is
right and hard-won — the oracle measured 88 frames DMA'd into guest address zero by
gating on `RBLENR` instead — but it belongs to **CPU delivery**, not to the switch.

> ⭐⭐ **A GUARD IN THE WRONG PLACE IS NOT A WEAKER GUARD, IT IS A DIFFERENT BEHAVIOUR.**
> Mine silently turned a switch into a host NIC.

**2. A CRC per hop.** Frames grew 32 → 36 → 40 bytes because each hop re-created the
`EthernetFrame` with `addCrc: true`. `flood.py` compares `d[14:] == BODY` exactly, so it
could never match. QEMU's socket backend carries raw frames; so does this now.

## `netc-lab3` — PASS on Renode, through the QEMU model's own harness

> ⚠️ **SUPERSEDED TWICE, 2026-09-21.** First classified **harness-bound**
> because `wire-check.py` spawns its own nodes with QEMU CLI argv at three
> `Popen` sites. Then, once the seam existed, **protocol-complete but red** on a
> Renode core gap. It is now **green**: `rc=0`, all eight phases. The older text
> is kept below the line because its *correction* (about misreading line 43) is
> still worth keeping.

**The seam, built and measured:**

* **Oracle half** — the three `Popen` sites collapse to one
  `spawn_node(group, port, mac)`. `NODE=qemu` (default) builds exactly the argv
  it always built; anything else execs `NODE_SPAWN <group> <port> <mac> <elf>`.
  No Renode-specific knowledge entered @rt1180emulator's tree.
* **Renode half** — `scripts/holobench-node.sh` in this repo.
* **Refactor proved neutral BEFORE use:** QEMU baseline `rc=0` PASS captured
  first; post-refactor `rc=0` PASS with a **clean diff of the check sequence**.

**Phases now passing against a Renode node** (MEASURED, `NODE=renode`):

| assertion | verdict |
|---|---|
| emits the AGREED body, read off the wire, accepted by the peer's `frame_ok()` | **ok — 6 frames, seq strictly increasing** |
| foreign IPv6 frames drew no CORRUPT | **ok** |
| imx91 `0x88B8` body read and ACCEPTED | **ok** |
| un-upgraded peer counted, not condemned | **ok** |
| **PASS with BOTH peers content-VERIFIED** | **ok** |
| guest `t=` a plausible Unix epoch | **FAIL — Renode defect #16** |

**The blocker was never the RT1180 model — it was Renode's core, and it is
fixed.** The firmware timestamps with ARM semihosting **SYS_TIME (0x11)**, which
Renode 1.17.0 declares in its `Operation` enum and never implements, so it
returned `-1` and the guest printed `t=4294967295.000`. Now **defect #16**, built
and mutation-proven: swap only `Infrastructure.dll` and the verdict flips
(`02d8f934…` → FAIL at the epoch check; `814444ea…` → PASS, `t=` monotonic and
advancing over 26 beats). See `patches/README.md`.

> ⭐ **THE OTHER TWO FAILURES ON THE WAY WERE THE HARNESS BEING QEMU-SHAPED, NOT
> THE NODE BEING WRONG.** Both are fixed in `spawn_node` so no call site can
> forget them:
> * Phases 1 and 8 started measuring the instant `Popen` returned, charging the
>   node for its emulator's ~9 s startup (QEMU: 0.3 s). Phase 8 sent all 80
>   frames and gave up 1.5 s later at a node that had not booted, then reported
>   *"Silence is not a verdict."* **It was not silent. It was absent, and the
>   harness said so about the node.** The barrier is the guest's FIRST PRINT —
>   blocking on the `UP` banner instead *broke QEMU*, where that banner does not
>   appear until phase 1 is already running.
> * The banner grammar check anchors with `^`, and Renode's analyzer prefixes
>   every console line with `[INFO] lpuart1: [host: …]`. That prefix is the
>   emulator talking over the guest, so the spawner strips it — the Renode node
>   now emits a console indistinguishable from QEMU's `-serial stdio`.
>
> QEMU re-verified after every one of these changes: `rc=0`, **check sequence
> diffs clean against the original baseline**.

### 🐞 And the peer found two defects the whole corpus could not

| defect | evidence |
|---|---|
| **ENETC0 TX-done rang ENETC1's doorbell** (`DoEndpointTxRing` hardcoded ENETC1's MSI-X table and vector for both callers) | one passive listener, 25 s, each emulator in turn: **QEMU 21,841 beacons; Renode 0** (one frame at startup, then silence) → after: **481**, seq strictly increasing |
| **RX writeback put the length at byte 12; the oracle puts it at byte 8** | driver read `bufLen`=0, never freed the BD: `RBCIR` stuck at 0 while `RBPIR` hit 7, ring wedged **full** after one ring's worth — `RX ring full (pir=7 cir=0)` forever |

> ⭐ **BOTH SURVIVED 55 ROWS FOR ONE STRUCTURAL REASON.** Every NETC test injects
> a frame and asserts on what came out; **none asks the node to send a second
> frame on its own initiative**, and a ring only wedges once it *wraps*. The
> parameter that never varied was **how many frames in a row**, and the corpus
> held it at one. A live peer found both in a night.

**Regression after all fixes, including the swapped core assembly that sits under
EVERY test: 45 PASS / 0 FAIL / 10 NEEDS-OWN-HARNESS, NO ROW CHANGED VERDICT** (row-by-row diff, coverage asserted 55 == 55), plus `netc-flood`
and `netc-portfwd` re-run byte-exact.

---

