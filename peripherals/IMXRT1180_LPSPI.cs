//
// i.MX RT1180 LPSPI — written because Renode's stock SPI.IMXRT_LPSPI IGNORES
// TCR[CONT], and CONT is the whole transaction.
//
// ⭐⭐⭐ RENODE DEFECT #15, MEASURED. imxrt1180-lpspi reads a JEDEC ID: it writes
// TCR = FRAMESZ8|CONT, clocks the command byte plus two dummies, then writes
// TCR = FRAMESZ8 (CONT cleared) for the last frame so chip-select drops. With the
// stock model the log reads:
//
//     lpspi4: Pushing a command: 0x00200007        <- CONT (bit 21) IS set
//     lpspi4: Starting a new SPI xfer, frame size: 1 bytes
//     lpspi4: Sending 0x9F to the device
//     lpspi4: Received response 0x0 from the device
//     lpspi4: SPI transfer not initialized
//     lpspi4: Starting a new SPI xfer, ...          <- a NEW xfer for byte 2
//     lpspi4_flash: Command decoding failed on byte: 0x0
//
// A new transfer per frame means chip-select is dropped between bytes, so the flash
// resets its command decoder and every byte after the opcode is decoded as a fresh
// command. The JEDEC ID can never come back. The guest was driving CONT correctly;
// the model simply did not look at it.
//
// ⭐ AND THE FAILURE IS INVISIBLE TO A SINGLE-BYTE TEST. Any SPI exchange that is
// one frame long works perfectly against the stock model -- CONT only matters when a
// transaction spans frames. Fifth instance tonight of the same shape: a parameter
// that never varies (here, frame count = 1) collapses two behaviours into one
// observation.
//
// Scope: what the oracle's three LPSPI tests poll, and no more.
//   CR    0x10  MEN bit0, RST bit1
//   SR    0x14  TDF bit0 (TX FIFO has room), RDF bit1 (RX FIFO has data)
//   IER   0x18 / DER 0x1C  TDDE bit0 / RDDE bit1 gate the DMA request lines
//   CFGR1 0x54  MASTER bit0
//   FCR   0x58  TXWATER [1:0], RXWATER [17:16]
//   TCR   0x60  FRAMESZ [11:0], PCS [25:24], CONT bit21
//   TDR   0x64  transmit data
//   RSR   0x70  RXEMPTY bit1
//   RDR   0x74  receive data
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.SPI
{
    public class IMXRT1180_LPSPI : SimpleContainer<ISPIPeripheral>, IDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput
    {
        public IMXRT1180_LPSPI(IMachine machine) : base(machine)
        {
            this.machine = machine;
            regs = new uint[Size / 4];
            rxFifo = new Queue<uint>();
            IRQ = new GPIO();
            Connections = new Dictionary<int, IGPIO>
            {
                { TransmitRequestLine, new GPIO() },
                { ReceiveRequestLine, new GPIO() },
            };
            Reset();
        }

        public long Size => 0x1000;
        public GPIO IRQ { get; }
        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        public override void Reset()
        {
            Array.Clear(regs, 0, regs.Length);
            rxFifo.Clear();
            chipSelectAsserted = false;
            foreach(var line in Connections.Values)
            {
                line.Unset();
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset + 4 > Size)
            {
                return 0;
            }
            switch(offset)
            {
                case Status:
                    // TDF: the TX path is synchronous here, so there is always room.
                    // RDF: set while the RX FIFO holds more than RXWATER entries.
                    return TransmitDataFlag | (ReceiveDataFlag ? ReceiveFlagBit : 0u);
                case ReceiveStatus:
                    return rxFifo.Count == 0 ? ReceiveEmpty : 0u;
                case ReceiveData:
                {
                    var value = rxFifo.Count > 0 ? rxFifo.Dequeue() : 0u;
                    RearmReceiveRequest();
                    return value;
                }
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
                case Control:
                    if((value & SoftwareReset) != 0)
                    {
                        Reset();
                        return;
                    }
                    regs[offset / 4] = value;
                    UpdateDmaRequests();
                    return;

                case DmaEnable:
                    regs[offset / 4] = value;
                    UpdateDmaRequests();
                    return;

                case TransmitData:
                    Transfer(value);
                    return;

                case TransmitCommand:
                {
                    // ⭐ CONT IS LATCHED WITH THE COMMAND AND CONSULTED AT THE END OF
                    // EVERY FRAME. Writing TCR with CONT clear while a transaction is
                    // open does NOT drop CS on its own -- the drop happens after the
                    // NEXT frame completes, which is exactly how the guest ends a
                    // transaction: set CONT for the continuing frames, clear it for
                    // the last one, then clock that last frame.
                    regs[offset / 4] = value;
                    return;
                }

                default:
                    regs[offset / 4] = value;
                    return;
            }
        }

        private void Transfer(uint value)
        {
            var peripheral = TryGetSelectedPeripheral();
            if(peripheral == null)
            {
                rxFifo.Enqueue(0);
                UpdateDmaRequests();
                return;
            }

            chipSelectAsserted = true;
            var response = peripheral.Transmit((byte)value);
            rxFifo.Enqueue(response);

            // End of frame: if CONT is clear the transaction is over and chip-select
            // drops, which is what tells the device to reset its command decoder.
            if((regs[TransmitCommand / 4] & Continuous) == 0)
            {
                peripheral.FinishTransmission();
                chipSelectAsserted = false;
            }
            UpdateDmaRequests();
            RearmTransmitRequest();
        }

        private ISPIPeripheral TryGetSelectedPeripheral()
        {
            var select = (int)((regs[TransmitCommand / 4] >> PeripheralChipSelectShift) & 0x3);
            if(TryGetByAddress(select, out var peripheral))
            {
                return peripheral;
            }
            return TryGetByAddress(0, out peripheral) ? peripheral : null;
        }

        private bool ModuleEnabled => (regs[Control / 4] & ModuleEnable) != 0;
        private bool ReceiveDataFlag => rxFifo.Count > (int)((regs[FifoControl / 4] >> ReceiveWatermarkShift) & 0x3);
        private bool TransmitRequestActive => ModuleEnabled && (regs[DmaEnable / 4] & TransmitDmaEnable) != 0;
        private bool ReceiveRequestActive => ModuleEnabled && (regs[DmaEnable / 4] & ReceiveDmaEnable) != 0 && ReceiveDataFlag;

        // Same held-level-by-re-arm shape as the LPI2C: the eDMA's own access to the
        // data register is the handshake, so the requests stop when the channel's
        // major loop retires and nothing has to be bounded by a guard counter.
        private void UpdateDmaRequests()
        {
            Connections[TransmitRequestLine].Set(TransmitRequestActive);
            Connections[ReceiveRequestLine].Set(ReceiveRequestActive);
        }

        private void RearmTransmitRequest()
        {
            Rearm(TransmitRequestLine, () => TransmitRequestActive);
        }

        private void RearmReceiveRequest()
        {
            Rearm(ReceiveRequestLine, () => ReceiveRequestActive);
        }

        private void Rearm(int index, Func<bool> stillActive)
        {
            var line = Connections[index];
            line.Set(false);
            machine.LocalTimeSource.ExecuteInNearestSyncedState(_ =>
            {
                if(stillActive())
                {
                    line.Set(true);
                }
            });
        }

        private readonly IMachine machine;
        private readonly uint[] regs;
        private readonly Queue<uint> rxFifo;
        private bool chipSelectAsserted;

        private const long Control         = 0x10;
        private const long Status          = 0x14;
        private const long DmaEnable       = 0x1C;
        private const long Configuration1  = 0x54;
        private const long FifoControl     = 0x58;
        private const long TransmitCommand = 0x60;
        private const long TransmitData    = 0x64;
        private const long ReceiveStatus   = 0x70;
        private const long ReceiveData     = 0x74;

        private const uint ModuleEnable      = 1u << 0;
        private const uint SoftwareReset     = 1u << 1;
        private const uint TransmitDataFlag  = 1u << 0;   // always: synchronous TX
        private const uint ReceiveFlagBit    = 1u << 1;
        private const uint ReceiveEmpty      = 1u << 1;
        private const uint Continuous        = 1u << 21;
        private const uint TransmitDmaEnable = 1u << 0;
        private const uint ReceiveDmaEnable  = 1u << 1;
        private const int  PeripheralChipSelectShift = 24;
        private const int  ReceiveWatermarkShift     = 16;
        private const int  TransmitRequestLine = 0;
        private const int  ReceiveRequestLine  = 1;
    }
}
