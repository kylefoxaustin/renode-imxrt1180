#!/usr/bin/env bash
# ┌──────────────────────────────────────────────────────────────────────────┐
# │ HOLOBENCH NODE SPAWNER — one RT1180 Renode node on a multicast segment.  │
# └──────────────────────────────────────────────────────────────────────────┘
#
# This is the RENODE HALF OF THE HOLOBENCH SEAM. The fleet's wire harnesses
# (@rt1180emulator's wire-check.py and friends) used to spawn their nodes with
# QEMU argv inline, at three separate call sites -- which is why this model was
# scored NEEDS-OWN-HARNESS on `netc-lab3`: not a model gap, an argv gap.
#
# The harness now calls ONE `spawn_node(group, port, mac)`. For NODE=qemu it
# builds the argv it always built. For anything else it execs a spawner with
# this EXACT contract:
#
#     spawner <group> <port> <mac> <elf>
#         -> exec an emulator node joined to <group>:<port>, carrying <mac>,
#            running <elf>, with THE GUEST CONSOLE ON STDOUT, one line at a
#            time, and nothing else interleaved on stdout.
#
# ⭐ CONTRACT NOTES THAT ARE NOT OPTIONAL:
#
#  1. WE `exec`. The harness kills the node with Popen.terminate(), which signals
#     the process it started. If that process is a shell wrapper, the shell dies
#     and RENODE KEEPS THE SOCKET -- and the next phase binds a group whose old
#     node is still transmitting on it. That is a harness that manufactures a
#     finding. `exec` makes the pid the harness holds the pid of the emulator.
#
#  2. THE GROUP AND PORT ARE ARGUMENTS, NOT CONSTANTS. wire-check.py randomizes
#     both per phase precisely so two phases cannot contaminate each other, and
#     it spawns THREE nodes on THREE groups. A .repl with a hardcoded
#     230.0.0.9:31337 (which is what platforms/mimxrt1189_cm33_lab3.repl has)
#     would put all three on one segment and every phase would read every other
#     phase's traffic. So we GENERATE the .repl per node.
#
#  3. STDOUT IS THE CONSOLE, AND ONLY THE CONSOLE'S LINES MATTER. Renode logs to
#     stdout with a `[INFO] name:` prefix; the harness greps with re.search, so a
#     prefix is harmless -- but log NOISE is not, so the global level is WARNING
#     and only the console sources are lifted. `stdbuf -oL` because the harness
#     drains with select() and readline(): a block-buffered pipe makes a live
#     node look silent for 4 KiB at a time, which reads as "the node said
#     nothing" -- a false RED.
set -u
GROUP="${1:?usage: holobench-node.sh <group> <port> <mac> <elf>}"
PORT="${2:?}"
MAC="${3:?}"
ELF="${4:?}"

ROOT=/home/kyle/Documents/GitHub/rt1180renode
RENODE="${RENODE:-/home/kyle/.cache/renode/renode_1.17.0-portable/renode}"

# Per-node scratch, keyed by group:port so concurrent nodes never share a file.
#  4. NO `trap ... EXIT` CLEANUP HERE, ON PURPOSE. We exec, so this shell is
#     REPLACED -- an EXIT trap would never fire, and writing one would be a
#     cleanup that looks present in the source and does nothing at runtime
#     (exactly the "reports work it did not do" class this repo keeps finding).
#     The generated files are instead parked under ONE root, and THIS SCRIPT
#     PRUNES ITS OWN STALE DIRS on the way in (below). An earlier version of
#     this comment promised the cleanup to a "run_holobench.sh" that was never
#     written -- a comment describing work nothing performed, which is the same
#     defect class this repo keeps finding, just in prose instead of code.
#  5. THE SCRATCH ROOT IS USER-OWNED AND NAMESPACED, because /tmp/holobench is
#     ALREADY TAKEN -- by the real fleet holobench, root-owned, with an
#     imx95-evk lab sitting in it. The first version of this script used that
#     name, `mkdir -p` failed with EPERM, the heredocs failed too, and it
#     exec'd Renode on a .resc that had never been written. Renode said "File
#     does not exist" and exited 0. A spawner that cannot write its input must
#     DIE, not hand the harness a node-shaped process that is not a node.
TAG="$(echo "${GROUP}_${PORT}" | tr '.:' '__')"
ROOTTMP="${HOLOBENCH_TMP:-${TMPDIR:-/tmp}/rt1180renode-holobench-$(id -u)}"
mkdir -p "$ROOTTMP" || { echo "holobench-node: cannot create $ROOTTMP" >&2; exit 2; }
# Prune node dirs from finished runs. Bounded by AGE, not by count, and it never
# touches a dir younger than an hour -- concurrent nodes (wire-check runs three)
# must not clean up under each other. Each dir is a few KiB; the point is that
# the root cannot grow without limit across a long session.
find "$ROOTTMP" -maxdepth 1 -name 'node-*' -type d -mmin +60 \
    -exec rm -rf {} + 2>/dev/null || true

TMP="$ROOTTMP/node-$TAG.$$"
mkdir -p "$TMP" || { echo "holobench-node: cannot create $TMP" >&2; exit 2; }

#  6. THE GENERATED FRAGMENT CARRIES NO `using`, AND IS LOADED ADDITIVELY.
#     A .repl's relative `using` resolves against the directory of the file at
#     the TOP of the include chain, not its own -- so a generated file in /tmp
#     that inherits platforms/mimxrt1189_cm33_value.repl makes THAT file's
#     `using "mimxrt1189_m1.repl"` resolve into /tmp, and the load dies with
#     "Error E36: Using 'mimxrt1189_m1.repl' does not exist". Loading the stock
#     platform by absolute path FIRST and then this fragment on top keeps every
#     relative `using` anchored in platforms/, and keeps generated files out of
#     the repo. (netc_port0's `Switch: netc` resolves because the first load has
#     already created netc.)
cat > "$TMP/wire.repl" <<REPL
// GENERATED by holobench-node.sh for ${GROUP}:${PORT} -- do not edit, do not commit.
netc_port0: Network.IMXRT1180_NETC_Port @ sysbus 0x71001000
    portIndex: 0
    Switch: netc

labwire: Network.UdpWire @ sysbus 0x71011000
    listenPort: $PORT
    remotePort: $PORT
    multicastGroup: "$GROUP"
REPL

cat > "$TMP/node.resc" <<RESC
using sysbus
include @$ROOT/scripts/load_peripherals.resc
mach create "holobench"
machine LoadPlatformDescription @$ROOT/platforms/mimxrt1189_cm33_value.repl
machine LoadPlatformDescription @$TMP/wire.repl
sysbus Redirect 0x50000000 0x40000000 0x10000000

# ⭐ THE PORT AND THE WIRE MUST BE CONNECTED, AND DECLARING BOTH IN THE .repl DOES
# NOT CONNECT THEM. Renode network peripherals are joined by a switch at RUNTIME;
# a platform that instantiates netc_port0 and labwire and stops there produces a
# node that boots, joins the multicast group, logs "wire port 0 registered" -- and
# never moves a single frame between the two. Every one of those log lines is true
# and the node is still deaf. (Omitting these two lines is what made the first
# generated node silent, and the log gave no hint: nothing errors.)
emulation CreateSwitch "seg"
connector Connect sysbus.netc_port0 seg
connector Connect sysbus.labwire seg

# The guest's wall clock. Renode's RealTimeClockMode defaults to Epoch, i.e.
# 1970 + elapsed virtual time -- so a guest asking for the time gets 1970 and any
# peer checking for a plausible present-day epoch rejects it. HostTimeUTC anchors
# the machine clock at the host's UTC time at creation and then advances it with
# VIRTUAL time, which is both present-day and reproducible within a run.
#
# ⚠ THIS LINE DOES NOTHING YET ON A STOCK RENODE. Semihosting SYS_TIME is
# unimplemented in Renode 1.17.0 (defect #16 -- see patches/README.md), so the
# guest still reads 0xFFFFFFFF whatever this is set to. It is set here so the
# node is already configured correctly for the patched assembly, and so the
# reason the node's clock is wrong stays ONE defect and not two.
machine RealTimeClockMode HostTimeUTC

logLevel 2
# ⭐ THE GUEST CONSOLE MUST REACH STDOUT, AND logLevel ALONE DOES NOT PUT IT THERE.
# MEASURED: this firmware prints on LPUART1 (the semihosting UART stayed at 0
# bytes for a whole run, while lpuart1 carried the banner) -- so BOTH are attached
# here rather than guessing which one a given node firmware uses. showAnalyzer
# routes a UART's bytes to stdout, which is the whole contract this spawner owes
# the harness.
# ⚠ AND THE GLOBAL LOG LEVEL MUST BE LIFTED FOR THOSE UARTs, or the analyzer is a
# no-op you cannot see. Renode levels are NOISY=-1 DEBUG=0 INFO=1 WARNING=2
# ERROR=3. showAnalyzer's LoggingUartAnalyzer emits each console line at INFO --
# strictly BELOW the logLevel 2 above -- so the analyzer attaches, the guest
# prints, and stdout stays empty. The node then reads as dead to the harness while
# being perfectly alive. Lift ONLY the console sources; the bring-up noise is why
# the global level is WARNING in the first place.
# ⭐ THE GUEST CONSOLE MUST REACH STDOUT *LIVE*, AND ONLY THE ANALYZER DOES THAT.
# CreateFileBackend writes RAW undecorated bytes -- which is what the harness's
# anchored banner check wants -- but it BUFFERS: the console file stayed empty
# for a whole 25 s run and only filled when Renode disposed. A backend that
# delivers the console after the test is over is not a console. (It also refuses
# a second UART on the same path, and that error ABORTS THE REST OF THE .resc, so
# LoadELF and start never run and the "node" is not a node.)
#
# So: the analyzer, which streams. It prefixes each line with
# "[11:50:11] [INFO] lpuart1: [host: ...|virt: ...] " -- the emulator talking over
# the guest -- and the wrapper below strips exactly that prefix back off.
# logLevel must be lifted for these two sources or the analyzer emits at INFO,
# below the WARNING global, and stdout stays empty while the guest prints.
showAnalyzer lpuart1
showAnalyzer cpu.semihosting.semihosting_uart
logLevel 1 lpuart1
logLevel 1 cpu.semihosting.semihosting_uart

sysbus LoadELF @$ELF false cpu
start
RESC

# The generated input must EXIST before we replace ourselves with Renode. Past
# this `exec` there is no one left to notice that it does not.
for f in "$TMP/wire.repl" "$TMP/node.resc"; do
    [ -s "$f" ] || { echo "holobench-node: $f was not written" >&2; exit 2; }
done
[ -r "$ELF" ] || { echo "holobench-node: ELF not readable: $ELF" >&2; exit 2; }

# Renode resolves relative includes against its own directory, and its portable
# tree expects to be the cwd; every other runner here does the same cd.
cd "$(dirname "$RENODE")" || exit 2
# ⭐ THE NODE MUST OUTLIVE ITS OWN SCRIPT, AND `< /dev/null` KILLS IT.
# The .resc ends in `start`, then Renode's monitor reads stdin -- and stdin at
# EOF is a `quit`. With /dev/null the node exited the instant it started, having
# printed only its SDK banner, and `timeout` returned 0 so nothing looked wrong:
# a node that lived 90 ms reads exactly like a node that said nothing.
# A fifo opened READ-WRITE has a writer for as long as the process holds it, so
# it never reaches EOF; Renode blocks on the monitor prompt and the emulation
# runs free until the harness terminates the process -- which is precisely the
# lifecycle wire-check.py expects to own.
mkfifo "$TMP/monitor.in" || { echo "holobench-node: cannot mkfifo" >&2; exit 2; }
exec 3<> "$TMP/monitor.in"

# ── THE CONSOLE FORWARDER, AND WHY THIS IS NO LONGER A BARE `exec` ───────────
#
# Renode's UART file backend writes RAW guest bytes -- no "[INFO] lpuart1:"
# decoration -- which is what the harness's anchored banner check needs. But it
# cannot write to /dev/stdout: CreateFileBackend ROTATES any existing file first
# ("Error occured while moving old file to /dev/stdout.1: Access denied"), and
# /dev/stdout always exists. So it writes to a file and we forward that file.
#
# ⚠ THAT COSTS THE `exec`, AND THE EXEC WAS LOAD-BEARING: the harness stops a
# node with Popen.terminate(), which signals the process it started. So this
# shell must stay alive as that process AND pass the signal on -- otherwise the
# wrapper dies, Renode keeps running, and the socket stays bound to a group the
# next phase is about to reuse. The trap below is what replaces exec's guarantee.
#
# `tail -F` (capital F) is deliberate: the console file does not exist yet --
# Renode creates it when the backend is attached, several seconds in -- and -F
# retries a missing file where -f would give up immediately and forward nothing.
# The prefix stripper. Process substitution (not a pipe) so that $! below is
# RENODE's pid and the trap signals the emulator itself -- with a plain pipe, $!
# is sed, terminate() would kill the filter, and Renode would keep the socket
# bound to a group the next phase is about to reuse.
"$RENODE" --console --disable-xwt --plain \
    -e "include @$TMP/node.resc" <&3 2>/dev/null \
    > >(stdbuf -oL sed -E 's/^\[[0-9:.]+\] \[INFO\] [A-Za-z0-9_.]+: \[host:[^]]*\] //') &
RENODE_PID=$!

forward() {
    kill -TERM "$RENODE_PID" 2>/dev/null
}
trap forward TERM INT HUP

wait "$RENODE_PID"
rc=$?
# Let the last console bytes drain before the forwarder is cut off, or a verdict
# the guest printed microseconds before exit is lost -- which reads to the
# harness as a node that never said it.
sleep 0.3
exit "$rc"
