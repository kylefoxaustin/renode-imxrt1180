#!/usr/bin/env python3
"""The SAI oracle for the Renode side, and it lives OUTSIDE the guest.

The golden is DERIVED FROM THE RM AND THE FIRMWARE'S OWN INTENT, recomputed here
in the same three lines the oracle's check.py uses -- deliberately NOT shared code
and deliberately NOT read back from the model.

    A MIRROR THAT DECLARES ITSELF IS STILL A MIRROR.

    CCM root 65 mux 0 = OSC_RC_24M = 24 MHz, DIV field 0 (divisor 1)  -> MCLK 24e6
    BCLK = MCLK / (2 * (TCR2[DIV] + 1))
    rate = BCLK / ((FRSZ+1) words * (W0W+1) bits)

Exits 0 or 1. A test whose verdict is read by a human is not a test.
"""
import struct
import sys

# MCLK is an ARGUMENT, not a constant: sai/sai-dma run off OSC_RC_24M (24 MHz) while
# sai-audiopll runs off the Audio PLL -- 24e6*(32+768/1000)/2 = 393.216 MHz, SAI1 root
# = AudioPll/16 -> 24.576 MHz. Hardcoding 24 MHz would have made the Audio-PLL test
# assert against the wrong tree and "pass" a clock that was never exercised.
MCLK_HZ = int(sys.argv[3]) if len(sys.argv) > 3 else 24_000_000
WORDS_PER_FRAME = 2          # TCR4[FRSZ] + 1
BITS_PER_WORD = 16           # TCR5[W0W] + 1
WANT_CHANNELS = 2
# ⭐ NSAMPLES IS THE TEST'S NUMBER, NOT A CONSTANT I GET TO PICK.
# Hardcoding 4096 made sai-dma -- which writes 2048 -- report "the SAI dropped 2048
# of them" while the firmware was printing PASS. A checker that asserts against its
# own assumption instead of the test's parameter manufactures a defect in the model.
NSAMPLES = int(sys.argv[4]) if len(sys.argv) > 4 else 4096


def rate_for(div):
    bclk = MCLK_HZ // (2 * (div + 1))
    return bclk // (WORDS_PER_FRAME * BITS_PER_WORD)


def wave_sample(n):
    v = (n * 977) & 0xFFFF
    return v - 0x10000 if v >= 0x8000 else v


GOLDEN = [wave_sample(n) for n in range(NSAMPLES)]


def fail(msg):
    print("    FAIL: %s" % msg)
    sys.exit(1)


wav_path, div = sys.argv[1], int(sys.argv[2])
want_rate = rate_for(div)

try:
    raw = open(wav_path, "rb").read()
except FileNotFoundError:
    fail("DIV=%d: the SAI produced NO AUDIO AT ALL -- no file. That is what a TDR\n"
         "          that accepts and discards looks like from outside, and what a\n"
         "          register-handshake test could never see." % div)

if not raw:
    fail("DIV=%d: the sink file is empty -- the block clocked nothing out." % div)
if raw[:4] != b"RIFF" or raw[8:12] != b"WAVE":
    fail("DIV=%d: the sink produced something that is not a RIFF/WAVE file" % div)

fi = raw.index(b"fmt ") + 8
channels, rate = struct.unpack("<HI", raw[fi + 2:fi + 8])
width = struct.unpack("<H", raw[fi + 14:fi + 16])[0] // 8
pcm = raw[raw.index(b"data") + 8:]

if width != 2:
    fail("DIV=%d: expected 16-bit samples, got %d-bit" % (div, width * 8))
if channels != WANT_CHANNELS:
    fail("DIV=%d: expected %d channels, wav says %d" % (div, WANT_CHANNELS, channels))

# ⭐ THE RATE IS ASSERTED, NOT PRINTED. The header is written by the model from its
# OWN registers, so a model that ignores TCR2[DIV] renders both sweep points at the
# same rate and is caught HERE, at the second point, and nowhere else.
if rate != want_rate:
    fail("DIV=%d: the block opened its sink at %d Hz; the RM's formula says %d Hz.\n"
         "          A model that ignores TCR2[DIV] gets the first sweep point right\n"
         "          and this one wrong." % (div, rate, want_rate))

samples = list(struct.unpack("<%dh" % (len(pcm) // 2), pcm))
nonzero = sum(1 for x in samples if x != 0)
peak = max((abs(x) for x in samples), default=0)

if nonzero == 0:
    fail("DIV=%d: THE SAI CLOCKED PURE SILENCE: %d samples, ALL ZERO. The guest would\n"
         "          have reported success anyway -- an in-guest oracle is structurally\n"
         "          incapable of noticing this." % (div, len(samples)))
if len(samples) < NSAMPLES:
    fail("DIV=%d (%d Hz): the wav holds %d samples; the firmware wrote %d. The SAI\n"
         "          dropped %d of them." % (div, want_rate, len(samples), NSAMPLES,
                                            NSAMPLES - len(samples)))

mismatch = [(i, GOLDEN[i], samples[i]) for i in range(NSAMPLES) if samples[i] != GOLDEN[i]]
if mismatch:
    i, want, got = mismatch[0]
    fail("DIV=%d (%d Hz): THE SAMPLES ARE NOT THE SAMPLES THE FIRMWARE WROTE.\n"
         "          %d of %d differ; first at index %d: wrote %d, played %d\n"
         "          'Non-silent' would have hidden every one of these: A RANGE IS NOT\n"
         "          A GOLDEN." % (div, want_rate, len(mismatch), NSAMPLES, i, want, got))

print("    ok  DIV=%-2d -> %5d Hz: %d samples, byte-exact, peak %d, %d non-zero"
      % (div, want_rate, len(samples), peak, nonzero))
sys.exit(0)
