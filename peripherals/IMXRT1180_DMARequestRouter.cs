//
// eDMA request-source router (the DMA request MUX, as a wiring object).
//
// ⭐ WHY THIS EXISTS. On silicon a peripheral does not raise "DMA channel 3" -- it
// raises its own REQUEST SOURCE number (PERI_DMA4.h: kDma4RequestMuxPwm1Write0 =
// 62 for PWM1 SM0, and so on), and the eDMA matches that number against each
// channel's CH_MUX to decide which channel runs. Firmware picks the channel by
// programming CH_MUX; it never tells the peripheral a channel number.
//
// QEMU wires it exactly that way -- hw/arm/imxrt1180_soc.c connects each PWM
// submodule's "dma-req" output to the eDMA's "dma-req" input indexed by the SOURCE
// (62..65 for PWM1 SM0..3). Renode's eDMA model instead indexes IGPIOReceiver by
// CHANNEL, but it already exposes the right primitive: TryGetChannelBySlot(), which
// finds the channel whose ServiceRequestSource (CH_MUX) matches a slot.
//
// So this object is the missing translation, and nothing more: GPIO index in =
// request source, look up the channel that selected it, service that channel.
// Wiring a peripheral straight to a channel number instead would have HARDCODED
// the firmware's CH_MUX choice into the platform -- it would work for one test and
// silently ignore CH_MUX for every other, which is the shape of bug this project
// keeps finding.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.DMA;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_DMARequestRouter : IPeripheral, IGPIOReceiver
    {
        public IMXRT1180_DMARequestRouter()
        {
        }

        public IMXRT1180_eDMA DMA { get; set; }

        public void Reset()
        {
            unroutedLogged = false;
        }

        public void OnGPIO(int requestSource, bool value)
        {
            if(!value || DMA == null)
            {
                return;
            }
            if(DMA.TryGetChannelBySlot(requestSource, out var channel))
            {
                DMA.OnGPIO(channel, true);
                return;
            }
            // ⚠ NOT AN ERROR, AND SAID ONCE. A request whose source no channel has
            // selected is exactly what silicon does with it: nothing. Firmware that
            // has not programmed CH_MUX yet raises these during init. Logging every
            // one would bury the run (the PWM raises one per reload); logging none
            // would hide a genuinely mis-wired platform.
            if(!unroutedLogged)
            {
                unroutedLogged = true;
                this.Log(LogLevel.Debug,
                    "DMA request source {0} is not selected by any channel's CH_MUX -- ignored, as " +
                    "hardware does. Reported once; if a DMA transfer you expected never runs, check " +
                    "that firmware programmed CH_MUX to this source.", requestSource);
            }
        }

        private bool unroutedLogged;
    }
}
