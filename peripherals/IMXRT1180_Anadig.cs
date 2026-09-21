//
// i.MX RT1180 ANADIG (analog clock: OSC + PLL + PMU) — clock-ready model.
//
// PORTED FROM THE QEMU ORACLE, NOT RE-DERIVED.
// Source: ~/Documents/GitHub/rt1180emulator/hw/misc/imxrt1180_anadig.c
// Every offset, mask and POR value below is copied from that model, which in
// turn verified them against the MIMXRT1189 CMSIS headers. Per the project
// brief: do NOT re-derive RT1180 behaviour from scratch, and do NOT fabricate
// register offsets / reset values.
//
// WHY THIS PERIPHERAL EXISTS: BOARD_BootClockRUN enables the 24 MHz OSC and
// each PLL, then polls their "stable/locked" status bits. Clock lock is
// instantaneous in emulation, so the model is register-backed (reads return
// what was written) with the status bits forced on read.
//
// ⚠ THE SUBTLETY THAT WOULD HAVE COST ME A DAY (inherited from the oracle):
// permanently forcing a PFD stable bit set HANGS the SDK's PFD reconfigure
// sequence, which gates a PFD and *waits for its stable bit to CLEAR* before
// rewriting it. Hence the one-shot relock transient below.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_Anadig : IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_Anadig(IMachine machine)
        {
            this.machine = machine;
            regs = new uint[Size / 4];
            Reset();
        }

        public long Size => 0x8000;   // IMXRT1180_ANADIG_SIZE [oracle]

        public void Reset()
        {
            for(var i = 0; i < regs.Length; i++)
            {
                regs[i] = 0;
            }
            // Power-on reset values — anadig_por[] in the oracle, verbatim.
            Poke(0x4000, 0x400000A6);  // ARM_PLL_CTRL   -- DIV_SELECT = 166
            Poke(0x4010, 0x40000003);  // SYS_PLL3_CTRL
            Poke(0x4030, 0x8CA0918D);  // SYS_PLL3_PFD
            Poke(0x4040, 0x40000000);  // SYS_PLL2_CTRL
            Poke(0x4070, 0xA098909B);  // SYS_PLL2_PFD
            Poke(0x4090, 0x00000016);  // SYS_PLL2_MFI   -- 22 => 24 MHz x 22 = 528 MHz
            Poke(0x40A0, 0x0FFFFFFF);  // SYS_PLL2_MFD
            Poke(0x4100, 0x00004000);  // SYS_PLL1_CTRL
            Poke(0x4200, 0x00004000);  // PLL_AUDIO_CTRL
            Poke(0x4310, 0x007901F2);  // OSC_RC24M_CTRL
            Poke(0x4320, 0x00000080);  // OSC_24M_CTRL
            Poke(0x4340, 0x80000000);  // OSC_400M_CTRL0
            Poke(0x4350, 0x00000001);  // OSC_400M_CTRL1
            Poke(0x4600, 0x00008000);  // PMU_BIAS_CTRL
            Poke(0x4640, 0x00000005);  // PMU_LDO_PLL
            Poke(0x4710, 0x00000040);  // PMU_REF_CTRL
            Poke(0x4740, 0x00000108);  // PMU_LDO_AON_ANA
            Poke(0x4760, 0x01301C05);  // PMU_LDO_AON_DIG
            pfdRelock = 0;
        }

        // RAW stored value, bypassing the status-injection ReadDoubleWord does.
        // The CCM's clock-tree computation needs the bits the GUEST actually wrote
        // (AUDIO_PLL CTRL0/NUMER/DENOM, the PFD fracs), not the readback view --
        // exactly as the QEMU oracle's anadig_reg() reads s->anadig->regs[off/4].
        public uint PeekRaw(long offset)
        {
            return (offset >= 0 && offset + 4 <= Size) ? regs[offset / 4] : 0u;
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset + 4 > Size || offset < 0)
            {
                this.Log(LogLevel.Warning, "OOB read @0x{0:X}", offset);
                return 0;
            }

            // AUDIO_PLL fractional-PLL block (0x4280..0x42BF) is a PLL_Type: each
            // 32-bit register is a 16-byte RW/SET/CLR/TOG group; any alias reads
            // back the RW value. [oracle]
            if(offset >= AudioPllBase && offset < AudioPllEnd)
            {
                return regs[(offset & ~0xFL) / 4];
            }

            var pfd = PfdIndex(offset);
            if(pfd >= 0)
            {
                var pv = regs[offset / 4];
                if((pfdRelock & (1u << pfd)) != 0)
                {
                    // One relock-transient read: report the PFDs not-yet-stable so
                    // the SDK's "wait for the stable bit to change" loop exits.
                    pfdRelock &= ~(1u << pfd);
                    return pv & ~PfdStableAll;
                }
                return PfdStatus(pv);
            }
            return Status(offset, regs[offset / 4]);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size || offset < 0)
            {
                this.Log(LogLevel.Warning, "OOB write @0x{0:X}", offset);
                return;
            }

            if(offset >= AudioPllBase && offset < AudioPllEnd)
            {
                // RW / SET / CLR / TOG aliases within each 16-byte group.
                var baseIdx = (offset & ~0xFL) / 4;
                switch(offset & 0xF)
                {
                    case 0x0: regs[baseIdx] = value; break;
                    case 0x4: regs[baseIdx] |= value; break;
                    case 0x8: regs[baseIdx] &= ~value; break;
                    case 0xC: regs[baseIdx] ^= value; break;
                }
                return;
            }

            var pfd = PfdIndex(offset);
            if(pfd >= 0)
            {
                // Arm a single not-stable read so the reconfigure loop can exit.
                pfdRelock |= (1u << pfd);
            }
            regs[offset / 4] = value;
        }

        // Status bits derived from enable/gate state, so firmware sees INSTANT
        // lock. [oracle]
        private uint Status(long offset, uint v)
        {
            switch(offset)
            {
                case 0x4320:               // OSC_24M_CTRL: 24M OSC always stable
                    return v | Osc24MStable;
                // TMPSNS: the fsl_tempsensor driver derives its 25C reference from
                // TEMP_VAL and asserts the alarm code it computes is >= 0, which a
                // zero trim makes negative. Report a plausible factory trim (1900)
                // and a matching conversion-done STATUS0 => ~25 C. [oracle]
                case 0x4530:               // TEMPSNS_OTP_TRIM_VALUE: TEMP_VAL=1900
                    return 1900u << 10;
                case 0x45D0:               // TMPSNS STATUS0: FINISH | 1900
                    return 0x10000u | 1900u;
                case 0x4000:               // ARM_PLL_CTRL
                case 0x4010:               // SYS_PLL3_CTRL
                case 0x4040:               // SYS_PLL2_CTRL
                case 0x4100:               // SYS_PLL1_CTRL
                case 0x4200:               // PLL_AUDIO_CTRL
                    // Report locked unconditionally: firmware waits for STABLE=1
                    // (often before it (re)asserts POWERUP), assuming the boot ROM
                    // already brought the PLL up. [oracle]
                    return v | PllStable;
                case 0x4610:               // PMU_BIAS_CTRL2: body-bias network
                    // PMU_EnableFBB() sets WB_EN then spins on WB_OK. The virtual
                    // bias network settles instantly. [oracle]
                    return (v & PmuBiasWbEn) != 0 ? (v | PmuBiasWbOk) : v;
                default:
                    return v;
            }
        }

        // Per-PFD (n=0..3, 8 bits each): STABLE = 0x40<<(n*8), CLKGATE = 0x80<<(n*8).
        private static uint PfdStatus(uint v)
        {
            for(var n = 0; n < 4; n++)
            {
                var gate = 0x80u << (n * 8);
                var stable = 0x40u << (n * 8);
                if((v & gate) != 0)
                {
                    v &= ~stable;          // gated -> not stable
                }
                else
                {
                    v |= stable;           // enabled -> stable (instant lock)
                }
            }
            return v;
        }

        private static int PfdIndex(long offset)
        {
            switch(offset)
            {
                case 0x4030: return 0;     // SYS_PLL3_PFD
                case 0x4070: return 1;     // SYS_PLL2_PFD
                default: return -1;
            }
        }

        private void Poke(long offset, uint value) => regs[offset / 4] = value;

        private readonly IMachine machine;
        private readonly uint[] regs;
        private uint pfdRelock;

        private const long AudioPllBase = 0x4280;
        private const long AudioPllEnd = 0x42C0;
        private const uint Osc24MStable = 0x40000000u;  // OSC_24M_CTRL bit 30
        private const uint PllStable = 0x20000000u;     // *_PLL_CTRL bit 29
        private const uint PfdStableAll = 0x40404040u;
        private const uint PmuBiasWbEn = 0x01000000u;   // PMU_BIAS_CTRL2 bit 24
        private const uint PmuBiasWbOk = 0x04000000u;   // PMU_BIAS_CTRL2 bit 26
    }
}
