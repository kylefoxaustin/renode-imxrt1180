//
// EchoWire — a harness-only network node that reflects every frame it receives.
//
// ⭐ THIS IS A BACKEND, NOT A DEVICE. It models nothing on the RT1180; it exists to
// reproduce, deliberately, what QEMU's socket backend gives @rt1180emulator for
// free: IP_MULTICAST_LOOP=1 is hardcoded in net/socket.c, so a node on a multicast
// socket sees its OWN transmitted frames come back. imxrt1180-netc-rxfwd's oracle
// REQUIRES that self-echo -- it TXes a "drop" frame and a "sentinel" broadcast and
// asserts on which of them comes back up the RX path.
//
// ⚠️ AND IT MUST NOT BE USED FOR THE SWITCHED TESTS. On a switched fabric the same
// loopback is a TRAP: a switch flooding onto a shared group RE-INGESTS its own flood
// and storms. That is a real loop but not the topology under test, and it would read
// as a forwarding defect while being a backend artefact. @rt1180emulator links each
// wire point-to-point for netc-flood/portfwd/lab3 for exactly this reason.
// So: EchoWire for rxfwd and the shared-hub lab; point-to-point for the rest.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Network;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Network;

namespace Antmicro.Renode.Peripherals.Network
{
    public class EchoWire : IMACInterface, IDoubleWordPeripheral, IKnownSize
    {
        public EchoWire(IMachine machine)
        {
            MAC = MACAddress.Parse("02:00:00:00:00:FE");
        }

        public MACAddress MAC { get; set; }

        public event Action<EthernetFrame> FrameReady;

        public void ReceiveFrame(EthernetFrame frame)
        {
            // Reflect it straight back, unmodified. The echo preserves ORDER, which
            // is what rxfwd's sentinel barrier relies on: if the drop frame had been
            // delivered it would sit in the RX ring BEFORE the sentinel.
            var handler = FrameReady;
            if(handler != null)
            {
                handler(frame);
            }
        }

        public void Reset()
        {
        }

        // No registers: the bus presence exists only so the monitor can name the node
        // for `connector Connect`. Reads return 0 and writes are dropped.
        public long Size => 0x100;
        public uint ReadDoubleWord(long offset) { return 0; }
        public void WriteDoubleWord(long offset, uint value) { }
    }
}
