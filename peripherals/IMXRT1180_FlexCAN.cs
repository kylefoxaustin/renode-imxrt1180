//
// i.MX RT1180 FlexCAN — internal-loopback message-buffer path.
//
// Base FlexCAN1 0x443A0000. Scope is what imxrt1180-flexcan drives, and the whole
// test is one transaction:
//   MCR   0x000  MAXMB in [6:0]; MDIS/FRZ/HALT clear -> operational
//   CTRL1 0x004  LPB bit12 = internal loopback
//   IFLAG1 0x030 per-MB interrupt flags, W1C
//   MB n:  CS @ 0x80 + n*0x10, ID +4, DATA0 +8, DATA1 +12
//          CS[31:24] = CODE. 0x4 = RX_EMPTY (armed), 0x2 = RX_FULL,
//                             0xC = TX_DATA (writing this TRANSMITS)
//
// ⭐ THE CS WRITE IS THE TRIGGER, NOT A STATUS UPDATE. Writing CODE=TX_DATA to a
// mailbox's CS is what puts the frame on the bus -- the data and ID registers are
// staged beforehand and mean nothing until that write lands. A model that treats CS
// as ordinary storage accepts the whole setup and transmits nothing, which is this
// project's recurring shape: the registers all read back correctly and no frame
// exists. Here the act and its effect are the same write.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.CAN
{
    public class IMXRT1180_FlexCAN : IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public IMXRT1180_FlexCAN(IMachine machine)
        {
            regs = new uint[Size / 4];
            Connections = new Dictionary<int, IGPIO> { { 0, new GPIO() } };
            Reset();
        }

        public long Size => 0x4000;
        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);
            // Reset: module disabled and frozen, so NOTRDY and FRZACK both read set --
            // which is what the driver expects to see before it brings the block up.
            regs[ModuleControl / 4] = ModuleDisable | Freeze | Halt | NotReady | FreezeAck;
        }

        public uint ReadDoubleWord(long offset)
        {
            return offset + 4 <= Size ? regs[offset / 4] : 0u;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset + 4 > Size)
            {
                return;
            }
            if(offset == InterruptFlags1)
            {
                regs[offset / 4] &= ~value;            // W1C
                UpdateInterrupt();
                return;
            }
            if(offset == ModuleControl)
            {
                // ⭐⭐ THE FREEZE HANDSHAKE IS NOT OPTIONAL, EVEN THOUGH THE TEST DOES
                // NOT POLL IT. @rt1180emulator flagged this explicitly: their smoke
                // test writes MCR directly and never reads FRZACK/NOTRDY, but
                // FLEXCAN_Init in the real fsl driver SPINS on them. A model built to
                // exactly what the test polls would pass this row and hang the SDK --
                // the single-operating-point trap one level up, where the collapsed
                // case is the TEST ITSELF rather than a parameter inside it.
                //
                // FRZACK follows FRZ&HALT; NOTRDY follows MDIS or freeze. Both are
                // status bits the guest never writes.
                var request = value & ~(FreezeAck | NotReady);
                var frozen = (request & Freeze) != 0 && (request & Halt) != 0;
                var disabled = (request & ModuleDisable) != 0;
                regs[offset / 4] = request
                    | (frozen ? FreezeAck : 0u)
                    | ((frozen || disabled) ? NotReady : 0u);
                return;
            }
            regs[offset / 4] = value;

            if(offset >= MessageBufferBase && offset < MessageBufferBase + MessageBufferCount * MessageBufferStride)
            {
                var index = (int)((offset - MessageBufferBase) / MessageBufferStride);
                var word = (offset - MessageBufferBase) % MessageBufferStride;
                if(word == 0 && ((value >> CodeShift) & 0xF) == CodeTransmitData)
                {
                    Transmit(index);
                }
            }
        }

        private void Transmit(int source)
        {
            var baseOffset = MessageBufferBase + source * MessageBufferStride;
            var id = regs[(baseOffset + 4) / 4];
            var data0 = regs[(baseOffset + 8) / 4];
            var data1 = regs[(baseOffset + 12) / 4];
            var length = (regs[baseOffset / 4] >> LengthShift) & 0xF;

            // The TX mailbox retires to INACTIVE once the frame is away.
            regs[baseOffset / 4] = (regs[baseOffset / 4] & ~(0xFu << CodeShift)) | (CodeTransmitInactive << CodeShift);

            if((regs[Control1 / 4] & Loopback) == 0)
            {
                // No loopback and no bus peer modelled: the frame leaves and nothing
                // receives it. Say so rather than silently delivering it to ourselves.
                this.Log(LogLevel.Debug, "MB{0} transmitted with loopback disabled and no bus peer; frame dropped", source);
                return;
            }

            // Internal loopback: deliver into the first mailbox armed RX_EMPTY that is
            // not the transmitter itself.
            for(var i = 0; i < MessageBufferCount; i++)
            {
                if(i == source)
                {
                    continue;
                }
                var target = MessageBufferBase + i * MessageBufferStride;
                if(((regs[target / 4] >> CodeShift) & 0xF) != CodeReceiveEmpty)
                {
                    continue;
                }
                regs[(target + 4) / 4] = id;
                regs[(target + 8) / 4] = data0;
                regs[(target + 12) / 4] = data1;
                regs[target / 4] = (CodeReceiveFull << CodeShift) | (length << LengthShift);
                regs[InterruptFlags1 / 4] |= 1u << i;
                UpdateInterrupt();
                return;
            }
            this.Log(LogLevel.Debug, "MB{0} transmitted in loopback but no mailbox was armed to receive", source);
        }

        private void UpdateInterrupt()
        {
            Connections[0].Set((regs[InterruptFlags1 / 4] & regs[InterruptMask1 / 4]) != 0);
        }

        private readonly uint[] regs;

        private const long ModuleControl   = 0x000;
        private const long Control1        = 0x004;
        private const long InterruptMask1  = 0x028;
        private const long InterruptFlags1 = 0x030;
        private const long MessageBufferBase   = 0x080;
        private const long MessageBufferStride = 0x10;
        private const int  MessageBufferCount  = 32;

        private const uint ModuleDisable = 1u << 31;
        private const uint Freeze        = 1u << 30;
        private const uint Halt          = 1u << 28;
        private const uint NotReady      = 1u << 27;
        private const uint FreezeAck     = 1u << 24;
        private const uint Loopback      = 1u << 12;
        private const int  CodeShift   = 24;
        private const int  LengthShift = 16;
        private const uint CodeReceiveEmpty      = 0x4;
        private const uint CodeReceiveFull       = 0x2;
        private const uint CodeTransmitData      = 0xC;
        private const uint CodeTransmitInactive  = 0x8;
    }
}
