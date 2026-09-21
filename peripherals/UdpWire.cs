//
// UdpWire — a harness-only node that bridges one NETC wire port to a host UDP socket.
//
// ⭐ THIS IS A BACKEND, NOT A DEVICE. It models nothing on the RT1180. It exists so
// @rt1180emulator's external harnesses (flood.py, portfwd.py, wire-check.py) can drive
// and observe a wire the same way they do against QEMU's `-netdev socket,udp=...`.
//
// ⚠ POINT-TO-POINT BY CONSTRUCTION. Each instance has ONE remote endpoint. That is
// deliberate: a switch flooding onto a SHARED/reflective segment re-ingests its own
// flood and storms -- a real loop, but not the topology under test, and it would read
// as a forwarding defect while being a backend artefact. The echo wire (shared,
// reflective) is for netc-rxfwd's self-echo oracle and MUST NOT be used here.
//
// ⭐⭐ POLLED FROM THE EMULATION'S OWN CLOCK, NOT A BACKGROUND THREAD. A socket
// callback fires on a thread the emulator does not own, and raising FrameReady from
// there would deliver a frame at an arbitrary point in another CPU's quantum --
// non-deterministic by construction, in a project whose whole value is that two
// implementations can be compared byte-for-byte. Polling inside a clock entry keeps
// arrival ordered with respect to virtual time. It costs a poll per tick and buys
// reproducibility.
//
using System;
using System.Net;
using System.Net.Sockets;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Network;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Network;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Network
{
    public class UdpWire : IMACInterface, IDoubleWordPeripheral, IKnownSize, IDisposable
    {
        // multicastGroup switches this from a POINT-TO-POINT wire to a SHARED SEGMENT.
        //
        // ⚠ THE TWO MODES ARE NOT INTERCHANGEABLE AND THE CHOICE IS PER-TEST.
        // A switched fabric on a reflective/shared segment re-ingests its own flood
        // and storms -- so flood/portfwd get point-to-point. But the fleet's 3-node
        // lab IS a shared segment by design, and its peer is a plain multicast socket,
        // so lab3 gets multicast. Picking the wrong one produces a believable-looking
        // forwarding failure that is entirely a backend artefact.
        public UdpWire(IMachine machine, int listenPort, int remotePort,
                       string remoteHost = "127.0.0.1", string multicastGroup = null)
        {
            this.machine = machine;

            if(multicastGroup != null)
            {
                var group = IPAddress.Parse(multicastGroup);
                this.remote = new IPEndPoint(group, listenPort);
                socket = new UdpClient();
                socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));
                socket.JoinMulticastGroup(group);
                socket.MulticastLoopback = true;   // QEMU hardcodes IP_MULTICAST_LOOP=1
                socket.Client.Blocking = false;
                this.Log(LogLevel.Info, "UDP wire: joined multicast {0}:{1}", multicastGroup, listenPort);
                machine.ClockSource.AddClockEntry(new ClockEntry(PollIntervalMicroseconds,
                    MicrosecondsPerSecond, Poll, this, "udpwire-poll", true));
                MAC = MACAddress.Parse("02:00:00:00:02:01");
                return;
            }

            this.remote = new IPEndPoint(IPAddress.Parse(remoteHost), remotePort);
            MAC = MACAddress.Parse("02:00:00:00:02:01");

            socket = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.1"), listenPort));
            socket.Client.Blocking = false;

            machine.ClockSource.AddClockEntry(new ClockEntry(PollIntervalMicroseconds,
                MicrosecondsPerSecond, Poll, this, "udpwire-poll", true));
            this.Log(LogLevel.Info, "UDP wire: listening on {0}, sending to {1}", listenPort, remote);
        }

        public MACAddress MAC { get; set; }

        public event Action<EthernetFrame> FrameReady;

        // Switch -> wire.
        public void ReceiveFrame(EthernetFrame frame)
        {
            var bytes = frame.Bytes;
            if(bytes == null || bytes.Length == 0)
            {
                return;
            }
            try
            {
                socket.Send(bytes, bytes.Length, remote);
                this.Log(LogLevel.Info, "switch -> UDP wire: {0} bytes to {1}", bytes.Length, remote);
            }
            catch(SocketException e)
            {
                this.Log(LogLevel.Warning, "UDP wire send failed: {0}", e.Message);
            }
        }

        // wire -> switch, on the emulation thread.
        private void Poll()
        {
            if(!polledOnce)
            {
                polledOnce = true;
                this.Log(LogLevel.Info, "UDP wire poll is live");
            }
            var handler = FrameReady;
            if(handler == null)
            {
                if(!noHandlerLogged)
                {
                    noHandlerLogged = true;
                    this.Log(LogLevel.Warning, "UDP wire has no FrameReady subscriber: nothing is connected to this wire");
                }
                return;
            }
            for(var guard = 0; guard < MaximumFramesPerPoll; guard++)
            {
                byte[] data;
                try
                {
                    if(socket.Available <= 0)
                    {
                        return;
                    }
                    var sender = new IPEndPoint(IPAddress.Any, 0);
                    data = socket.Receive(ref sender);
                }
                catch(SocketException)
                {
                    return;
                }
                if(data == null || data.Length < 14)
                {
                    continue;
                }
                // No CRC: the wire carries the frame exactly as the peer sent it.
                if(EthernetFrame.TryCreateEthernetFrame(data, false, out var frame))
                {
                    this.Log(LogLevel.Info, "UDP wire -> switch: {0} bytes", data.Length);
                    handler(frame);
                }
            }
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
            socket?.Dispose();
        }

        public long Size => 0x100;
        public uint ReadDoubleWord(long offset) { return 0; }
        public void WriteDoubleWord(long offset, uint value) { }

        private readonly IMachine machine;
        private readonly UdpClient socket;
        private readonly IPEndPoint remote;
        private bool polledOnce;
        private bool noHandlerLogged;

        private const long PollIntervalMicroseconds = 100;
        private const long MicrosecondsPerSecond = 1000000;
        private const int MaximumFramesPerPoll = 32;
    }
}
