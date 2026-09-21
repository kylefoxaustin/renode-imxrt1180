# CLAUDE.md — Renode RT1180 (comparison experiment vs the QEMU model)

## Why this repo exists

This is a **targeted experiment**, not a product. Build a **Renode** model of the NXP
i.MX RT1180 (MIMXRT1189, Cortex-M33 + Cortex-M7) and compare it head-to-head against
the existing **fidelity-first QEMU model** at `~/Documents/GitHub/rt1180emulator`.

The question we're answering is NOT "can Renode boot something" — it's **"where does
each tool win?"** across three axes:
1. **Fidelity** — does the *unmodified* NXP SDK firmware run the same on both?
2. **The dual-core / multi-node axis** (Renode's home turf) — is M33↔M7 co-simulation
   and cross-node determinism *easier* in Renode than the socket-glued QEMU approach?
3. **Authoring effort** — how much work to reach each milestone, in a declarative
   `.repl`/C# world vs. QEMU's compiled-C device models.

> The QEMU model is the reference oracle. Do NOT re-derive RT1180 behaviour from
> scratch — it's already been dug out, debugged, and mutation-proven over months.
> This brief carries the facts you need; the QEMU tree carries the rest (see below).

## The experiment: a 3-rung milestone ladder

Build in this order. Each rung is a comparison data point — **record effort + result
for both tools at each rung** (that record IS the deliverable).

- **M0 — `hello_world` over LPUART1.** The stock SDK `demo_apps/hello_world` (cm33)
  boots and prints. QEMU oracle: the console contains **`hello world.`**
  Exercises: M33 core + memory map + LPUART1. This is the "can Renode run the real
  vendor image at all, and how hard was cores+map+UART" baseline.
- **M1 — one self-checking driver example runs to PASS.** Pick a deterministic,
  self-verifying example both can run (a timer/compare example that prints a pass
  string). Fidelity check: same firmware, same verdict?
- **M2 — the real question: dual-core.** The stock `multicore_examples/
  multicore_manager` (M33 boots M7) and then `rpmsg_lite_pingpong` (M33↔M7 vring
  ping-pong). **This is the payoff** — the dual-core boot chain was the *hardest*
  part of the QEMU project, so "was it easier in Renode?" is the headline finding.
  QEMU oracles: multicore_manager → **`The secondary core application has been
  started.`**; rpmsg → **`Message: Size=4, DATA = 101`** (the terminal data value,
  NOT the "RPMsg demo ends" banner — its failure form contains that substring).

M0/M1 are table stakes; **M2 is the point.** If Renode's native multi-core makes M2
cheap, that's the strongest argument for the fleet's holobench to live in Renode.

## The QEMU reference (your oracle + fact source)

`~/Documents/GitHub/rt1180emulator/` — the QEMU machine `mimxrt1180-evk`. Read:
- `CLAUDE.md` — verified facts table, the bring-up loop, the hard-won lessons.
- `PERIPHERALS.md` — per-block coverage + honest gaps.
- `docs/validation/corpus.tsv` + `SCORECARD.md` — every real SDK example run + its
  EXTERNAL oracle (the string the firmware itself prints, cited by SDK file:line).
  **These oracles are exactly your M0–M2 comparison targets.**
- `hw/*/imxrt1180_*.c` — the C device models. When you need a register's exact
  behaviour, read the QEMU model (it models *what the driver polls*, which the RM
  alone does not tell you) rather than guessing.
- `reference/sdk-1189/mcuxsdk/devices/RT/RT1180/MIMXRT1189/` — the MIMXRT1189 CMSIS
  headers (`*_COMMON.h`). The source of truth for any base/offset/IRQ not below.

## Verified facts (grounded from the QEMU model — do not re-invent)

### Cores
- Cortex-M33 (secure boot core, **prio-bits 3**) + Cortex-M7 (main, **prio-bits 4**).
- NVIC: **239** external IRQs. M33 is the boot core; M7 starts held.
- Renode already has both cores (via tlib). No CPU work needed.

### Memory map
| Region | Base | Size | Note |
|---|---|---|---|
| CM33 code TCM (ITCM) | `0x0FFE0000` | 128 KiB | where `--config debug` images link |
| CM33 system TCM (DTCM) | `0x20000000` | 128 KiB | |
| OCRAM1 | `0x20484000` | `0x7C000` | first 16K TRDC-blocked on HW |
| OCRAM2 | `0x20500000` | 256 KiB | |
| FlexSPI1 NOR XIP | `0x28000000` | 16 MiB | secure alias `0x38000000` |
| **M7 TCM (system view)** | `0x303C0000` | `0x80000` | `CORE1_BOOT_ADDRESS`; M7 local view = ITCM@0x0 + DTCM@0x20000000 |
| Peripheral window | `0x40000000` | `0x20000000` | TZ-M: NS `0x4xxx_xxxx` / secure alias `0x5xxx_xxxx` (+`0x1000_0000`) |
| NETC (Ethernet) | `0x60000000` | — | out of scope for M0–M2 |

### LPUART1 (the console — M0)
- Base **`0x44380000`**, **IRQ 19**. Bind it to Renode's UART analyzer / `sysbus`.
- Offsets: VERID `0x00` (**reset `0x04010003`**), PARAM `0x04`, GLOBAL `0x08`,
  PINCFG `0x0C`, BAUD `0x10` (**reset `0x0F000004`**), STAT `0x14`, CTRL `0x18`,
  DATA `0x1C`, FIFO `0x28`, WATER `0x2C`.
- **What the driver polls (model this or it hangs):**
  - STAT.**TDRE** (bit 23) + **TC** (bit 22) = transmit ready → keep them **set**
    (synchronous TX; the QEMU model returns `TDRE|TC` always).
  - STAT.**RDRF** (bit 21) = RX has a byte.
  - WATER.**RXCOUNT** (bits [28:24]) = RX FIFO fill — `hello_world`'s echo loop
    (`LPUART_WaitForReadData`) polls this; it's fine for it to read 0 (no input).

### MU1 — inter-core mailbox (M2)
- **MUA (CM33 side) `0x44220000`**, **MUB (CM7 side) `0x44230000`**, **IRQ 21** on
  each core's NVIC. Cross-wired: `MUA.TR[n] → MUB.RR[n]` and vice-versa.
- Offsets: VER `0x000`, PAR `0x004`, CR `0x008`, SR `0x00C`, FCR `0x100`, FSR `0x104`,
  **GIER `0x110`** (GP int enable), **GCR `0x114`** (GP int request — the doorbell),
  **GSR `0x118`** (GP int status, **W1C**), TCR `0x120`, TSR `0x124`, RCR `0x128`,
  RSR `0x12C`, **TR[0..3] `0x200`**, **RR[0..3] `0x280`**.
- **Doorbell mechanism (RPMsg rides this):** writing 1 to `GCR[GIRn]` raises a GP
  interrupt on the OTHER side (`GSR[GIPn]` set → that core's NVIC IRQ); the receiver
  clears `GSR` (W1C). TR/RR message registers carry MCMGR events + data.

### Dual-core boot handshake (M2 — this was the hard part in QEMU)
- **M7_CFG @ `0x444F0080`** (BLK_CTRL_S_AONMIX): `INITVTOR` = M7 vector base `[31:7]`
  (i.e. `vtor >> 7`); **`WAIT` (CPUWAIT) = bit 4 (`0x10`), resets HIGH.**
- **SRC.SCR @ `0x44460010`** (SRC_GENERAL): `BT_RELEASE_M7 = 0x1`.
- **Two-gate release:** the M7 runs only after BOTH (a) `SCR.BT_RELEASE_M7` is set AND
  (b) `M7_CFG.WAIT` is cleared. The SDK sets INITVTOR + releases the reset with the
  image not yet copied and WAIT still high, then clears WAIT after the image is in
  place. Model both gates or you boot garbage.
- **ELE/S3MU kick:** `MCMGR_StartCore` sends `S3MU(@0x47540000).TR[0] = 0x17d20106`
  and blocks reading the reply (**`RR[0]=0xE1D20206`, `RR[1]=0xD6`**), then clears
  `M7_CFG.WAIT`. Answer that reply or MCMGR hangs.
- **CORE1_BOOT_ADDRESS = `0x303C0000`** (M7 TCM system alias). The M33 zeroes the M7
  TCM (`0x303C0000..0x30440000`) via eDMA, copies the M7 image there, then releases.

### RPMsg-lite (M2 final)
- Rides the **MU doorbell** (above) + **shared OCRAM vrings** (the two cores must see
  coherent shared memory). Both cores' NVIC must route the MU IRQ. In QEMU this
  worked first try once the dual-core boot was solid.

## Known risks / gotchas (Fable warnings — read before you estimate)

1. **⚠ Time model is the #1 risk for M2.** QEMU needed `-icount` to make the M33's
   `InitCM7DMA` sequence work: it software-STARTs an eDMA transfer to zero the M7 TCM,
   W1C-clears CH_CSR[DONE], then polls DONE — which only works if the transfer is
   *in flight* across the clear (an instant transfer sets DONE before the clear → poll
   hangs). Renode's time model is deterministic-quantum, NOT `-icount`. **Verify early
   whether Renode can express "this transfer completes after N units, not instantly."**
   If you model the TCM zero+copy as a plain memcpy (no eDMA timing), the race may
   simply not arise — but confirm it, don't assume.
2. **Model what the driver POLLS, not just what the RM lists.** Every hang in the
   QEMU model was a status bit the `fsl_*` driver waited on that wasn't modelled. The
   QEMU C models already encode these — read them.
3. **Reboot must re-park the M7 held.** In QEMU, a `system_reset` left the M7 running
   on a stale vector → HardFault lockup. If Renode reboots are in scope, ensure the M7
   returns to held on reset and only re-runs after the M33 re-does the two-gate release.
4. **Do NOT fabricate register offsets / bases / IRQs / reset values.** Derive them
   from this brief, the QEMU model, or the CMSIS header. A wrong offset is a silent
   hang; a wrong reset value is a silent-wrong.

## Building the SAME firmware (so the comparison is apples-to-apples)

Use the identical vendor images the QEMU scorecard uses. SDK cache is reboot-persistent
at `~/.cache/rt1180-sdk/` (`sdk/mcuxsdk` + `venv`).
```sh
export PATH=~/.cache/rt1180-sdk/venv/bin:$PATH ARMGCC_DIR=/usr
cd ~/.cache/rt1180-sdk/sdk/mcuxsdk
# single-core (M0/M1):
west build -b evkmimxrt1180 --toolchain armgcc examples/demo_apps/hello_world \
    -Dcore_id=cm33 --config debug -d /tmp/hw
# dual-core (M2) is a SYSBUILD (needs cmake >= 3.25; the venv has cmake 4.x via pip):
west build --sysbuild examples/multicore_examples/multicore_manager/primary \
    --toolchain armgcc --config debug -b evkmimxrt1180 -Dcore_id=cm33 -d /tmp/mc
```
`--config debug` links to TCM (`0x0FFE0000`) and runs directly — do NOT use the default
flexspi_nor XIP config. The M7 secondary is incbin'd into the primary image.

## Renode-specific setup (first moves for the new session)

- **Prereqs:** install Renode (Antmicro release/nightly) + a .NET runtime. **First
  verify Renode runs at all** (`renode --version`) before writing platform files.
- Platform = **`.repl`** (declarative peripheral map) + **`.resc`** (script: load
  platform, load ELF, start). CPU cores exist in Renode's `cpus/` — instantiate M33
  and M7. Peripherals you write in **C#** (`IDoubleWordPeripheral` etc.) unless an
  existing Renode NXP model fits.
- **Before writing anything, check what Renode already ships** for NXP i.MX RT / MCX /
  Kinetis (`platforms/`, `peripherals/`) — LPUART, MU, or timer models may be reusable
  or adaptable. Reuse beats rewrite; but verify the register map matches the facts above.
- Renode does multi-core + multi-node co-sim natively — lean into that for M2 rather
  than reproducing QEMU's socket approach.

## Reporting

- Keep an `EXPERIMENT.md` here: per rung, record (result, effort, what fought you,
  what Renode gave for free vs. what QEMU gave for free). That comparison is the point.
- Optional: the fleet message bus (`~/.claude/bin/bus.sh`) — you can report progress
  under a tag like `rt1180renode` and cross-reference the QEMU model's findings.
