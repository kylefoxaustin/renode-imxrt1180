# tlib patch bundle — Renode 1.17.0, Cortex-M core defects

Four defects in Renode's CPU core (tlib), found by running the QEMU oracle's
bare-metal tests against the Renode model. Each is **mutation-proven**: the
fix is switched off and on by swapping only the `.so`, and the test verdict
flips with it. None is argued from reading code alone.

| patch | defect | what it broke | proven by |
|---|---|---|---|
| `tlib-defect11-defect12.patch` | #11 wrong EXC_RETURN SPSEL, #12 thread-swap | `arm_thread_swap` | byte-identical console vs QEMU |
| `tlib-defect13.patch` | #13 CFSR.STKOF never raised — ARMv8-M stack-limit protection was inert while every MSR/MRS to MSPLIM/PSPLIM succeeded | stack-overflow detection | `.so` flip |
| `tlib-defect14.patch` | #14 taking an exception does not clear `env->wfi` — the core sleeps inside its own handler | **every WFI-idle RTOS path** | `.so` flip ×2, deterministic |

## ⚠️ THERE ARE TWO tlib TREES ON THIS MACHINE. ONLY ONE IS BUILT.

This cost me real time and nearly cost me a wrong patch:

- **`~/.cache/renode-infrastructure/src/Emulator/Cores/tlib`** ← **THE BUILD TREE.**
  Carries all four patches. This is what `cmake` compiles.
- `~/.cache/tlib` — a stale standalone copy carrying only 1 of the 4. Reading it
  gives plausible-looking but WRONG line numbers for every patch site.

Before editing, confirm you are in the build tree:

```sh
grep -c "RT1180 PATCH" ~/.cache/renode-infrastructure/src/Emulator/Cores/tlib/arch/arm/helper.c
```

## Build recipe

tlib **must be built through Renode's `Cores` wrapper**, not standalone: a plain
`tlib` build omits the `renode_external_attach__*` interop symbols and the
resulting library loads but misbehaves.

```sh
cd ~/.cache/renode-infrastructure/src/Emulator/Cores
cmake -S . -B /tmp/bld-tlib \
      -DTARGET_ARCH=arm-m -DTARGET_WORD_SIZE=32 -DHOST_ARCH=i386 \
      -DCMAKE_BUILD_TYPE=Release
cmake --build /tmp/bld-tlib -j"$(nproc)"
# -> /tmp/bld-tlib/tlib/translate-arm-m-le.so
```

## ⭐ ABI-VERIFY BEFORE INSTALLING. ALWAYS.

An incompatible rebuild and a wrong patch fail the same way. Separate them
*before* you install, not after:

```sh
L=~/.cache/renode/renode_1.17.0-portable/platform-lib/linux-x64
nm -D --defined-only $L/translate-arm-m-le.so   | awk '{print $3}' | sort > /tmp/abi.installed
nm -D --defined-only /tmp/bld-tlib/tlib/translate-arm-m-le.so | awk '{print $3}' | sort > /tmp/abi.new
diff /tmp/abi.installed /tmp/abi.new && echo "ABI OK"
```

Expected: **2564 symbols on both sides, 0 missing, 0 extra.**

Then install, keeping the provenance chain, and run **M0 as a positive control**
— it separates "my patch is wrong" from "my rebuild is incompatible":

```sh
cp $L/translate-arm-m-le.so $L/translate-arm-m-le.so.<previous>-verified
cp /tmp/bld-tlib/tlib/translate-arm-m-le.so $L/translate-arm-m-le.so
bash scripts/run_m0.sh      # must print: M0 PASS
```

## Library provenance

Every build is kept beside the installed one, named for the defects it carries,
so any result can be re-attributed to an exact binary.

| file | md5 (first 12) |
|---|---|
| `translate-arm-m-le.so.orig-1.17.0` | `ca2cd6ce76fb` |
| `.ppbpatch-baseline` | `9508aa67ac7a` |
| `.d11d12-verified` | `d187d18a7f59` |
| `.d11d12d13-verified` | `8c119501a29a` |
| `.d11d12d13d14-verified` **(installed)** | `834f9fbc0cbb` |

---

## `renode-defect16-semihosting-systime.patch` — BUILT, INSTALLED, MUTATION-PROVEN

Held to the same standard as the `tlib` patches: built from the tree that
actually ships, installed as a one-file swap, and proven by flipping the
assembly and watching the verdict flip with it.

| `Infrastructure.dll` | md5 | `netc-lab3` via the QEMU harness |
|---|---|---|
| stock | `02d8f93417acd86dedd2bfe4bc11e03e` | **FAIL** — `the guest t= is not a plausible Unix epoch (got 4294967295.000)` |
| patched | `814444ea98db5a743b5d04d2e08d5d00` | **PASS** — `t= present, Unix-epoch, monotonic, advancing: 1790010017.220 -> 1790010019.670 over 26 beats` |

Nothing else changed between those two runs. Regression with the patched
assembly in place (it sits under **every** test, not one peripheral path):
**45 PASS / 0 FAIL / 10 NEEDS-OWN-HARNESS, no row changed verdict**, coverage
asserted 55 == 55, plus M0, `netc-flood` and `netc-portfwd` all still passing.

### The defect

ARM semihosting **`SYS_TIME` (0x11)** and **`SYS_CLOCK` (0x10)** are declared in
`SemihostingHandler.Operation` but have **no case** in `DoSemihosting()`. Both
fall to `default:` → log + `return unchecked((uint)-1)`. Renode's own source
admits it: *"TODO: Add check for the following when they get implemented …
SYS_TIME"*.

**MEASURED**, the fleet's lab-3 node firmware, same ELF on both emulators:

| emulator | guest prints |
|---|---|
| QEMU | `t=1789974258.340` (host clock then: `1789974870`) |
| Renode | `t=4294967295.000` — exactly `0xFFFFFFFF` |

and the Renode node's log carried `Unhandled 0x11 operation (SYS_TIME)`.

This is a **general Renode gap**, not an RT1180 one: any guest that timestamps
via semihosting gets `-1`.

### Why it is not in the `tlib` bundle

Defects #11–#14 are in **`tlib`**, a native `.so` that Renode dlopens — so a fix
ships as a library swap, and the swap is what makes mutation proof cheap.
**#16 is in the managed core** (`Infrastructure.dll`, from
`src/Emulator/Cores/Arm/SemihostingHandler.cs`). `DoSemihosting` is **not
`virtual`** and `Arm.Register` takes the concrete `SemihostingHandler`, so no
peripheral in this repo can intercept it. It needs a real assembly rebuild.

### Provenance — and THE TREE I FIRST PATCHED WAS THE WRONG ONE

The TWO-TREES hazard that nearly got the wrong `tlib` patched came back, and this
time there were **three** candidates:

| tree | commit | verdict |
|---|---|---|
| `~/.cache/renode-infrastructure` | `47a4e12` | **NOT the shipped source** — patched it first anyway |
| `renode` @ tag `v1.17.0`, submodule `src/Infrastructure` | `066a7f1` | **this is the one** |
| what the binary reports: `1.17.0+20260907gitf1dd1b4af` | `f1dd1b4af` | not fetchable; a `renode`-repo commit, not a submodule one |

The two trees differ in **42 `.cs` files**, including `NVIC.cs`, `CortexM.cs`,
`TimeSourceBase.cs` and `Machine.cs`. Building the cache tree would have dropped
a newer, divergent core into a 1.17.0 install — and the 55-row sweep might well
have stayed green while CPU, NVIC and time behaviour quietly moved.

**Settled by a two-directional vote**, not by picking the tree that was already
open: extract every string literal unique to each tree (skipping `///` comments,
`.Trace(...)` and `DebugHelper.Assert(...)`, which do not survive a Release
build), and ask the shipped assembly which set it contains.

```
literals unique to CACHE tree (47a4e12): present in dll=0   absent=10
literals unique to TAG   tree (066a7f1): present in dll=2   absent=0
```

e.g. `Requested non blocking ACK which is not implemented.` — tag-only, and in
the binary.

> ⚠️ **TWO INSTRUMENT FAILURES ON THE WAY, EITHER OF WHICH WOULD HAVE "PROVED"
> SOMETHING FALSE.**
>
> 1. `strings` defaults to ASCII; .NET stores literals as **UTF-16**. The first
>    probe returned 0 hits and would have condemned the right tree. `-el` is not
>    optional.
> 2. The first three discriminators I chose returned 0 for **both** trees — they
>    were `this.Trace(...)` and `DebugHelper.Assert(...)` strings, compiled out
>    of Release. A **positive control** (three literals present in *both* trees,
>    from the same file) is what caught it: they were all in the binary, so the
>    assembly and the probe were fine and my *discriminators* were the problem.
>
> ⭐ **A NEGATIVE RESULT FROM AN UNVALIDATED INSTRUMENT IS NOT EVIDENCE.** Run
> the positive control first, every time. `diff -rq --include="*.cs"` also
> silently did nothing earlier in this same investigation — `--include` is a
> `grep` option, not a `diff` one, and it reported "0 files differ" for two trees
> that differ in 42.

### Design note — this is NOT a copy of what QEMU does

* **`SYS_CLOCK`** → elapsed **virtual** time in centiseconds. The spec says
  "centiseconds since execution started", which *is* virtual time; reading the
  host clock would make a deterministic emulator answer differently on every
  replay of an identical run.
* **`SYS_TIME`** → `machine.RealTimeClockDateTime`, Renode's **own** wall-clock
  convention (`RealTimeClockStart + elapsed virtual time`) — already used by
  `AmbiqApollo4_RTC`, `ZynqMP_RTC` and `MAX32650_RTC`. Semihosting then agrees
  with the RTC on the same machine and the existing `RealTimeClockMode` knob
  keeps working.

⚠ **Consequence a caller must know:** `RealTimeClockMode` defaults to `Epoch`
(1970 + virtual time), so a guest wanting a present-day epoch needs
`machine RealTimeClockMode HostTimeUTC`. QEMU reads the host clock
unconditionally and offers no choice; here the reproducible behaviour is the
default and the host clock is opt-in. **Labelled, not silent.**

### Build recipe — THIS IS THE ONE THAT WORKED

```sh
# 1. A .NET SDK is required; the portable Renode ships a RUNTIME only.
curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
./dotnet-install.sh --channel 8.0 --install-dir "$HOME/.dotnet"   # user-local, no sudo

# 2. Infrastructure is a SUBMODULE and cannot build standalone.
git clone --depth 1 --branch v1.17.0 --recurse-submodules --shallow-submodules \
    https://github.com/renode/renode.git
cd renode
git apply --directory=src/Infrastructure \
    /path/to/patches/renode-defect16-semihosting-systime.patch

# 3. The prebuilt reference DLLs are not in git. Take them from the portable
#    package, so the compile links against exactly what will run.
P=~/.cache/renode/renode_1.17.0-portable
mkdir -p lib/resources/libraries/ironpython-netcore
cp "$P"/IronPython.dll "$P"/IronPython.Modules.dll lib/resources/libraries/ironpython-netcore/
cp "$P"/{Sprache,protobuf-net,FlatBuffers,Microsoft.Scripting,Microsoft.Dynamic,Nini,BitMiracle.LibJpeg.NET}.dll \
   lib/resources/libraries/

# 4. TFM comes from build.sh:42, NOT from the csproj's declared net6.0.
#    -p:TargetFramework (singular) does NOT work: restore reads TargetFrameworks.
cat > Directory.Build.targets <<'EOF'
<Project>
  <PropertyGroup>
    <TargetFrameworks>net8.0</TargetFrameworks>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
  </PropertyGroup>
</Project>
EOF
~/.dotnet/dotnet build src/Infrastructure/src/Infrastructure.csproj -c Release
# -> src/Infrastructure/src/bin/Release/net8.0/Infrastructure.dll

# 5. KEEP THE OLD ASSEMBLY, named for what it lacks, and swap ONLY this file.
cp "$P"/Infrastructure.dll "$P"/Infrastructure.dll.no-d16
cp src/Infrastructure/src/bin/Release/net8.0/Infrastructure.dll "$P"/Infrastructure.dll

# 6. Mutation-prove it: swap back, the verdict must go red; swap forward, green.
NODE=renode NODE_SPAWN=scripts/holobench-node.sh \
  python3 ~/Documents/GitHub/rt1180emulator/tests/imxrt1180-netc-lab3/wire-check.py
```

Do **not** set `GUI_DISABLED`: the shipped package contains
GdkSharp/CairoSharp/GLibSharp, so a GUI-disabled build is not a drop-in.

### What the build taught that reading could not

The pre-build version of this file said the patch was *argued, not
demonstrated*, and listed the APIs verified by reading. That was the right
label at the time. Building it then falsified three things in that same file:

1. **`dotnet build src/Infrastructure.csproj` was wrong.** The project has ~12
   `ProjectReference`s to `../../../lib/**`; from a bare `renode-infrastructure`
   checkout those resolve to `~/lib/**`. It is a submodule and cannot build
   alone.
2. **`net6.0` in the csproj is not what ships.** `build.sh:42` sets
   `TFM="net8.0"` and writes it into a generated `Directory.Build.targets`.
   Overriding `-p:TargetFramework` (singular) does **not** work — restore is
   driven by `TargetFrameworks` (plural), and the net6.0 restore fails outright
   because GirCore 0.7.0 has no net6.0 target.
3. **`lib/resources/libraries/*.dll` are not in git** (IronPython, Nini,
   FlatBuffers, protobuf-net, Sprache, Microsoft.Scripting/.Dynamic,
   BitMiracle). Copied from the shipped portable — which is better than any
   substitute, since the compile then links against exactly what runs.

> ⭐ **A READING IS NOT A BUILD.** Every API call in the patch was correct — the
> reading was sound. Everything *around* the patch, which reading never touched,
> was wrong in three separate ways. The unproven label was carrying real weight.

### Equivalence of the built assembly

The new `Infrastructure.dll` differs from the shipped one in **6 strings**: the
five this patch introduces (`SYS_TIME`, `SYS_CLOCK`, `no args`, and the two
failure messages) and the version stamp. Nothing else in the string table moved,
in either direction.

⚠ **That is strong evidence, not proof.** The version stamps disagree:

```
shipped: 1.0.0+cd4b002aac2398994c4a61a34a8b31b5700facf2
built:   1.0.0+066a7f13c052215632d469c995c89aea37c573b1
```

So the shipped assembly came from a **third** commit, `cd4b002a`, which is not
fetchable from the public repo. Identical string tables cannot rule out a
difference in code carrying no literals — and one such difference is already
known to exist between the trees here (`NVIC.FilterCcrDiv0Write`, a bool default
that flipped `false`→`true`). **The 55-row sweep, M0 and the wire harnesses are
what actually bound that risk, and they are all unchanged.** Recorded rather
than papered over.
