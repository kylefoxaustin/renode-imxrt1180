//
// NXP i.MX RT1180 NETC -- BRING-UP SCOPE ONLY.
//
// PORTED from the QEMU oracle's hw/net/imxrt1180_netc.c. Offsets, capability
// values and the self-clearing bits are all from that file.
//
// ⚠⚠ READ THIS BEFORE BELIEVING ANY GREEN ROW THAT TOUCHES NETC.
// THIS IS NOT AN ETHERNET MODEL. It is a register-backed aperture plus the
// handful of self-clearing/capability bits the SDK's bring-up path polls. It has:
//   NO station-interface data path   NO TX/RX buffer-descriptor rings
//   NO MSI-X                         NO switch (SW0)
//   NO NTMP command-BD ring          NO FDB / VLAN tables
//   NO frame forwarding, learning, flooding or split-horizon
//   NO PTP timer
// driver_examples/netc/switch needs ALL of that to reach its oracle
// ("Frame forwarding to port") and DOES NOT PASS with this.
//
// WHY IT EXISTS: measured, the row's walls come in order, and each one hides the
// next. Clearing the bring-up walls converts the remaining NETC cost from an
// estimate into a measurement -- the same probe-first move that showed USB was one
// register and that SAI was really a codec plus a missing DMA controller.
//
// What is modelled, and why each one is load-bearing:
//   * a flat backing store for the whole 0x00C20000 aperture -- the oracle's own
//     structure, since most NETC registers are plain storage;
//   * PCI config-header INIT_FLR (@hdr+0x48) reads CLEAR -- the driver spins on
//     the FLR-complete bit;
//   * PMn_COMMAND_CONFIG SWR (@mac+0x008 and +0x408) reads CLEAR for all seven
//     port MACs -- NETC_PortSoftwareResetEthMac spins on it;
//   * NETCRR.SR reads clear, NETCSR reads 0 (STATE/ERROR clear);
//   * ECAPR1/2 and SIPCAPR1 report real capabilities (6 MSI-X, 8 TX + 8 RX BD
//     rings, 0 VSIs) so the driver's resource checks pass -- ⭐ UNDER-reporting
//     here would be the safe direction but these are the SILICON's numbers, and a
//     guest sizing rings from zeros gets nothing;
//   * SMCAPR on ENETC1 reports SM=1 -- ENETC1 *is* the switch-management ENETC.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
// The three network types live in THREE different namespaces in Renode 1.17, and
// none of them is the one the class name suggests. Found by probing the runtime
// compiler rather than guessing:
//   IMACInterface -> Antmicro.Renode.Peripherals.Network
//   MACAddress    -> Antmicro.Renode.Core.Structure
//   EthernetFrame -> Antmicro.Renode.Network
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Network;
using Antmicro.Renode.Peripherals.Network;
using Antmicro.Renode.Peripherals.Bus;
using System.Collections.Generic;

namespace Antmicro.Renode.Peripherals.Network
{
    public class IMXRT1180_NETC : IDoubleWordPeripheral, IKnownSize, IMACInterface
    {
        public IMXRT1180_NETC(IMachine machine)
        {
            this.machine = machine;          // needed for virtual time (PTP 1588)
            sysbus = machine.GetSystemBus(this);
            fdb = new FdbEntry[FdbSize];
            for(var i = 0; i < FdbSize; i++) { fdb[i] = new FdbEntry(); }
            vlan = new VlanEntry[VlanTableSize];
            for(var i = 0; i < VlanTableSize; i++) { vlan[i] = new VlanEntry(); }
            regs = new uint[Size / 4];
            phyRegisters = new ushort[NumberOfPhyRegisters];
            Reset();
        }

        public long Size => 0x00C20000;

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);
            Array.Clear(phyRegisters, 0, phyRegisters.Length);
            foreach(var e in fdb) { e.Valid = false; }
            nextEntryId = 1;
            loggedScope = false;
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset + 4 > Size)
            {
                return 0;
            }

            uint ptp;
            if(PtpRead(offset, out ptp))
            {
                return ptp;
            }

            // INIT_FLR always reads clear: the FLR-complete poll exits at once.
            if(IsPciFlr(offset))
            {
                return regs[offset / 4] & ~PciInitFlr;
            }
            // MAC software reset self-clears for every ETH_LINK port MAC.
            if(IsPortMacCommandConfig(offset))
            {
                return regs[offset / 4] & ~PortMacSoftwareReset;
            }

            // EMDIO lives INSIDE this aperture (region offset 0xBA0000), so it is
            // handled here rather than as a separate peripheral -- Renode rejects
            // overlapping sysbus registrations, and the oracle models it inside NETC too.
            switch(offset)
            {
                case EmdioConfig:
                    // BSY reported CLEAR: an MDIO transaction completes inline, so the
                    // driver's "wait while busy" poll retires immediately.
                    return regs[offset / 4] & ~EmdioBusy;
                case EmdioData:
                    return ReadPhyRegister((int)(regs[EmdioControl / 4] & EmdioDeviceMask));
                case NetcStatusRegister:        return 0;                   // STATE/ERROR clear
                case NetcResetRegister:         return regs[offset / 4] & ~NetcResetSoftwareReset;
                case Enetc0Capability0:         return 0;
                case Enetc0Capability1:
                case Enetc1Capability1:         return Capability1Value;
                case Enetc0Capability2:
                case Enetc1Capability2:         return Capability2Value;
                case Enetc0SiCapability1:
                case Enetc1SiCapability1:       return SiCapability1Value;
                case Enetc1SwitchMgmtCapability: return SwitchManagementPresent;
                default:                        return regs[offset / 4];
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size)
            {
                return;
            }
            if(PtpWrite(offset, value))
            {
                return;
            }
            regs[offset / 4] = value;

            // EMDIO data write -> the addressed PHY register (unless it is a read cmd).
            if(offset == EmdioData && (regs[EmdioControl / 4] & EmdioRead) == 0)
            {
                phyRegisters[(int)(regs[EmdioControl / 4] & EmdioDeviceMask)] = (ushort)value;
            }

            // ── DOORBELLS ────────────────────────────────────────────────────────
            // Writing a producer index is the "go" trigger, exactly as on hardware.
            if(offset == MgmtTxProducerIndex)
            {
                DoManagementTx();
            }
            if(offset == EndpointTxProducerIndex)
            {
                DoEndpointTx();
            }
            if(offset == Enetc0TxProducerIndex)
            {
                DoEnetc0Tx();
            }
            for(var ring = 0; ring < SwitchCommandRings; ring++)
            {
                if(offset == SwitchCbdrBase + ring * SwitchCbdrStep + CbdrProducerIndex)
                {
                    CommandRingDoorbell(ring);
                }
            }
        }

        // Behavioural PHY, per the oracle: BMCR reset self-clears, BMSR reports
        // link-up + autoneg-complete, ID1/ID2 identify an RTL8201 (0x001C/0xC816),
        // and the RTL8211F status register (0x1A) reports link/full-duplex/1G for
        // the switch ports' PHY driver.
        private uint ReadPhyRegister(int register)
        {
            switch(register)
            {
                case 0:  return (uint)(phyRegisters[0] & ~PhyControlReset);
                case 1:  return PhyStatusValue;
                case 2:  return PhyId1Value;
                case 3:  return PhyId2Value;
                case PhyRtl8211fStatus: return PhyRtl8211fStatusValue;
                default: return phyRegisters[register & (NumberOfPhyRegisters - 1)];
            }
        }

        // ── SWITCH MANAGEMENT TX ─────────────────────────────────────────────────
        // The switch driver injects a frame directed at an egress switch port
        // (SWT_SendFrame sets SMSO and the port number in the BD's flags dword).
        // Walk the new BDs on ENETC1's SI ring 0 and -- the demo's ports being in
        // loopback -- LEARN the frame's source MAC on that port, which is how the
        // forwarding database gets populated and what the example then queries back
        // ("Port N bounds to MAC ..."). Then mark each BD written.
        private void DoManagementTx()
        {
            var ringBase = (ulong)regs[MgmtTxBaseAddress0 / 4] | ((ulong)regs[MgmtTxBaseAddress1 / 4] << 32);
            var length = regs[MgmtTxLength / 4] & BdRingLengthMask;
            if(length == 0)
            {
                return;
            }
            var consumer = regs[MgmtTxConsumerIndex / 4] & 0xFFFF;
            var producer = regs[MgmtTxProducerIndex / 4] & 0xFFFF;
            var guard = 0u;

            while(consumer != producer && guard++ <= length)
            {
                var bdAddress = ringBase + (ulong)consumer * TxBdSize;
                var bd = sysbus.ReadBytes(bdAddress, TxBdSize);
                var bufferAddress = BitConverter.ToUInt64(bd, 0);
                uint frameLength = BitConverter.ToUInt16(bd, 10);        // standard.frameLen
                if(frameLength == 0 || frameLength > MaximumFrameLength)
                {
                    frameLength = BitConverter.ToUInt16(bd, 8);          // fall back to bufLen
                }
                frameLength = Math.Min(frameLength, MaximumFrameLength);
                var flags = BitConverter.ToUInt32(bd, 12);

                if((flags & TxDescriptorSwitchMgmtSendOption) != 0 && frameLength >= 14)
                {
                    var frame = sysbus.ReadBytes(bufferAddress, (int)frameLength);
                    var port = (int)((flags >> TxDescriptorPortShift) & TxDescriptorPortMask);
                    Learn(frame, 6, SwitchDefaultFid, port);
                }

                // Write back: written = 1, status = success.
                sysbus.WriteBytes(BitConverter.GetBytes(TxBdWritebackWritten), bdAddress + 8);
                consumer = (consumer + 1) % length;
            }
            regs[MgmtTxConsumerIndex / 4] = consumer;

            // ⭐ THE TX-DONE MSI-X. Without it the driver's completion callback never
            // runs and it never sends the next frame: MEASURED, exactly ONE of the
            // four management frames was processed and the example stalled at
            // "MAC learning." with the command ring never even doorbelled. The BD
            // write-back is not the completion signal -- the interrupt is.
            EmitMsix(Enetc1MsixTable, regs[Enetc1TxRingVector0 / 4]);
        }

        // MSI-X is a WRITE, not a wire: the entry holds a target address and the
        // data word to post there. A masked vector posts nothing.
        private void EmitMsix(long table, uint entryIndex)
        {
            var entry = table + (long)entryIndex * 16;
            if(entry + 16 > Size)
            {
                return;
            }
            var control = regs[(entry + 12) / 4];
            if((control & MsixControlMask) != 0)
            {
                return;                                    // vector masked
            }
            var messageAddress = (ulong)regs[entry / 4] | ((ulong)regs[(entry + 4) / 4] << 32);
            var messageData = regs[(entry + 8) / 4];
            if(messageAddress == 0)
            {
                return;
            }
            // ⚠ MUST be a single 32-bit write, not WriteBytes. MSGINTR is an
            // IDoubleWordPeripheral; WriteBytes decomposes into four BYTE writes and
            // every one is refused -- MEASURED, and the target said so plainly:
            //   "Attempted Byte write isn't supported by the peripheral. Offset 0x0"
            // The MSI-X message vanished, the driver's completion callback never ran,
            // and exactly one of four frames was sent. An MSI-X message IS a dword
            // write; decomposing it is not an implementation detail.
            sysbus.WriteDoubleWord(messageAddress, messageData);
        }

        // ── ENDPOINT TX: THE FORWARDING DECISION ────────────────────────────────
        // EP_SendFrame puts a frame on ENETC1 SI ring 1. The switch looks the
        // DESTINATION MAC up in the FDB and forwards it out the port(s) that entry
        // names.
        //
        // ⭐ HOW THE DEMO OBSERVES THE RESULT, which is the whole trick: it does not
        // receive the frame anywhere. It POLLS THE EGRESS PORT MAC'S TRANSMIT
        // COUNTERS (PMn_T1023N at ETH_LINK+0x290, frames-OK at +0x220) and reports
        // whichever port's counter moved -- "Frame forwarding to port N". So the
        // forwarding decision has to show up as a COUNTER INCREMENT on the right
        // port MAC, not as a delivered buffer. Model the wrong observable and the
        // frame is switched perfectly and the demo still sees nothing.
        private void DoEndpointTx()
        {
            DoEndpointTxRing(EndpointTxBaseAddress0, EndpointTxBaseAddress1,
                             EndpointTxProducerIndex, EndpointTxConsumerIndex, EndpointTxLength,
                             Enetc1MsixTable, Enetc1TxRingVector1);
        }

        // ⭐⭐ THE SWITCH LEARNS FROM THE CPU PORT'S TX RING -- AND THERE IS MORE THAN
        // ONE SUCH RING. This path existed and worked, on ENETC1 SI0 ring 1
        // (0xB4_0000 + 0x8210), which is EP_SendFrame's ring. imxrt1180-netc-fdb
        // injects its learning frame on ENETC0 SI0 ring 0 (0xB0_0000 + 0x8010), a
        // completely different doorbell -- so the frame was never ingressed, nothing
        // was learned, and the test's learning assert failed against a switch that
        // had never been handed a frame.
        //
        // Nothing about the ingress/learning logic was wrong. It was listening on one
        // doorbell out of two. Same shape as capturing one semihosting backend out of
        // two: the model does the work, and the path in is not wired.
        //
        // ⭐⭐ AND THE TX-DONE MSI-X BELONGS TO THE RING, NOT TO THIS FUNCTION.
        // Both callers used to land on `EmitMsix(Enetc1MsixTable,
        // Enetc1TxRingVector1)` -- ENETC1's table and ENETC1's vector -- because
        // that is the ring this path was written for. So ENETC0 sent its frame,
        // wrote its BD back, advanced its consumer index, and then rang ENETC1's
        // doorbell. Everything ENETC0's driver could SEE was correct; the only
        // thing missing was the completion it was actually blocked on.
        //
        // MEASURED, with one passive listener pointed at each emulator in turn:
        // QEMU 21,841 beacons in 25 s; this model 0 -- one frame at startup and
        // then silence forever. The firmware free-runs a TX loop and waits for
        // TX-done; it never came, so the node transmitted exactly once.
        //
        // SOURCED, not guessed -- hw/net/imxrt1180_netc.c:
        //   :173  #define R_SIMSITRVR0 (ENETC0_SI0_OFF + 0xB00)  /* TX ring 0 -> MSI-X entry idx */
        //   :178  #define NETC_MSIX_TABLE 0xBF0000
        //   :272  netc_emit_msix() -> netc_emit_msix_tbl(s, NETC_MSIX_TABLE, ...)  /* ENETC0PSI0 */
        //   :591  netc_emit_msix(s, netc_reg(s, R_SIMSITRVR0));   <- end of the ENETC0 TX ring
        //
        // ⚠ THE TELL WAS AVAILABLE THE WHOLE TIME AND NO TEST ASKED FOR IT.
        // netc-fwd/-fdb/-flood/-portfwd all inject a frame and assert on what
        // came out; not one of them asks the node to send a SECOND frame on its
        // own initiative. A completion that never arrives is invisible to every
        // test that only ever needs one transfer. It took a firmware that
        // free-runs -- and a peer that counts -- to turn "it works" into "it
        // works once".
        private void DoEndpointTxRing(long baseAddress0, long baseAddress1,
                                      long producerIndex, long consumerIndex, long lengthRegister,
                                      long msixTable, long ringVectorRegister)
        {
            var ringBase = (ulong)regs[baseAddress0 / 4] | ((ulong)regs[baseAddress1 / 4] << 32);
            var length = regs[lengthRegister / 4] & BdRingLengthMask;
            if(length == 0)
            {
                return;
            }
            var consumer = regs[consumerIndex / 4] & 0xFFFF;
            var producer = regs[producerIndex / 4] & 0xFFFF;
            var guard = 0u;

            while(consumer != producer && guard++ <= length)
            {
                var bdAddress = ringBase + (ulong)consumer * TxBdSize;
                var bd = sysbus.ReadBytes(bdAddress, TxBdSize);
                var bufferAddress = BitConverter.ToUInt64(bd, 0);
                uint frameLength = BitConverter.ToUInt16(bd, 10);
                if(frameLength == 0 || frameLength > MaximumFrameLength)
                {
                    frameLength = BitConverter.ToUInt16(bd, 8);
                }
                frameLength = Math.Min(frameLength, MaximumFrameLength);

                if(frameLength >= 14)
                {
                    var frame = sysbus.ReadBytes(bufferAddress, (int)frameLength);
                    // Learn the source on the CPU port, then resolve the destination.
                    Learn(frame, 6, SwitchDefaultFid, SwitchCpuPort);
                    var egress = SwitchEgressBitmap(frame, SwitchDefaultFid, SwitchCpuPort);
                    var reachedWire = false;
                    for(var port = 0; port < SwitchWirePorts; port++)
                    {
                        if((egress & (1u << port)) != 0)
                        {
                            CountEgressFrame(port, frameLength);
                            if(port != SwitchCpuPort)
                            {
                                reachedWire = true;
                            }
                        }
                    }
                    // ⭐ COUNTING A FRAME OUT IS NOT SENDING IT. The counter is what
                    // netc-fwd observes, and modelling only the counter was enough to
                    // pass that row -- which is exactly the shape of defect this
                    // project keeps finding (a block that reports work it did not do).
                    // A frame that egresses a wire port has to actually leave.
                    if(reachedWire)
                    {
                        for(var port = 0; port < SwitchWirePorts; port++)
                        {
                            if(port != SwitchCpuPort && (egress & (1u << port)) != 0)
                            {
                                SendOnPort(port, frame);
                            }
                        }
                        EmitToWire(frame);   // the block's own wire, for single-wire setups
                    }
                }

                sysbus.WriteBytes(BitConverter.GetBytes(TxBdWritebackWritten), bdAddress + 8);
                consumer = (consumer + 1) % length;
            }
            regs[consumerIndex / 4] = consumer;
            EmitMsix(msixTable, regs[ringVectorRegister / 4]);
        }

        // ENETC0 SI0 ring 0 -- the ring imxrt1180-netc-fdb injects on.
        private void DoEnetc0Tx()
        {
            DoEndpointTxRing(Enetc0TxBaseAddress0, Enetc0TxBaseAddress1,
                             Enetc0TxProducerIndex, Enetc0TxConsumerIndex, Enetc0TxLength,
                             Enetc0MsixTable, Enetc0TxRingVector0);
        }

        // ── SWITCH FORWARDING DECISION ───────────────────────────────────────────
        //
        // ⭐⭐ AN UNKNOWN DESTINATION FLOODS. IT DOES NOT VANISH.
        // The previous version counted egress ONLY when the FDB held an entry, so a
        // broadcast or an unknown unicast was silently dropped -- and imxrt1180-netc-fwd
        // asserts precisely the opposite in two of its three cases. Worse, it made an
        // UNPROGRAMMED switch a black hole, when an unprogrammed switch must behave as
        // a hub: that is what plain endpoint TX relies on to reach the wire at all.
        //
        // Rule, from the oracle's netc_switch_egress():
        //   - group destination (bit0 of the first octet) -> flood all ports;
        //   - unicast with an FDB entry -> that entry's port bitmap;
        //   - unknown unicast -> flood;
        //   - intersect with the VLAN's port membership when this fid has a VF entry;
        //   - never include the ingress port (split-horizon).
        private uint SwitchEgressBitmap(byte[] frame, ushort fid, int ingressPort)
        {
            uint bitmap;
            if((frame[0] & 0x01) != 0)
            {
                bitmap = SwitchAllPorts;                   // group address -> flood
            }
            else
            {
                var entry = FindByKey(frame, 0, fid);
                bitmap = entry != null ? entry.PortBitmap : SwitchAllPorts;   // unknown unicast -> flood
            }
            for(var i = 0; i < VlanTableSize; i++)
            {
                if(vlan[i].Valid && (vlan[i].Cfge[1] & 0xFFF) == fid)
                {
                    bitmap &= vlan[i].Cfge[0] & 0xFFFFFF;  // restrict to VLAN members
                    break;
                }
            }
            return bitmap & ~(1u << ingressPort);          // split-horizon
        }

        // Bump the egress port MAC's TX counters. The demo reads the 512-1023-octet
        // bucket, so a frame is counted there when its length lands in that range --
        // counting every frame in one bucket regardless of size would make the demo
        // report a port for a frame the hardware would have counted elsewhere.
        private void CountEgressFrame(int port, uint frameLength)
        {
            var macBase = EthernetLinkMacBases[port];
            regs[(macBase + PortMacTxFramesOk) / 4] += 1;
            if(frameLength >= 512 && frameLength <= 1023)
            {
                regs[(macBase + PortMacTx512To1023) / 4] += 1;
            }
        }

        // ── SOURCE-MAC LEARNING ──────────────────────────────────────────────────
        // A frame ingressing on `port` teaches the switch its source MAC is reachable
        // there: create or refresh a DYNAMIC entry. Rules carried from the oracle:
        // a group (multicast/broadcast) source is never a real station; a STATIC
        // entry is never disturbed by learning; a known dynamic MAC seen on a new
        // port has MOVED; a full table stops learning silently, as the CAM does --
        // that is not a command error.
        private void Learn(byte[] frame, int macOffset, ushort fid, int port)
        {
            if((frame[macOffset] & 0x01) != 0)
            {
                return;                                   // group address: not a source
            }
            var existing = FindByKey(frame, macOffset, fid);
            if(existing != null)
            {
                if(existing.Dynamic)
                {
                    existing.PortBitmap = 1u << port;      // station moved
                }
                return;                                    // static entry: operator wins
            }
            foreach(var e in fdb)
            {
                if(e.Valid)
                {
                    continue;
                }
                Array.Copy(frame, macOffset, e.Mac, 0, 6);
                e.Fid = fid;
                e.PortBitmap = 1u << port;
                e.Dynamic = true;
                e.CfgeFlags = 1u << 11;                    // dynamic bit
                e.EtEid = 0;
                e.EntryId = nextEntryId++;
                e.Valid = true;
                return;
            }
            // Table full: the hardware simply stops learning. Not an error.
        }

        private FdbEntry FindByKey(byte[] buffer, int macOffset, ushort fid)
        {
            foreach(var e in fdb)
            {
                if(!e.Valid || e.Fid != fid)
                {
                    continue;
                }
                var match = true;
                for(var i = 0; i < 6; i++)
                {
                    if(e.Mac[i] != buffer[macOffset + i]) { match = false; break; }
                }
                if(match)
                {
                    return e;
                }
            }
            return null;
        }

        private FdbEntry FindById(uint id)
        {
            foreach(var e in fdb)
            {
                if(e.Valid && e.EntryId == id)
                {
                    return e;
                }
            }
            return null;
        }

        // ── NTMP COMMAND RING ────────────────────────────────────────────────────
        // CBDRPIR written -> process every BD between consumer and producer, then
        // advance CBDRCIR, which is what releases the driver's spin (it polls
        // CBDRCIR == producerIndex).
        private void CommandRingDoorbell(int ring)
        {
            var regBase = SwitchCbdrBase + ring * SwitchCbdrStep;
            if((regs[(regBase + CbdrMode) / 4] & CbdrModeEnable) == 0)
            {
                return;                                    // ring disabled
            }
            var length = regs[(regBase + CbdrLength) / 4] & CbdrLengthMask;
            if(length == 0)
            {
                return;
            }
            var ringBase = (ulong)(regs[(regBase + CbdrBaseAddress0) / 4] & CbdrBaseAddressMask)
                         | ((ulong)regs[(regBase + CbdrBaseAddress1) / 4] << 32);
            var producer = regs[(regBase + CbdrProducerIndex) / 4] & CbdrIndexMask;
            var consumer = regs[(regBase + CbdrConsumerIndex) / 4] & CbdrIndexMask;
            var guard = 0u;

            while(consumer != producer && guard++ <= length)
            {
                ProcessCommandBd(ringBase + (ulong)consumer * NtmpBdSize);
                consumer = (consumer + 1) % length;
            }
            regs[(regBase + CbdrConsumerIndex) / 4] = producer;   // completion
        }

        // One 32-byte NTMP command BD: dispatch on tableId/cmd, write the response
        // back into the BD's dword@12 as numMatched[15:0] | error[27:16] | ready[31].
        private void ProcessCommandBd(ulong bdAddress)
        {
            var bd = sysbus.ReadBytes(bdAddress, NtmpBdSize);
            var requestAddress = BitConverter.ToUInt64(bd, 0);
            var dword3 = BitConverter.ToUInt32(bd, 12);
            var command = dword3 & 0xF;
            var access = (dword3 >> 12) & 0x3;
            var tableId = (dword3 >> 16) & 0xFF;

            ushort matched = 0;
            uint error;
            if(tableId == NtmpTableFdb)
            {
                error = FdbOperation(command, access, requestAddress, ref matched);
            }
            else if(tableId == NtmpTableVlanFilter)
            {
                error = VlanFilterOperation(command, access, requestAddress, ref matched);
            }
            else
            {
                // ⭐ NOT MODELLED -> TELL THE DRIVER through the BD's own error field,
                // a documented non-gating channel. A silent ack over an unmodelled
                // table is a lie the driver cannot see. (VLAN filter table included:
                // this model does not implement it.)
                this.Log(LogLevel.Warning, "NTMP command for unmodelled table {0} (cmd 0x{1:X}) "
                    + "-- returning kNETC_InvTableID rather than a silent success.", tableId, command);
                error = NtmpErrorInvalidTable;
            }

            var response = (uint)(matched & 0xFFFF) | ((error & 0xFFF) << 16) | (1u << 31);
            sysbus.WriteBytes(BitConverter.GetBytes(response), bdAddress + 12);
        }

        // ── VLAN FILTER TABLE (NTMP table 18) ────────────────────────────────────
        //
        // The VLAN filter maps a VID to a filtering-ID (FID) and a port membership;
        // the FDB lookup's fid comes from here. Same NTMP mechanism as the FDB.
        //
        // ⭐ FOUND IN ONE RUN BECAUSE THE MODEL REFUSED TO LIE. imxrt1180-netc-fdb
        // prints a single cumulative verdict, so the console said only "FDB/VLAN/
        // learning mismatch" -- one bit for six stages. What named the stage was the
        // model's own warning: "NTMP command for unmodelled table 18 ... returning
        // kNETC_InvTableID rather than a silent success." An ack with no table behind
        // it would have let the driver's spin exit and every query return nothing,
        // and the failure would have looked like a broken FDB instead of an absent
        // VLAN table. Declining honestly is what made six stages debuggable in one run.
        //
        // Layouts compiler-verified by the oracle from netc_tb_vf_{req,rsp}_data_t:
        //   request  (24B): header@0; entryID|keye.vid@4 [11:0]; cfge@8 = 16 raw
        //                   bytes (portMembership@8 [23:0], fid@12 [11:0],
        //                   etaBitmap@16, baseETEID@20)
        //   response (28B): status@0, entryID@4, keye.vid@8, cfge@12 (16 bytes)
        private uint VlanFilterOperation(uint command, uint access, ulong requestAddress, ref ushort matched)
        {
            matched = 0;
            var request = sysbus.ReadBytes(requestAddress, VlanRequestSize);
            var key = BitConverter.ToUInt32(request, 4);
            VlanEntry entry = null;

            if((command & NtmpCommandAdd) != 0)
            {
                var vid = (ushort)(key & 0xFFF);
                entry = FindVlanByVid(vid);
                if(entry == null)
                {
                    for(var i = 0; i < VlanTableSize && entry == null; i++)
                    {
                        if(!vlan[i].Valid) { entry = vlan[i]; }
                    }
                    if(entry == null)
                    {
                        return NtmpErrorSize;              // table full -- honest fault
                    }
                    entry.Vid = vid;
                    entry.Valid = true;
                    entry.EntryId = vlanNextId++;
                }
                for(var i = 0; i < 4; i++)
                {
                    entry.Cfge[i] = BitConverter.ToUInt32(request, 8 + i * 4);
                }
                matched = 1;
            }
            else if((command & NtmpCommandUpdate) != 0)
            {
                entry = access == NtmpAccessEntryId ? FindVlanByEntryId(key) : FindVlanByVid((ushort)(key & 0xFFF));
                if(entry != null)
                {
                    for(var i = 0; i < 4; i++)
                    {
                        entry.Cfge[i] = BitConverter.ToUInt32(request, 8 + i * 4);
                    }
                    matched = 1;
                }
            }

            if((command & NtmpCommandQuery) != 0)
            {
                if(entry == null)
                {
                    entry = access == NtmpAccessEntryId ? FindVlanByEntryId(key) : FindVlanByVid((ushort)(key & 0xFFF));
                }
                if(entry != null)
                {
                    var response = new byte[VlanResponseSize];
                    Array.Copy(BitConverter.GetBytes(entry.EntryId), 0, response, 4, 4);
                    Array.Copy(BitConverter.GetBytes((uint)(entry.Vid & 0xFFF)), 0, response, 8, 4);
                    for(var i = 0; i < 4; i++)
                    {
                        Array.Copy(BitConverter.GetBytes(entry.Cfge[i]), 0, response, 12 + i * 4, 4);
                    }
                    sysbus.WriteBytes(response, requestAddress);
                    matched = 1;
                }
            }

            if((command & NtmpCommandDelete) != 0)
            {
                var victim = access == NtmpAccessEntryId ? FindVlanByEntryId(key) : FindVlanByVid((ushort)(key & 0xFFF));
                if(victim != null)
                {
                    victim.Valid = false;
                    matched = 1;
                }
            }
            return NtmpErrorNone;
        }

        private VlanEntry FindVlanByVid(ushort vid)
        {
            for(var i = 0; i < VlanTableSize; i++)
            {
                if(vlan[i].Valid && vlan[i].Vid == vid) { return vlan[i]; }
            }
            return null;
        }

        private VlanEntry FindVlanByEntryId(uint id)
        {
            for(var i = 0; i < VlanTableSize; i++)
            {
                if(vlan[i].Valid && vlan[i].EntryId == id) { return vlan[i]; }
            }
            return null;
        }

        private class VlanEntry
        {
            public bool Valid;
            public ushort Vid;
            public uint EntryId;
            public uint[] Cfge = new uint[4];
        }

        private uint FdbOperation(uint command, uint access, ulong requestAddress, ref ushort matched)
        {
            var request = sysbus.ReadBytes(requestAddress, 48);
            FdbEntry entry = null;
            matched = 0;

            if((command & NtmpCommandAdd) != 0)
            {
                var fid = (ushort)(BitConverter.ToUInt32(request, 12) & 0xFFF);
                entry = FindByKey(request, 4, fid);
                if(entry == null)
                {
                    foreach(var e in fdb)
                    {
                        if(!e.Valid) { entry = e; break; }
                    }
                    if(entry == null)
                    {
                        return NtmpErrorSize;                  // table full -- honest fault
                    }
                    Array.Copy(request, 4, entry.Mac, 0, 6);
                    entry.Fid = fid;
                    entry.Valid = true;
                    entry.EntryId = nextEntryId++;
                }
                entry.PortBitmap = BitConverter.ToUInt32(request, 36) & 0xFFFFFF;
                entry.CfgeFlags = BitConverter.ToUInt32(request, 40);
                entry.EtEid = BitConverter.ToUInt32(request, 44);
                entry.Dynamic = ((entry.CfgeFlags >> 11) & 1) != 0;
                matched = 1;
            }
            else if((command & NtmpCommandUpdate) != 0)
            {
                entry = access == NtmpAccessEntryId
                    ? FindById(BitConverter.ToUInt32(request, 4))
                    : FindByKey(request, 4, (ushort)(BitConverter.ToUInt32(request, 12) & 0xFFF));
                if(entry != null)
                {
                    entry.PortBitmap = BitConverter.ToUInt32(request, 36) & 0xFFFFFF;
                    entry.CfgeFlags = BitConverter.ToUInt32(request, 40);
                    entry.EtEid = BitConverter.ToUInt32(request, 44);
                    entry.Dynamic = ((entry.CfgeFlags >> 11) & 1) != 0;
                    matched = 1;
                }
            }

            if((command & NtmpCommandQuery) != 0 && access == NtmpAccessSearch)
            {
                // Search by criteria (SWT_BridgeSearchFDBTableEntry): return the first
                // valid entry AFTER resumeEntryId matching the requested elements; the
                // response status carries the resume point for the next page.
                var resume = BitConverter.ToUInt32(request, 4);
                var keyCriteria = request[33] & 0x3;
                var cfgCriteria = request[34] & 0x7;
                var searchFid = (ushort)(BitConverter.ToUInt32(request, 16) & 0xFFF);
                var searchPort = BitConverter.ToUInt32(request, 20) & 0xFFFFFF;
                var searchDynamic = ((BitConverter.ToUInt32(request, 24) >> 11) & 1) != 0;

                FdbEntry found = null;
                foreach(var c in fdb)
                {
                    if(!c.Valid) { continue; }
                    if(resume != 0xFFFFFFFF && c.EntryId <= resume) { continue; }
                    if((cfgCriteria & FdbCriteriaPortBitmap) != 0 && c.PortBitmap != searchPort) { continue; }
                    if((cfgCriteria & FdbCriteriaDynamic) != 0 && c.Dynamic != searchDynamic) { continue; }
                    if((keyCriteria & FdbCriteriaFid) != 0 && c.Fid != searchFid) { continue; }
                    found = c; break;
                }
                if(found != null)
                {
                    WriteFdbResponse(requestAddress, found, found.EntryId);
                    matched = 1;
                }
            }
            else if((command & NtmpCommandQuery) != 0)
            {
                if(entry == null)
                {
                    entry = access == NtmpAccessEntryId
                        ? FindById(BitConverter.ToUInt32(request, 4))
                        : FindByKey(request, 4, (ushort)(BitConverter.ToUInt32(request, 12) & 0xFFF));
                }
                if(entry != null)
                {
                    WriteFdbResponse(requestAddress, entry, 0);
                    matched = 1;
                }
            }

            if((command & NtmpCommandDelete) != 0)
            {
                var victim = access == NtmpAccessEntryId
                    ? FindById(BitConverter.ToUInt32(request, 4))
                    : FindByKey(request, 4, (ushort)(BitConverter.ToUInt32(request, 12) & 0xFFF));
                if(victim != null)
                {
                    victim.Valid = false;
                    matched = 1;
                }
            }
            return NtmpErrorNone;
        }

        // status@0 (search resume point), entryID@4, keye.macAddr@8, keye.fid@16,
        // cfge.portBitmap@20, cfge flags@24, cfge.etEID@28.
        private void WriteFdbResponse(ulong address, FdbEntry e, uint status)
        {
            var response = new byte[36];
            Array.Copy(BitConverter.GetBytes(status), 0, response, 0, 4);
            Array.Copy(BitConverter.GetBytes(e.EntryId), 0, response, 4, 4);
            Array.Copy(e.Mac, 0, response, 8, 6);
            Array.Copy(BitConverter.GetBytes((uint)(e.Fid & 0xFFF)), 0, response, 16, 4);
            Array.Copy(BitConverter.GetBytes(e.PortBitmap), 0, response, 20, 4);
            Array.Copy(BitConverter.GetBytes(e.CfgeFlags), 0, response, 24, 4);
            Array.Copy(BitConverter.GetBytes(e.EtEid), 0, response, 28, 4);
            sysbus.WriteBytes(response, address);
        }

        private class FdbEntry
        {
            public bool Valid;
            public bool Dynamic;
            public byte[] Mac = new byte[6];
            public ushort Fid;
            public uint PortBitmap;
            public uint CfgeFlags;
            public uint EtEid;
            public uint EntryId;
        }

        private static bool IsPciFlr(long offset)
        {
            foreach(var b in PciConfigHeaderBases)
            {
                if(offset == b + PciDeviceControlOffset)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsPortMacCommandConfig(long offset)
        {
            foreach(var b in EthernetLinkMacBases)
            {
                if(offset == b + PortMac0CommandConfig || offset == b + PortMac1CommandConfig)
                {
                    return true;
                }
            }
            return false;
        }

        private readonly IBusController sysbus;
        private readonly FdbEntry[] fdb;
        private uint nextEntryId;
        // ===== RX FRAME PATH (ENETC0 SI0 BDR[0]) ==============================
        //
        // Anchors from @rt1180emulator -- what fsl_netc_endpoint POLLS:
        //   RBMR   0x8100  RX ring mode, EN = bit31
        //   RBCIR  0x810C  RX CONSUMER index -- SOFTWARE writes it to FREE a BD
        //   RBBAR0 0x8110 / RBBAR1 0x8114   ring base (lo/hi)
        //   RBPIR  0x8118  RX PRODUCER index -- HARDWARE advances it on a frame
        //   RBLENR 0x8120  number of BDs
        // RX BD writeback: byte15 bit6 READY (0x40), bit7 FINAL (0x80).
        //
        // ⭐ THERE IS NO "FRAME READY" STATUS BIT. The driver watches RBPIR advance
        // past its own consumer index; THE INDEX DELTA *IS* THE SIGNAL. So delivery
        // means: copy the frame into the buffer the posted BD names, stamp the BD
        // writeback, advance RBPIR, raise the RX MSI-X. Anything less is a frame
        // that arrived and cannot be noticed.
        //
        // ⭐⭐ READY MEANS "THE GUEST ENABLED THE RING", NOT "THE GUEST SIZED IT".
        // Gating on RBLENR != 0 is too early by several steps: the driver writes
        // RBBAR/RBPIR/RBCIR/RBLENR, THEN posts a buffer address into every
        // descriptor, and only then sets RBMR.EN. Accepting frames at RBLENR means
        // DMAing into descriptors whose addr is still 0 -- i.e. into guest address
        // zero -- and stamping them READY over the driver's half-finished setup.
        // @rt1180emulator measured exactly that on a 3-node lab: a node joining a
        // live segment took 88 such frames and still printed PASS, reporting
        // "peers" whose source MAC was its own. EN is the guest's own statement
        // that the buffers are posted; believe that and nothing else.
        public bool CanReceive => (regs[Enetc0RxMode / 4] & RxModeEnable) != 0;

        public MACAddress MAC { get; set; } = MACAddress.Parse("02:00:00:00:00:01");

        public event Action<EthernetFrame> FrameReady;

        // ⭐ PORTS ARE REGISTERED, NOT ASSUMED. A switch with one MAC cannot express
        // "flooded out 0 and 2 but not 1" -- the assertion netc-flood makes. Each wire
        // port is its own connectable node and registers itself here.
        public void RegisterPort(int index, IMXRT1180_NETC_Port port)
        {
            if(index < 0 || index >= SwitchWirePorts || index == SwitchCpuPort)
            {
                this.Log(LogLevel.Error, "refusing to register port {0}: out of range or the CPU port", index);
                return;
            }
            wirePorts[index] = port;
            this.Log(LogLevel.Info, "wire port {0} registered", index);
        }

        // Ingress from a specific wire port. The port index is the whole point: it is
        // what learning binds the source MAC to and what split-horizon removes from the
        // egress set.
        public void ReceiveOnPort(int portIndex, EthernetFrame frame)
        {
            ReceiveFrameFrom(portIndex, frame);
        }

        public void ReceiveFrame(EthernetFrame frame)
        {
            // The block's own IMACInterface stays wired for the single-wire tests
            // (netc-rxfwd's echo): treat it as the default wire port.
            ReceiveFrameFrom(SwitchWireIngressPort, frame);
        }

        private void ReceiveFrameFrom(int ingressPort, EthernetFrame frame)
        {
            var bytes = frame.Bytes;
            if(bytes == null || bytes.Length < 14)
            {
                return;
            }
            // ⭐⭐⭐ THE RBMR.EN GATE BELONGS TO CPU DELIVERY, NOT TO SWITCH INGRESS.
            // It used to sit HERE, dropping every frame unless the CPU had armed its
            // receive ring -- so wire-to-wire forwarding did not work at all, and
            // netc-flood reported "the broadcast did not reach both other wires".
            //
            // A SWITCH FORWARDS BETWEEN WIRE PORTS WHETHER OR NOT THE HOST IS
            // LISTENING. The gate itself is right and hard-won (@rt1180emulator
            // measured 88 frames DMA'd into guest address zero by gating on RBLENR
            // instead of EN) -- I took a correct rule and applied it ONE SCOPE TOO
            // WIDE. A guard in the wrong place is not a weaker guard, it is a
            // different behaviour: here it silently turned a switch into a host NIC.
            // The check now lives in DeliverToHost, where the ring actually matters.

            // A frame ingressing a WIRE port teaches the switch its source, then the
            // FDB decides where it goes. It reaches the CPU only if the destination
            // resolves to the management port -- split-horizon already removes the
            // ingress wire port, so a unicast whose only egress is the wire it came
            // from is dropped, which is what imxrt1180-netc-rxfwd asserts.
            Learn(bytes, 6, SwitchDefaultFid, ingressPort);
            var egress = SwitchEgressBitmap(bytes, SwitchDefaultFid, ingressPort);
            for(var port = 0; port < SwitchWirePorts; port++)
            {
                if(port != SwitchCpuPort && (egress & (1u << port)) != 0)
                {
                    CountEgressFrame(port, (uint)bytes.Length);
                    SendOnPort(port, bytes);        // wire-to-wire forwarding
                }
            }
            if((egress & (1u << SwitchCpuPort)) == 0)
            {
                return;                        // not for the CPU: correctly dropped
            }
            DeliverToHost(bytes);
        }

        private void DeliverToHost(byte[] bytes)
        {
            // EN is the guest's own statement that the descriptors carry buffers.
            if(!CanReceive)
            {
                this.Log(LogLevel.Debug, "RX ring not enabled (RBMR.EN clear); not delivering to the host");
                return;
            }
            var length = regs[Enetc0RxLength / 4] & RxRingLengthMask;
            if(length == 0)
            {
                return;
            }
            var ringBase = (ulong)regs[Enetc0RxBaseAddress0 / 4] | ((ulong)regs[Enetc0RxBaseAddress1 / 4] << 32);
            var producer = regs[Enetc0RxProducerIndex / 4] & 0xFFFF;
            var consumer = regs[Enetc0RxConsumerIndex / 4] & 0xFFFF;

            var next = (producer + 1) % length;
            if(next == consumer)
            {
                this.Log(LogLevel.Warning, "RX ring full (pir={0} cir={1}); dropping frame", producer, consumer);
                return;
            }

            var bdAddress = ringBase + (ulong)producer * RxBdSize;
            var bd = sysbus.ReadBytes(bdAddress, RxBdSize);
            var bufferAddress = BitConverter.ToUInt64(bd, 0);
            if(bufferAddress == 0)
            {
                // The descriptor has no buffer posted. Refuse rather than DMA to 0.
                this.Log(LogLevel.Warning, "RX BD {0} has no buffer address posted; dropping frame", producer);
                return;
            }

            sysbus.WriteBytes(bytes, bufferAddress);

            // ⭐⭐ THE RECEIVED LENGTH GOES AT BYTE 8, NOT BYTE 12.
            // It was written at 12 here. Everything else about this path was
            // right -- the frame was DMA'd into the posted buffer, the BD was
            // stamped READY|FINAL, the producer index advanced, the MSI-X was
            // emitted -- so the model looked, from every angle it reports on,
            // like it had delivered a frame. The driver read bufLen from +8,
            // got 0, and never freed the descriptor; RBCIR stayed at 0 while
            // RBPIR climbed to 7, and the ring wedged FULL after exactly one
            // ring's worth of frames. MEASURED: "RX ring full (pir=7 cir=0)"
            // repeating forever while the node's console stayed silent -- the
            // node was deaf to every peer on the segment.
            //
            // SOURCED -- hw/net/imxrt1180_netc.c, netc_deliver_rx():
            //     stw_le_p(wb + 8, len);                  /* writeback.bufLen */
            //     wb[14] = 0;                             /* error */
            //     wb[15] = RXBD_WB_READY | RXBD_WB_FINAL; /* isReady | isFinal */
            //
            // ⚠ AGAIN NO EXISTING TEST COULD SEE IT. netc-rxfwd and friends
            // deliver a handful of frames and assert on a counter; a ring only
            // wedges once it WRAPS, which needs more frames than any of them
            // send. A four-byte offset error stayed invisible until a peer put
            // sustained traffic on the wire -- which is the entire argument for
            // running this model on a live segment instead of a fixture.
            var writeback = new byte[RxBdSize];
            Array.Copy(BitConverter.GetBytes((ushort)bytes.Length), 0, writeback, 8, 2);
            writeback[14] = 0;                       // error
            writeback[15] = RxBdReady | RxBdFinal;   // isReady | isFinal
            sysbus.WriteBytes(writeback, bdAddress);

            regs[Enetc0RxProducerIndex / 4] = next;      // the index delta IS the signal
            EmitMsix(Enetc0MsixTable, regs[Enetc0RxRingVector0 / 4]);
        }

        // Frames the switch sends OUT of a wire port go to whatever backend is
        // attached; with no backend this is a no-op, which is why the counter-based
        // tests (netc-fwd) work with no wire at all.
        private void SendOnPort(int port, byte[] bytes)
        {
            if(wirePorts[port] == null)
            {
                this.Log(LogLevel.Debug, "egress to port {0}: no wire node registered", port);
                return;
            }
            this.Log(LogLevel.Info, "egress {0} bytes on wire port {1}", bytes.Length, port);
            wirePorts[port].SendToWire(bytes);
        }

        private readonly IMXRT1180_NETC_Port[] wirePorts = new IMXRT1180_NETC_Port[SwitchWirePorts];

        private void EmitToWire(byte[] bytes)
        {
            var handler = FrameReady;
            if(handler == null)
            {
                return;
            }
            if(EthernetFrame.TryCreateEthernetFrame(bytes, true, out var frame))
            {
                handler(frame);
            }
        }

        // ===== PTP 1588 timer (TMR0 @ NETC + 0xB80000) ========================
        //
        // ⭐ A DIGITAL DDS, NOT A COUNTER THAT TICKS. Each reference-clock tick the
        // addend accumulates and the nanosecond counter advances by addend/2^32 ns;
        // the driver tunes frequency by scaling the addend. Modelled the way the
        // oracle models it (hw/net/imxrt1180_netc.c): derive the count from VIRTUAL
        // TIME rather than from a periodic callback --
        //
        //     count = base + elapsed_virtual_ns * (addend / nominal)
        //
        // -- because a callback-driven counter advances at the callback's rate, not
        // the addend's, and imxrt1180-netc-ptp's second assertion is precisely that
        // DOUBLING THE ADDEND DOUBLES THE ADVANCE over the same delay. A "the clock
        // ticks" model passes assertion 1 and fails assertion 2, which is the same
        // single-operating-point blind spot the SAI and ASRC both had.
        //
        // The FIRST addend written is taken as the rate-1 nominal, so later writes
        // are relative to whatever the driver first called nominal.
        private bool PtpRead(long offset, out uint value)
        {
            switch(offset)
            {
                case TmrCurL:
                {
                    var t = PtpCurrentTime();
                    ptpCurHiLatch = (uint)(t >> 32);      // reading _L latches _H
                    value = (uint)t;
                    return true;
                }
                case TmrCurH:
                    value = ptpCurHiLatch;
                    return true;
                case TmrCntL:
                    value = (uint)PtpRaw();
                    return true;
                case TmrCntH:
                    value = (uint)(PtpRaw() >> 32);
                    return true;
                default:
                    value = 0;
                    return false;
            }
        }

        private bool PtpWrite(long offset, uint value)
        {
            switch(offset)
            {
                case TmrCtrl:
                    PtpRelatch();                          // freeze with the OLD config
                    regs[offset / 4] = value;
                    ptpEnabled = (value & TmrCtrlTe) != 0;
                    return true;
                case TmrAdd:
                    PtpRelatch();
                    regs[offset / 4] = value;
                    if(ptpNominalAddend == 0)
                    {
                        ptpNominalAddend = PtpAddend();    // first addend == rate 1
                    }
                    return true;
                case TmrCntL:
                case TmrCntH:
                    regs[offset / 4] = value;              // software sets the counter
                    ptpCountBase = ((ulong)regs[TmrCntH / 4] << 32) | regs[TmrCntL / 4];
                    ptpTimeBaseNs = NowNanoseconds();
                    return true;
                default:
                    return false;
            }
        }

        // Full 64-bit addend = TCLK_PERIOD (TMR_CTRL[25:16]) << 32 | TMR_ADD.
        private ulong PtpAddend()
        {
            var tclk = (regs[TmrCtrl / 4] & TmrCtrlTclkPeriodMask) >> 16;
            return ((ulong)tclk << 32) | regs[TmrAdd / 4];
        }

        private ulong PtpRaw()
        {
            if(!ptpEnabled)
            {
                return ptpCountBase;                       // frozen while disabled
            }
            var now = NowNanoseconds();
            var dt = now > ptpTimeBaseNs ? now - ptpTimeBaseNs : 0UL;
            var addend = PtpAddend();
            if(ptpNominalAddend == 0 || addend == ptpNominalAddend)
            {
                return ptpCountBase + dt;                  // rate 1.0, exactly
            }
            return ptpCountBase + (ulong)((double)dt * addend / ptpNominalAddend);
        }

        private ulong PtpCurrentTime()
        {
            var off = (long)(((ulong)regs[TmrOffH / 4] << 32) | regs[TmrOffL / 4]);
            return PtpRaw() + (ulong)off;
        }

        // Freeze the running count and re-origin the clock, so a config change
        // (enable/disable, addend, counter set) takes effect from NOW and never
        // retroactively rescales time already elapsed.
        private void PtpRelatch()
        {
            ptpCountBase = PtpRaw();
            ptpTimeBaseNs = NowNanoseconds();
        }

        private ulong NowNanoseconds()
        {
            return (ulong)(machine.ElapsedVirtualTime.TimeElapsed.TotalMicroseconds * 1000.0);
        }

        private readonly IMachine machine;
        private ulong ptpCountBase;
        private ulong ptpTimeBaseNs;
        private ulong ptpNominalAddend;
        private uint ptpCurHiLatch;
        private bool ptpEnabled;

        private const long Tmr0Offset = 0xB80000;
        private const long TmrCtrl  = Tmr0Offset + 0x80;
        private const long TmrCntL  = Tmr0Offset + 0x98;
        private const long TmrCntH  = Tmr0Offset + 0x9C;
        private const long TmrAdd   = Tmr0Offset + 0xA0;
        private const long TmrOffL  = Tmr0Offset + 0xB0;
        private const long TmrOffH  = Tmr0Offset + 0xB4;
        private const long TmrCurL  = Tmr0Offset + 0xF0;
        private const long TmrCurH  = Tmr0Offset + 0xF4;
        private const uint TmrCtrlTe = 0x4;
        private const uint TmrCtrlTclkPeriodMask = 0x3FF0000;

        private readonly uint[] regs;
        private readonly ushort[] phyRegisters;
        private bool loggedScope;

        // Region-relative block bases, from the oracle's netc_pci_hdrs[] and
        // netc_eth_link_bases[].
        private static readonly long[] PciConfigHeaderBases =
            { 0x0000, 0x1000, 0x2000, 0x3000, 0x4000, 0xF8000, 0x100000 };
        private static readonly long[] EthernetLinkMacBases =
            { 0xA05000, 0xA09000, 0xA0D000, 0xA11000, 0xA15000,   // SW0 ports 0..4
              0xB15000,                                            // ENETC0 MAC
              0xB55000 };                                          // ENETC1 MAC

        private const long PciDeviceControlOffset  = 0x48;
        private const uint PciInitFlr              = 0x8000;
        private const long PortMac0CommandConfig   = 0x008;
        private const long PortMac1CommandConfig   = 0x408;
        private const uint PortMacSoftwareReset    = 0x4000000;

        private const long NetcPrivateBase   = 0x900000;
        private const long NetcResetRegister  = NetcPrivateBase + 0x100;   // SR=1, LOCK=2
        private const long NetcStatusRegister  = NetcPrivateBase + 0x104;
        private const uint NetcResetSoftwareReset = 0x1;

        private const long Enetc0Base    = 0xB10000;
        private const long Enetc0Si0Base = 0xB00000;
        private const long Enetc1Base    = 0xB50000;
        private const long Enetc1Si0Base = 0xB40000;
        private const long Sw0Base       = 0xA00000;

        // EMDIO controller, region offset 0xBA0000 + struct offset 0x1C00.
        private const long EmdioBase    = 0xBA0000;
        private const long EmdioConfig  = EmdioBase + 0x1C00;   // BSY1=bit31, BSY2=bit0
        private const long EmdioControl = EmdioBase + 0x1C04;   // READ=bit15, DEV=[4:0]
        private const long EmdioData    = EmdioBase + 0x1C08;
        private const uint EmdioBusy       = 0x80000001;
        private const uint EmdioRead       = 0x8000;
        private const uint EmdioDeviceMask = 0x1F;

        private const int  NumberOfPhyRegisters = 32;
        private const ushort PhyControlReset = 0x8000;
        private const uint PhyStatusValue    = 0x782D;   // link-up | autoneg-complete | caps
        private const uint PhyId1Value       = 0x001C;   // RTL8201: (ID1<<16)|ID2 == 0x001CC816
        private const uint PhyId2Value       = 0xC816;
        private const int  PhyRtl8211fStatus = 0x1A;
        private const uint PhyRtl8211fStatusValue = 0x002C;

        private const long Enetc0Capability0 = Enetc0Base + 0x0;
        private const long Enetc0Capability1 = Enetc0Base + 0x4;
        private const long Enetc0Capability2 = Enetc0Base + 0x8;
        private const long Enetc1Capability1 = Enetc1Base + 0x4;
        private const long Enetc1Capability2 = Enetc1Base + 0x8;
        private const long Enetc0SiCapability1 = Enetc0Si0Base + 0x24;
        private const long Enetc1SiCapability1 = Enetc1Si0Base + 0x24;
        private const long Enetc1SwitchMgmtCapability = Enetc1Base + 0x800;

        // NUM_MSIX=6, NUM_VSI=0 | NUM_TX_BDR=8, NUM_RX_BDR=8 | NUM_MSIX=6 | SM=1
        private const uint Capability1Value        = 6u << 12;
        private const uint Capability2Value        = (8u << 0) | (8u << 16);
        private const uint SiCapability1Value      = 6u << 12;
        private const uint SwitchManagementPresent = 0x1;

        // Switch management TX ring (ENETC1 SI0, BDR[0]).
        private const long MgmtTxBaseAddress0 = Enetc1Si0Base + 0x8010;
        private const long MgmtTxBaseAddress1 = Enetc1Si0Base + 0x8014;
        private const long MgmtTxProducerIndex = Enetc1Si0Base + 0x8018;   // doorbell
        private const long MgmtTxConsumerIndex = Enetc1Si0Base + 0x801C;
        private const long MgmtTxLength        = Enetc1Si0Base + 0x8020;
        private const uint BdRingLengthMask    = 0x1FFF8;
        private const int  TxBdSize            = 16;
        private const uint TxBdWritebackWritten = 0x04000000;              // dword[1] bit 26
        private const uint TxDescriptorSwitchMgmtSendOption = 1u << 23;
        private const int  TxDescriptorPortShift = 16;
        private const uint TxDescriptorPortMask  = 0x1F;
        private const uint MaximumFrameLength    = 2048;

        // Switch command BD rings.
        private const long SwitchCbdrBase   = Sw0Base + 0x800;
        private const long SwitchCbdrStep   = 0x30;
        private const int  SwitchCommandRings = 4;
        private const long CbdrMode          = 0x00;
        private const long CbdrBaseAddress0  = 0x10;
        private const long CbdrBaseAddress1  = 0x14;
        private const long CbdrProducerIndex = 0x18;
        private const long CbdrConsumerIndex = 0x1C;
        private const long CbdrLength        = 0x20;
        private const uint CbdrModeEnable      = 0x80000000;
        private const uint CbdrLengthMask      = 0x7F8;
        private const uint CbdrIndexMask       = 0x3FF;
        private const uint CbdrBaseAddressMask = 0xFFFFFF80;

        private const int  NtmpBdSize     = 32;
        private const uint NtmpTableFdb   = 15;
        private const uint NtmpCommandDelete = 0x1;
        private const uint NtmpCommandUpdate = 0x2;
        private const uint NtmpCommandQuery  = 0x4;
        private const uint NtmpCommandAdd    = 0x8;
        private readonly VlanEntry[] vlan;
        private uint vlanNextId = 1;
        private const int VlanTableSize = 64;
        private const int VlanRequestSize = 24;
        private const int VlanResponseSize = 28;
        private const uint NtmpTableVlanFilter = 18;

        private const uint NtmpAccessEntryId = 0;
        private const uint NtmpAccessSearch  = 2;
        private const uint NtmpErrorNone         = 0x00;
        private const uint NtmpErrorSize         = 0x02;   // table full
        private const uint NtmpErrorInvalidTable = 0x80;
        private const int  FdbCriteriaFid        = 0x1;
        private const int  FdbCriteriaDynamic    = 0x1;
        private const int  FdbCriteriaPortBitmap = 0x2;
        private const int  FdbSize = 64;
        private const ushort SwitchDefaultFid = 0;
        private const int SwitchCpuPort   = 4;
        private const int SwitchWirePorts = 5;
        private const uint SwitchAllPorts = (1u << SwitchWirePorts) - 1;

        // Endpoint TX ring (ENETC1 SI ring 1) -- EP_SendFrame's ring.
        // ENETC0 SI0 ring 0 TX -- offsets from the oracle's anchors and confirmed
        // against imxrt1180-netc-fdb's own defines (TB_BAR0 @ 0x60B0_8010 ..).
        // RX ring (ENETC0 SI0 BDR[0]) -- offsets from the oracle's anchors.
        private const long Enetc0RxMode          = Enetc0Si0Base + 0x8100;
        private const long Enetc0RxConsumerIndex = Enetc0Si0Base + 0x810C;
        private const long Enetc0RxBaseAddress0  = Enetc0Si0Base + 0x8110;
        private const long Enetc0RxBaseAddress1  = Enetc0Si0Base + 0x8114;
        private const long Enetc0RxProducerIndex = Enetc0Si0Base + 0x8118;
        private const long Enetc0RxLength        = Enetc0Si0Base + 0x8120;
        private const long Enetc0RxRingVector0   = Enetc0Si0Base + 0xB80;   // SIMSIRRVR0
        // ENETC0PSI0's MSI-X table. SOURCED from the oracle's
        // hw/net/imxrt1180_netc.c: "#define NETC_MSIX_TABLE 0xBF0000", used as
        // netc_emit_msix_tbl(s, NETC_MSIX_TABLE, ...) for the ENETC0 path -- NOT the
        // 0xC00000 ENETC1 table this model already had. Looked up rather than
        // guessed: a fabricated table base would write the MSI-X payload to a
        // plausible-looking wrong address and the interrupt would simply never
        // arrive, which is indistinguishable from "no frame came".
        private const long Enetc0MsixTable       = 0xBF0000;
        private const uint RxModeEnable   = 1u << 31;
        private const uint RxRingLengthMask = 0x1FFF8;
        private const int  RxBdSize = 16;
        private const byte RxBdReady = 0x40;
        private const byte RxBdFinal = 0x80;
        private const int  SwitchWireIngressPort = 0;

        private const long Enetc0TxBaseAddress0  = Enetc0Si0Base + 0x8010;
        private const long Enetc0TxBaseAddress1  = Enetc0Si0Base + 0x8014;
        private const long Enetc0TxProducerIndex = Enetc0Si0Base + 0x8018;   // doorbell = GO
        private const long Enetc0TxConsumerIndex = Enetc0Si0Base + 0x801C;
        private const long Enetc0TxLength        = Enetc0Si0Base + 0x8020;
        // SIMSITRVR0 -- ENETC0 TX ring 0's MSI-X entry index. Read from the oracle
        // (hw/net/imxrt1180_netc.c:173), same 0xB00 offset ENETC1 uses in its own
        // SI window, which is the cross-check that it is a layout and not a
        // coincidence.
        private const long Enetc0TxRingVector0   = Enetc0Si0Base + 0xB00;

        private const long EndpointTxBaseAddress0  = Enetc1Si0Base + 0x8210;
        private const long EndpointTxBaseAddress1  = Enetc1Si0Base + 0x8214;
        private const long EndpointTxProducerIndex = Enetc1Si0Base + 0x8218;
        private const long EndpointTxConsumerIndex = Enetc1Si0Base + 0x821C;
        private const long EndpointTxLength        = Enetc1Si0Base + 0x8220;
        private const long Enetc1TxRingVector1     = Enetc1Si0Base + 0xB04;

        // Port MAC statistics the demo polls to see where a frame egressed.
        private const long PortMacTxFramesOk  = 0x220;   // PMn TX frames-OK
        private const long PortMacTx512To1023 = 0x290;   // PMn_T1023N

        // MSI-X tables. ENETC1PSI0's is at region 0xC00000; the ring->entry index
        // lives in SIMSITRVRn.
        private const long Enetc1MsixTable    = 0xC00000;
        private const long Enetc1TxRingVector0 = Enetc1Si0Base + 0xB00;
        private const uint MsixControlMask     = 0x1;
    }

    public class IMXRT1180_NETC_Port : IMACInterface, IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_NETC_Port(IMachine machine, int portIndex = 0)
        {
            PortIndex = portIndex;
            MAC = MACAddress.Parse(string.Format("02:00:00:00:01:{0:X2}", portIndex));
        }

        public int PortIndex { get; }

        // Wired from the .repl. Setting it REGISTERS the port with the switch -- the
        // link has to be two-way or the switch can accept ingress from a port it
        // cannot egress to, which would half-work and look like a forwarding bug.
        public IMXRT1180_NETC Switch
        {
            get => switchReference;
            set
            {
                switchReference = value;
                value?.RegisterPort(PortIndex, this);
            }
        }

        private IMXRT1180_NETC switchReference;

        public MACAddress MAC { get; set; }

        public event Action<EthernetFrame> FrameReady;

        // From the wire into the switch, tagged with the port it arrived on -- the
        // ingress port is what split-horizon needs and what learning binds the source
        // MAC to.
        public void ReceiveFrame(EthernetFrame frame)
        {
            if(Switch == null)
            {
                this.Log(LogLevel.Warning, "port {0} received a frame but no switch is wired", PortIndex);
                return;
            }
            Switch.ReceiveOnPort(PortIndex, frame);
        }

        // From the switch out onto this port's wire. Called only for ports the
        // forwarding decision selected.
        public void SendToWire(byte[] bytes)
        {
            var handler = FrameReady;
            if(handler == null)
            {
                this.Log(LogLevel.Warning, "port {0}: nothing attached to this wire", PortIndex);
                return;
            }
            // Egress carries the frame as-is; adding a CRC here appended a second
            // one on every hop and corrupted the payload the harness byte-compares.
            if(EthernetFrame.TryCreateEthernetFrame(bytes, false, out var frame))
            {
                handler(frame);
            }
        }

        public void Reset()
        {
        }

        // No registers: the bus presence exists so `connector Connect` can name the
        // node, the same reason the echo wire has one.
        public long Size => 0x100;
        public uint ReadDoubleWord(long offset) { return 0; }
        public void WriteDoubleWord(long offset, uint value) { }
    }

}
