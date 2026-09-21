//
// i.MX RT1180 uSDHC — USDHC1 0x42850000, USDHC2 0x42860000.
//
// imxrt1180-usdhc is a caps-read + register-round-trip smoke test: HOST_CTRL_CAP
// @ +0x40 must be plausible and non-zero, DSADDR @ +0x00 must round-trip with the
// low two bits masked. That is all it checks.
//
// ⚠️ THE CAPS VALUE IS SOURCED AND FLAGGED, NOT INVENTED AND NOT TRUSTED.
// @rt1180emulator's model uses 0x07F30000 and explicitly flags it BEST-EFFORT for
// this uSDHC revision -- not silicon-exact, not boot-gating -- and told me to match
// the SHAPE rather than their exact bits. So this carries THEIR value WITH THEIR
// CAVEAT attached, rather than laundering a flagged number into an unflagged one by
// copying it across a boundary. The test cannot tell the difference, which is
// exactly why the provenance has to travel with it: a number nothing checks is the
// easiest kind to quietly promote to fact.
//
// ⭐ AND THE INIT PATH IS MODELLED THOUGH THIS TEST NEVER TOUCHES IT. Same reasoning
// as the FlexCAN freeze handshake: fsl_sdhc spins on SYS_CTRL's reset bits
// self-clearing and on INITA, and reads PRES_STATE for card-present/clock-stable.
// A model built to exactly what the test polls would pass this row and hang the SDK
// driver.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.SD
{
    public class IMXRT1180_USDHC : IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_USDHC(IMachine machine, uint capabilities = DefaultCapabilities)
        {
            this.capabilities = capabilities;
            regs = new uint[Size / 4];
            Reset();
        }

        public long Size => 0x1000;

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);
            // VEND_SPEC resets to 0x30007809 on this silicon. The oracle's note:
            // upstream sdhci reset it to ZERO and fsl_esdhc READ-MODIFY-WRITES it, so
            // the guest read the zero, wrote it back as its own configuration, and
            // walked away with the soft clock enables (14:11) OFF -- works in the
            // model, fails on hardware. A wrong reset value is a silent-wrong.
            regs[VendorSpecific / 4] = VendorSpecificReset;
            regs[PresentState / 4] = CardInserted | ClockStable;
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset + 4 > Size)
            {
                return 0;
            }
            switch(offset)
            {
                case HostControllerCapabilities:
                    return capabilities;
                case PresentState:
                    return regs[offset / 4] | CardInserted | ClockStable;
                default:
                    return regs[offset / 4];
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size)
            {
                return;
            }
            switch(offset)
            {
                case HostControllerCapabilities:
                    return;                                   // read-only
                case SystemControl:
                    // RSTA/RSTC/RSTD and INITA are momentary: the driver spins waiting
                    // for them to clear, so they must never be observable as set.
                    regs[offset / 4] = value & ~(ResetAll | ResetCommand | ResetData | InitializeActive);
                    return;
                default:
                    regs[offset / 4] = value;
                    return;
            }
        }

        private readonly uint[] regs;
        private readonly uint capabilities;

        private const long DmaSystemAddress            = 0x00;
        private const long PresentState                = 0x24;
        private const long SystemControl               = 0x2C;
        private const long HostControllerCapabilities  = 0x40;
        private const long VendorSpecific              = 0xC0;

        // SOURCED from @rt1180emulator's model, and carrying their flag: BEST-EFFORT
        // for this uSDHC revision, NOT silicon-exact.
        private const uint DefaultCapabilities   = 0x07F30000;
        private const uint VendorSpecificReset   = 0x30007809;
        private const uint CardInserted          = 1u << 16;
        private const uint ClockStable           = 1u << 3;
        private const uint ResetAll              = 1u << 24;
        private const uint ResetCommand          = 1u << 25;
        private const uint ResetData             = 1u << 26;
        private const uint InitializeActive      = 1u << 27;
    }
}
