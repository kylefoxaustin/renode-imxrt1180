#!/usr/bin/env bash
# ┌──────────────────────────────────────────────────────────────────────────┐
# │ THE ACCEPTANCE TEST: 1180-renode and 1180-qemu on ONE segment, at the    │
# │ same time, each REQUIRED to verify the other.                            │
# └──────────────────────────────────────────────────────────────────────────┘
#
# Kyle's bar, stated at the outset: "holobench booting 1180 renode alongside
# qemu 1180 and not be able to tell a functional difference."
#
# ⭐ WHY THIS NEEDED NEW FIRMWARE, AND WHY -nic mac= WAS NOT ENOUGH.
# The lab-3 image compiles in its own MAC and EtherType. MEASURED: launching QEMU
# with `-nic ...,mac=54:27:8d:00:00:99` still produced a node announcing
# `mac=54:27:8d:00:00:00` -- the NIC model's address is not the one the firmware
# stamps into frames. Two RT1180 nodes from the same image are therefore TWO
# STATIONS WITH ONE IDENTITY, and the segment cannot tell them apart. (The
# oracle's own build script carries this lesson: "THREE STATIONS WITH ONE MAC --
# and a self-test that [was green anyway]. Give each node its own.")
#
# So both nodes are rebuilt, from @rt1180emulator's own build_node(), with:
#     node A   EtherType 0x88B6   peers 0x88B9 + 0x88B5   MAC ...:00
#     node B   EtherType 0x88B9   peers 0x88B6 + 0x88B5   MAC ...:09
# 0x88B9 is free in the fleet's allocated block 0x88B5..0x88BF (0x88B5 mcx,
# 0x88B6 rt1180, 0x88B7 imx95, 0x88B8 imx91).
#
# ⭐⭐ EACH NODE *REQUIRES* THE OTHER. A's peer set names B's EtherType and B's
# names A's, so neither can reach PASS by talking to itself or to the synthetic
# peer alone. "Both printed PASS" therefore means the Renode node verified the
# QEMU node's frames AND the QEMU node verified the Renode node's -- which is the
# claim, not a coincidence of both being alive.
#
# The third peer (0x88B5) is synthetic, emitted by this script, because a node
# needs TWO verified peers to declare PASS. It is deliberately NOT one of the
# models: if it were, a failure could not be localised.
set -u
ROOT=/home/kyle/Documents/GitHub/rt1180renode
ORACLE=/home/kyle/Documents/GitHub/rt1180emulator
QEMU="${QEMU:-$ORACLE/build/qemu-system-arm}"
ELF_A="${ELF_A:-$HOME/.cache/rt1180-artifacts/sdk/lab3-0x88B6-peerB.elf}"
ELF_B="${ELF_B:-$HOME/.cache/rt1180-artifacts/sdk/lab3-0x88B9-peerA.elf}"
GROUP="${GROUP:-230.0.4.7}"; PORT="${PORT:-31477}"
SECS="${SECS:-75}"
OUT="${OUT:-$ROOT/results/holobench}"; mkdir -p "$OUT"

for f in "$ELF_A" "$ELF_B"; do
    [ -r "$f" ] || { echo "MISSING FIRMWARE: $f"; echo "build it first (see EXPERIMENT.md)"; exit 2; }
done

cleanup() {
    [ -n "${QPID:-}" ] && kill "$QPID" 2>/dev/null
    [ -n "${RPID:-}" ] && kill "$RPID" 2>/dev/null
    [ -n "${PPID2:-}" ] && kill "$PPID2" 2>/dev/null
}
trap cleanup EXIT

# ── node A on QEMU ───────────────────────────────────────────────────────────
"$QEMU" -M mimxrt1180-evk -audio none -display none -monitor none \
    -semihosting-config enable=on,target=native -kernel "$ELF_A" \
    -nic "socket,mcast=$GROUP:$PORT,mac=54:27:8d:00:00:00" \
    -serial "file:$OUT/nodeA-qemu.txt" </dev/null >/dev/null 2>&1 &
QPID=$!

# ── node B on Renode ─────────────────────────────────────────────────────────
"$ROOT/scripts/holobench-node.sh" "$GROUP" "$PORT" 54:27:8d:00:00:09 "$ELF_B" \
    > "$OUT/nodeB-renode.txt" 2>&1 &
RPID=$!

# ── the synthetic third peer (0x88B5), so each node can reach its two-peer bar ─
python3 - "$GROUP" "$PORT" "$SECS" > "$OUT/peer.log" 2>&1 <<'PY' &
import os, re, socket, struct, sys, time
H = os.path.expanduser("~/Documents/GitHub/rt1180emulator/tests/imxrt1180-netc-lab3/wire-check.py")
src = open(H).read()
ns = {"struct": struct, "random": __import__("random")}
exec(compile(src[src.index("FRAME_LEN = 64"):src.index("def check_beacon")], H, "exec"), ns)
beacon, MAC_A, ET_A = ns["beacon"], ns["MAC_A"], ns["ET_A"]      # 0x88B5, mcx's identity
group, port, secs = sys.argv[1], int(sys.argv[2]), float(sys.argv[3])
tx = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
tx.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
tx.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_LOOP, 1)
seq, t0 = 0, time.time()
while time.time() - t0 < secs:
    seq += 1
    tx.sendto(beacon(MAC_A, ET_A, seq), (group, port))
    time.sleep(0.05)
PY
PPID2=$!

sleep "$SECS"
cleanup; sleep 2

# ── verdict, rendered outside both guests ────────────────────────────────────
a="$OUT/nodeA-qemu.txt"; b="$OUT/nodeB-renode.txt"
echo "── node A (QEMU, 0x88B6) ──"; grep -aoE "ENET-LAB3 (UP|PASS)[^|]*" "$a" 2>/dev/null | tail -2 | sed 's/^/   /'
echo "── node B (Renode, 0x88B9) ──"; grep -aoE "ENET-LAB3 (UP|PASS)[^|]*" "$b" 2>/dev/null | tail -2 | sed 's/^/   /'

rc=0
# Each node must PASS *and* name the OTHER MODEL's ethertype as VERIFIED.
grep -aq "ENET-LAB3 PASS" "$a" && grep -aq "0x88b9 VERIFIED" "$a" \
  || { echo "FAIL: node A (QEMU) did not PASS with 0x88b9 (the Renode node) VERIFIED"; rc=1; }
grep -aq "ENET-LAB3 PASS" "$b" && grep -aq "0x88b6 VERIFIED" "$b" \
  || { echo "FAIL: node B (Renode) did not PASS with 0x88b6 (the QEMU node) VERIFIED"; rc=1; }
[ "$rc" = 0 ] && echo "HOLOBENCH PASS - both models on one segment, each VERIFIED the other's frames"
exit "$rc"
