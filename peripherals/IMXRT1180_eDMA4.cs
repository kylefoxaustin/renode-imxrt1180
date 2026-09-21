//
// i.MX RT1180 eDMA4 — Renode's own DMA.NXP_eDMA / NXP_eDMA_Channels models,
// carried into this project with ONE load-bearing change: the software-START
// DONE flag is deferred instead of being set inside the MMIO write.
// See the "[RT1180 PATCH]" markers below for the change and its rationale.
//
// Upstream: renode-infrastructure src/Emulator/Peripherals/Peripherals/DMA/
//           NXP_eDMA.cs + NXP_eDMA_Channels.cs  (MIT, (c) 2010-2026 Antmicro)
// The two classes live in ONE file because Renode compiles each `i @file.cs`
// as a separate assembly, and they are mutually referential.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Time;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Utilities.Packets;
using System.Linq;
using Antmicro.Renode.Exceptions;
using Channel = Antmicro.Renode.Peripherals.DMA.IMXRT1180_eDMA_Channels.Channel;

namespace Antmicro.Renode.Peripherals.DMA
{
    public class IMXRT1180_eDMA_Channels : IDoubleWordPeripheral, IWordPeripheral, IKnownSize
    {
        public IMXRT1180_eDMA_Channels(IMachine machine, IMXRT1180_eDMA dma, uint count, int firstChannel = 0,
            long channelSize = 0x1000, bool hasMuxingRegisters = true, NXP_XRDC xrdc = null,
            uint defaultMasterId = 0x1)
        {
            Count = count;
            FirstChannelNumber = firstChannel;
            ChannelSize = channelSize;
            this.dma = dma;
            this.defaultMasterId = defaultMasterId;

            channels = new Channel[count];

            this.machine = machine;   // [RT1180 PATCH] needed to defer DONE
            var sysbus = machine.GetSystemBus(this);
            for(var i = 0; i < count; ++i)
            {
                channels[i] = new Channel(sysbus, this, firstChannel + i, hasMuxingRegisters);
                dma.SetChannel(firstChannel + i, channels[i]);
            }

            this.xrdc = xrdc;
        }

        public void Reset()
        {
            foreach(var channel in channels)
            {
                channel.Reset();
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            return channels[offset / ChannelSize].ReadDoubleWord(offset % ChannelSize);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            channels[offset / ChannelSize].WriteDoubleWord(offset % ChannelSize, value);
        }

        public ushort ReadWord(long offset)
        {
            return channels[offset / ChannelSize].ReadWord(offset % ChannelSize);
        }

        public void WriteWord(long offset, ushort value)
        {
            channels[offset / ChannelSize].WriteWord(offset % ChannelSize, value);
        }

        public long Size => ChannelSize * Count;

        public uint Count { get; }

        public long ChannelSize { get; }

        // [RT1180 PATCH] see the DONE-deferral note at ExecuteTransfer.
        internal readonly IMachine machine;

        public int FirstChannelNumber { get; }

        public IEnumerable<Channel> Channels => channels;

        private Channel.ErrorFlags CheckForErrorsWithMasterId(Request request, uint masterId, bool isPrivileged = false)
        {
            dma.DebugLog("Requesting transaction (master id {0}) for {1}", masterId, request);
            if(request.Source.Address.HasValue && (!xrdc?.CheckTransactionLegalWithMasterId(masterId, request.Source.Address.Value, MpuAccess.Read, isPrivileged: isPrivileged) ?? false))
            {
                dma.DebugLog("Read not allowed");
                return Channel.ErrorFlags.SourceBus;
            }
            if(request.Destination.Address.HasValue && (!xrdc?.CheckTransactionLegalWithMasterId(masterId, request.Destination.Address.Value, MpuAccess.Write, isPrivileged: isPrivileged) ?? false))
            {
                dma.DebugLog("Write not allowed");
                return Channel.ErrorFlags.DestinationBus;
            }
            return Channel.ErrorFlags.NoError;
        }

        private readonly Channel[] channels;
        private readonly IMXRT1180_eDMA dma;
        private readonly NXP_XRDC xrdc;
        private readonly uint defaultMasterId;

        public class Channel : IProvidesRegisterCollection<DoubleWordRegisterCollection>, IProvidesRegisterCollection<WordRegisterCollection>
        {
            public Channel(IBusController sysbus, IMXRT1180_eDMA_Channels channels, int channelNumber, bool hasMuxingRegisters = true)
            {
                this.sysbus = sysbus;
                this.channels = channels;
                dmaEngine = new DmaEngine(sysbus);
                IRQ = new GPIO();
                ChannelNumber = channelNumber;
                dwRegisters = new DoubleWordRegisterCollection(channels);
                wRegisters = new WordRegisterCollection(channels);

                DefineRegisters(hasMuxingRegisters);
            }

            public void Reset()
            {
                ServiceRequestSource = 0;
                dwRegisters.Reset();
                wRegisters.Reset();
                IRQ.Unset();
            }

            public uint ReadDoubleWord(long offset)
            {
                return dwRegisters.Read(offset);
            }

            public void WriteDoubleWord(long offset, uint value)
            {
                dwRegisters.Write(offset, value);
            }

            public ushort ReadWord(long offset)
            {
                return wRegisters.Read(offset);
            }

            public void WriteWord(long offset, ushort value)
            {
                wRegisters.Write(offset, value);
            }

            public void HardwareServiceRequest()
            {
                if(!enableDMARequest.Value && !enableAsynchronousDMARequest.Value)
                {
                    // It's not an error condition, as it's an intended programmed behavior.
                    channels.dma.DebugLog("CH{0}: Hardware request is currently disabled by channel configuration", ChannelNumber);
                    return;
                }
                ExecuteTransfer(true);
                UpdateInterrupts();
            }

            public void ChannelLinkInternalRequest()
            {
                tcdInMemory.START.Value = true;
                ExecuteTransfer();
                UpdateInterrupts();
            }

            DoubleWordRegisterCollection IProvidesRegisterCollection<DoubleWordRegisterCollection>.RegistersCollection => dwRegisters;

            WordRegisterCollection IProvidesRegisterCollection<WordRegisterCollection>.RegistersCollection => wRegisters;

            public GPIO IRQ { get; }

            public int ChannelNumber { get; }

            public ErrorFlags Errors => channelError.Value;

            public int? ServiceRequestSource { get; private set; }

            // A configuration error is an illegal setting in the transfer control descriptor.
            private static ErrorFlags CheckForConfigurationErrorsAtActivation(TransferControlDescriptorLocal tcd)
            {
                var errors = ErrorFlags.NoError;
                // A configuration error is reported when an inconsistent state is represented by one of these factors:
                // • Starting source or destination address
                // • Source or destination offsets
                // • Minor loop byte count
                // • Transfer size

                // The addresses and offsets must be aligned on zero-modulo-transfer-sized boundaries.
                var sourceTransferSize = tcd.SourceDataTransferSizeInBytes;
                var destinationTransferSize = tcd.DestinationDataTransferSizeInBytes;

                if(tcd.SourceAddress % sourceTransferSize != 0)
                {
                    errors |= ErrorFlags.SourceAddress;
                }
                if(tcd.SourceAddressSignedOffset % sourceTransferSize != 0)
                {
                    errors |= ErrorFlags.SourceOffset;
                }
                if(tcd.DestinationAddress % destinationTransferSize != 0)
                {
                    errors |= ErrorFlags.DestinationAddress;
                }
                if(tcd.DestinationAddressSignedOffset % destinationTransferSize != 0)
                {
                    errors |= ErrorFlags.DestinationOffset;
                }

                return errors;
            }

            // A scatter/gather configuration error is reported when the scatter/gather operation begins at major loop completion.
            private static ErrorFlags CheckForConfigurationErrorsAtMajorLoopCompletion(TransferControlDescriptorLocal tcd)
            {
                var errors = ErrorFlags.NoError;

                if(tcd.EnableScatterGatherProcessing)
                {
                    if(tcd.LastDestinationAddressAdjustmentOrScatterGatherAddress % TransferControlDescriptorSize != 0)
                    {
                        // The scatter/gather address is not aligned on a 32-byte boundary.
                        errors |= ErrorFlags.ScatterGatherConfiguration;
                    }
                }

                return errors;
            }

            private static ErrorFlags CheckForConfigurationErrorsAtMinorLoopCompletion(TransferControlDescriptorLocal tcd)
            {
                var errors = ErrorFlags.NoError;

                // The minor loop byte count must be a multiple of the source and destination transfer sizes.
                if(tcd.NBytes % (int)Math.Max(tcd.SourceDataTransferSizeInBytes, tcd.DestinationDataTransferSizeInBytes) != 0)
                {
                    errors |= ErrorFlags.NbytesCiterConfiguration;
                }

                if(tcd.EnableLinkCITER || tcd.EnableLinkBITER)
                {
                    if(tcd.EnableLinkCITER != tcd.EnableLinkBITER || tcd.MinorLoopLinkChannelNumberELinkYesCITER != tcd.MinorLoopLinkChannelNumberELinkYesBITER)
                    {
                        // The ELINK field does not equal for CITER and BITER or
                        // the LINKCH field does not equal for CITER and BITER.
                        errors |= ErrorFlags.NbytesCiterConfiguration;
                    }
                }

                return errors;
            }

            private ICPU GetCurrentCPUOrNull()
            {
                if(!sysbus.TryGetCurrentCPU(out var cpu))
                {
                    return null;
                }
                return cpu;
            }

            private void UpdateInterrupts()
            {
                IRQ.Set(interruptRequest.Value);
            }

            // The same flow for software and peripheral request.
            // Interrupts are updated once after exit from this method.
            private void ExecuteTransfer(bool singleIteration = false)
            {
                do
                {
                    var success = ExecuteMinorLoop();
                    if(!success)
                    {
                        return;
                    }
                    if(singleIteration)
                    {
                        break;
                    }
                }
                while(tcd.CurrentMajorIterationCount > 0);

                if(tcd.CurrentMajorIterationCount != 0)
                {
                    return;
                }

                // The major iteration count was exhausted.

                // ════ [RT1180 PATCH v5 — DEFER THE SIGNALLING PAIR, NOT THE TRANSFER] ══
                // The transfer itself stays synchronous, so the DmaEngine's
                // initiator/master-ID capture is unaffected (that is what broke
                // v2/v3, which deferred the whole ExecuteTransfer).
                // Only the two SIGNALLING actions move, and they move TOGETHER:
                // CH_CSR[DONE] is set FIRST, then CH_INT / the NVIC line.
                // That yields both properties stock firmware demands at once:
                //   * DONE is NOT set when the START write returns  -> InitCM7DMA's
                //     W1C-then-poll works (the M2 requirement).
                //   * DONE IS set before the completion IRQ is delivered ->
                //     EDMA_HandleIRQ samples CH_CSR&DONE=1 (the M1 requirement).
                // Structure suggested by @rt1180emulator, whose QEMU model does
                // exactly this: edma_sw_complete -> move data, set DONE, set INT,
                // raise the line -- all inside one deferred callback.
                // DONE is deferred UNCONDITIONALLY -- InitCM7DMA polls CH_CSR[DONE]
                // on a transfer with NO interrupt enabled, so gating the DONE set on
                // INTMAJOR loses M2 (measured: v4 did exactly that). Only the
                // interrupt half is conditional, and it always follows DONE.
                var raiseInterrupt = tcd.EnableInterruptIfMajorCounterComplete;
                channels.machine.ScheduleAction(DoneDeferral, _ =>
                {
                    channelDone.Value = true;
                    if(raiseInterrupt)
                    {
                        interruptRequest.Value = true;
                        UpdateInterrupts();
                    }
                });
                if(tcd.DisableRequest)
                {
                    enableDMARequest.Value = false;
                }

                // Reload the BITER field into the CITER field.
                tcd.CurrentMajorIterationCount = tcd.StartingMajorIterationCount;

                if(tcd.EnableStoreDestinationAddress)
                {
                    sysbus.WriteDoubleWord(tcd.LastSourceAddressAdjustmentOrStoreDADDRAddress, tcd.DestinationAddress, context);
                }
                else
                {
                    // In this context SLAST_SDA represents a signed value (negative adjustment value is allowed).
                    // We don't need to perform any conversion, as signed numbers in C# are represented in two's complement notation,
                    // so the binary representation of the result and hence the value of the register field is the same no matter the underlying type.
                    tcd.SourceAddress += tcd.LastSourceAddressAdjustmentOrStoreDADDRAddress;
                }

                if(tcd.EnableLinkWhenMajorLoopComplete)
                {
                    channels.dma.LinkChannel(ChannelNumber, tcd.MajorLoopLinkChannelNumber);
                }

                var errors = CheckForConfigurationErrorsAtMajorLoopCompletion(tcd);
                if(errors != ErrorFlags.NoError)
                {
                    ReportErrors(errors);
                    return;
                }

                if(tcd.EnableScatterGatherProcessing)
                {
                    // Fetch a new TCD using the scatter/gather address from system memory and load to a local memory.
                    var data = new byte[TransferControlDescriptorSize];
                    var destination = new Place(data, 0);
                    var req = new Request(
                        source: tcd.LastDestinationAddressAdjustmentOrScatterGatherAddress,
                        destination: destination,
                        size: TransferControlDescriptorSize,
                        readTransferType: TransferType.Byte,
                        writeTransferType: TransferType.Byte,
                        incrementReadAddress: true,
                        incrementWriteAddress: true
                    );

                    errors |= CheckForErrors(req);
                    if(errors != ErrorFlags.NoError)
                    {
                        ReportErrors(errors);
                        return;
                    }

                    var resp = dmaEngine.IssueCopy(req, context);
                    tcd = Packet.Decode<TransferControlDescriptorLocal>(data);
                    UpdateTCDInLocalMemory(tcd);
                }
                else
                {
                    tcd.DestinationAddress += tcd.LastDestinationAddressAdjustmentOrScatterGatherAddress;

                    // ════ [RT1180 PATCH v6 — COMMIT THE MAJOR-LOOP EPILOGUE] ═══════════
                    // Upstream never writes the local `tcd` back on the NON-scatter-gather
                    // path, so everything the major-loop epilogue computes is DISCARDED:
                    //   * the BITER->CITER reload  (line "Reload the BITER field")
                    //   * the SLAST adjustment to SADDR
                    //   * the DLAST adjustment to DADDR  (the line right above)
                    // ExecuteMinorLoop() re-fetches the TCD from the register file on every
                    // minor loop, so the next activation of this channel reads the STALE
                    // CITER == 0 that the last minor loop wrote, decrements it, and wraps
                    // to 0x3FFF. MEASURED on stock DMA.NXP_eDMA, no patches, running the
                    // stock SDK driver_examples/edma4/channel_link:
                    //
                    //   ch=0 citer=2 -> ch=1 citer=1 -> ch=0 citer=1 -> ch=1 citer=0
                    //   -> ch=1 citer=16383, 16382, 16381 ... (runaway)
                    //   -> ConstructionException: Value exceeds the size of the field
                    //      (CITER_ELINKYES is 9 bits) -> UNHANDLED -> process ABORT (rc=134)
                    //
                    // The ESG branch above already writes back; only this branch forgets.
                    // Renode 1.17.0 upstream defect -- see EXPERIMENT.md "defect #5".
                    UpdateTCDInLocalMemory(tcd);
                }
            }

            private bool ExecuteMinorLoop()
            {
                if(!channels.dma.IsTransferAllowed())
                {
                    channels.dma.WarningLog("CH{0}: Transfer won't be executed, because debug or halt feature is active", ChannelNumber);
                    return false;
                }

                tcd = FetchTCDFromLocalMemory();
                channels.dma.DebugLog("CH{0}: Fetched TCD: {1}", ChannelNumber, tcd);
                var errors = CheckForConfigurationErrorsAtActivation(tcd);

                if(errors != ErrorFlags.NoError)
                {
                    // The first transfer is initiated on the internal bus, unless a configuration error is detected
                    ReportErrors(errors);
                    return false;
                }

                // These fields are updated only when some data was transferred.
                // If it was halted due to an error or debug mode, then no status is updated.
                // Hardware clears START flag after the channel begins execution,
                // what is immediate during emulation so sofware always reads 0 from these fields.
                channelDone.Value = false;
                tcd.ChannelStart = false;

                // Minor loop transfer.
                // Signed SOFF and DOFF short values are casted to ulong, but the semantic of negative value is not lost.
                // Signed numbers in C# are represented in two's complement notation.
                // Adding unsigned numbers when some of them are casted from a signed type to an unsigned type
                // gives the result with the same binary representation as adding numbers with a sign.
                // The cast is wrapped in an unchecked block to make it explicit that casting a negative number to the unsigned type is allowed here.
                var request = new Request(
                    source: tcd.SourceAddress,
                    destination: tcd.DestinationAddress,
                    size: (int)tcd.NBytes,
                    readTransferType: (TransferType)tcd.SourceDataTransferSizeInBytes,
                    writeTransferType: (TransferType)tcd.DestinationDataTransferSizeInBytes,
                    sourceIncrementStep: unchecked((ulong)tcd.SourceAddressSignedOffset),
                    destinationIncrementStep: unchecked((ulong)tcd.DestinationAddressSignedOffset),
                    incrementReadAddress: true,
                    incrementWriteAddress: true
                );

                errors |= CheckForErrors(request);
                if(errors != ErrorFlags.NoError)
                {
                    ReportErrors(errors);
                    return false;
                }

                channels.dma.DebugLog("CH{0}: Executing transfer from 0x{1:X} ({2}) to 0x{3:X} ({4}), size {5}B", ChannelNumber, request.Source.Address, request.ReadTransferType, request.Destination.Address, request.WriteTransferType, request.Size);
                var response = dmaEngine.IssueCopy(request, context);

                // Minor loop completion
                tcd.SourceAddress = (uint)response.ReadAddress;
                tcd.DestinationAddress = (uint)response.WriteAddress;
                tcd.CurrentMajorIterationCount--;

                // Signed extended value is used for the calculation, because MLOFF field width is 20 bits, so it wouldn't be automatically sign extended in C#.
                if(tcd.SourceMinorLoopOffsetEnable)
                {
                    tcd.SourceAddress += tcd.MinorLoopOffsetSignExtended;
                }
                if(tcd.DestinationMinorLoopOffsetEnable)
                {
                    tcd.DestinationAddress += tcd.MinorLoopOffsetSignExtended;
                }

                if(tcd.CurrentMajorIterationCount == tcd.StartingMajorIterationCount / 2)
                {
                    if(tcd.EnableInterruptIfMajorCounterHalfComplete)
                    {
                        interruptRequest.Value = true;
                    }
                }

                // ════ [RT1180 PATCH — THE ONE LOAD-BEARING CHANGE] ════════════════
                // Upstream sets DONE here, i.e. synchronously inside the MMIO
                // write that set TCD_CSR[START]. That breaks the SDK's
                // InitCM7DMA/Prepare_CM7 idiom, which is correct on silicon:
                //     START  ->  W1C-clear a STALE CH_CSR[DONE]  ->  poll DONE
                // On hardware the transfer is still IN FLIGHT across the clear,
                // so the clear drops the stale flag and DONE sets afterwards.
                // With instant completion, DONE is set BEFORE the W1C, the W1C
                // wipes the fresh flag, and the poll hangs forever.
                //
                // QEMU hit this exact bug and fixed it the same way
                // (rt1180emulator 55aef3f0dd, + tests/imxrt1180-edma-swstart-order).
                // Per that author, MEASURED from their model: the byte-rate is
                // cosmetic and their 1000 ns floor is wall-clock robustness they
                // needed and Renode does not. The ONLY load-bearing property is
                // that DONE must not be set inside the START write. Renode's
                // deterministic quantum supplies the reproducibility their floor
                // was faking, so ANY nonzero deferral suffices.
                UpdateTCDInLocalMemory(tcd);
                // [v4] DONE is now set with the signalling pair at major completion.
                // ═══════════════════════════════════════════════════════════════

                errors = CheckForConfigurationErrorsAtMinorLoopCompletion(tcd);
                if(errors != ErrorFlags.NoError)
                {
                    ReportErrors(errors);
                    // Error prevents channel linking, but doesn't block other actions if the major iteration count was exhausted, so do not return here.
                }
                else
                {
                    if(tcd.EnableLinkCITER)
                    {
                        channels.dma.LinkChannel(ChannelNumber, tcd.MinorLoopLinkChannelNumberELinkYesCITER);
                    }
                }

                return true;
            }

            private ErrorFlags CheckForErrors(Request request)
            {
                if(enableMasterIdReplication.Value)
                {
                    return channels.CheckForErrorsWithMasterId(request, (uint)masterId.Value, privilegedAccessLevel.Value);
                }
                return channels.CheckForErrorsWithMasterId(request, channels.defaultMasterId, true);
            }

            private void ReportErrors(ErrorFlags errors)
            {
                channelError.Value = errors;
                if(errors != ErrorFlags.NoError)
                {
                    channels.dma.ReportErrorOnChannel(ChannelNumber);
                    if(enableErrorInterrupt.Value)
                    {
                        interruptRequest.Value = true;
                    }
                }
            }

            private TransferControlDescriptorLocal FetchTCDFromLocalMemory()
            {
                return new TransferControlDescriptorLocal
                {
                    SourceAddress = (uint)tcdInMemory.SADDR.Value,
                    SourceAddressSignedOffset = (short)tcdInMemory.SOFF.Value,
                    DestinationDataTransferSize = (byte)tcdInMemory.DSIZE.Value,
                    DestinationAddressModulo = (byte)tcdInMemory.DMOD.Value,
                    SourceDataTransferSize = (byte)tcdInMemory.SSIZE.Value,
                    SourceAddressModulo = (byte)tcdInMemory.SMOD.Value,
                    NBytesWithMinorLoopOffsets = (uint)tcdInMemory.NBYTES.Value,
                    MinorLoopOffset = (uint)tcdInMemory.MLOFF.Value,
                    DestinationMinorLoopOffsetEnable = tcdInMemory.DMLOE.Value,
                    SourceMinorLoopOffsetEnable = tcdInMemory.SMLOE.Value,
                    LastSourceAddressAdjustmentOrStoreDADDRAddress = (uint)tcdInMemory.SLAST_SDA.Value,
                    DestinationAddress = (uint)tcdInMemory.DADDR.Value,
                    DestinationAddressSignedOffset = (short)tcdInMemory.DOFF.Value,
                    CurrentMajorIterationCountELinkYes = (ushort)tcdInMemory.CITER_ELINKYES.Value,
                    MinorLoopLinkChannelNumberELinkYesCITER = (ushort)tcdInMemory.CITERLINKCH.Value,
                    ReservedELinkYesCITER = (byte)tcdInMemory.CITERRESERVED.Value,
                    EnableLinkCITER = tcdInMemory.CITERELINK.Value,
                    LastDestinationAddressAdjustmentOrScatterGatherAddress = (uint)tcdInMemory.DLAST_SGA.Value,
                    ChannelStart = tcdInMemory.START.Value,
                    EnableInterruptIfMajorCounterComplete = tcdInMemory.INTMAJOR.Value,
                    EnableInterruptIfMajorCounterHalfComplete = tcdInMemory.INTHALF.Value,
                    DisableRequest = tcdInMemory.DREQ.Value,
                    EnableScatterGatherProcessing = tcdInMemory.ESG.Value,
                    EnableLinkWhenMajorLoopComplete = tcdInMemory.MAJORELINK.Value,
                    EnableEndOfPacketProcessing = tcdInMemory.EEOP.Value,
                    EnableStoreDestinationAddress = tcdInMemory.ESDA.Value,
                    MajorLoopLinkChannelNumber = (byte)tcdInMemory.MAJORLINKCH.Value,
                    BandwidthControl = (byte)tcdInMemory.BWC.Value,
                    StartingMajorIterationCountELinkYes = (ushort)tcdInMemory.BITER_ELINKYES.Value,
                    MinorLoopLinkChannelNumberELinkYesBITER = (ushort)tcdInMemory.BITERLINKCH.Value,
                    ReservedELinkYesBITER = (byte)tcdInMemory.BITERRESERVED.Value,
                    EnableLinkBITER = tcdInMemory.BITERELINK.Value
                };
            }

            private void UpdateTCDInLocalMemory(TransferControlDescriptorLocal tcd)
            {
                tcdInMemory.SADDR.Value = tcd.SourceAddress;
                tcdInMemory.SOFF.Value = (ushort)tcd.SourceAddressSignedOffset;
                tcdInMemory.DSIZE.Value = tcd.DestinationDataTransferSize;
                tcdInMemory.DMOD.Value = tcd.DestinationAddressModulo;
                tcdInMemory.SSIZE.Value = tcd.SourceDataTransferSize;
                tcdInMemory.SMOD.Value = tcd.SourceAddressModulo;
                tcdInMemory.NBYTES.Value = tcd.NBytesWithMinorLoopOffsets;
                tcdInMemory.MLOFF.Value = tcd.MinorLoopOffset;
                tcdInMemory.DMLOE.Value = tcd.DestinationMinorLoopOffsetEnable;
                tcdInMemory.SMLOE.Value = tcd.SourceMinorLoopOffsetEnable;
                tcdInMemory.SLAST_SDA.Value = tcd.LastSourceAddressAdjustmentOrStoreDADDRAddress;
                tcdInMemory.DADDR.Value = tcd.DestinationAddress;
                tcdInMemory.DOFF.Value = (ushort)tcd.DestinationAddressSignedOffset;
                tcdInMemory.CITER_ELINKYES.Value = (ushort)tcd.CurrentMajorIterationCountELinkYes;
                tcdInMemory.CITERLINKCH.Value = (ushort)tcd.MinorLoopLinkChannelNumberELinkYesCITER;
                tcdInMemory.CITERRESERVED.Value = tcd.ReservedELinkYesCITER;
                tcdInMemory.CITERELINK.Value = tcd.EnableLinkCITER;
                tcdInMemory.DLAST_SGA.Value = tcd.LastDestinationAddressAdjustmentOrScatterGatherAddress;
                tcdInMemory.START.Value = tcd.ChannelStart;
                tcdInMemory.INTMAJOR.Value = tcd.EnableInterruptIfMajorCounterComplete;
                tcdInMemory.INTHALF.Value = tcd.EnableInterruptIfMajorCounterHalfComplete;
                tcdInMemory.DREQ.Value = tcd.DisableRequest;
                tcdInMemory.ESG.Value = tcd.EnableScatterGatherProcessing;
                tcdInMemory.MAJORELINK.Value = tcd.EnableLinkWhenMajorLoopComplete;
                tcdInMemory.EEOP.Value = tcd.EnableEndOfPacketProcessing;
                tcdInMemory.ESDA.Value = tcd.EnableStoreDestinationAddress;
                tcdInMemory.MAJORLINKCH.Value = tcd.MajorLoopLinkChannelNumber;
                tcdInMemory.BWC.Value = tcd.BandwidthControl;
                tcdInMemory.BITER_ELINKYES.Value = (ushort)tcd.StartingMajorIterationCountELinkYes;
                tcdInMemory.BITERLINKCH.Value = (ushort)tcd.MinorLoopLinkChannelNumberELinkYesBITER;
                tcdInMemory.BITERRESERVED.Value = (byte)tcd.ReservedELinkYesBITER;
                tcdInMemory.BITERELINK.Value = tcd.EnableLinkBITER;
            }

            private void DefineRegisters(bool hasMuxingRegisters)
            {
                Registers.ChannelControlAndStatus.Define(dwRegisters, name: "CHn_CSR")
                    .WithFlag(0, out enableDMARequest, name: "ERQ")
                    .WithFlag(1, out enableAsynchronousDMARequest, name: "EARQ")
                    .WithFlag(2, out enableErrorInterrupt, name: "EEI")
                    .WithTaggedFlag("EBW", 3)
                    .WithReservedBits(4, 12)
                    .WithReservedBits(16, 14)
                    .WithFlag(30, out channelDone, FieldMode.WriteOneToClear | FieldMode.Read, name: "DONE")
                    .WithFlag(31, FieldMode.Read, valueProviderCallback: _ =>
                    {
                        // Transfers are immediate so channel is always idle from the software perspective.
                        return false;
                    }, name: "ACTIVE")
                    .WithWriteCallback((_, __) =>
                    {
                        // Capture the context of cpu that configures DMA channel.
                        // It is used for DMA transfers triggered by other peripherals where cpu is not involved.
                        // Master ID Replication (Channel System Bus register) is a different mechanism that would capture
                        // the identity of core programming the eDMA's TCD.
                        context = GetCurrentCPUOrNull();
                        var mid = 0u;
                        if((!channels.xrdc?.TryGetMasterId(context, out mid)) ?? true)
                        {
                            channels.dma.WarningLog("Master ID for captured initiator not available");
                            masterId.Value = channels.defaultMasterId;
                            return;
                        }
                        masterId.Value = mid;
                    });

                Registers.ChannelErrorStatus.Define(dwRegisters, name: "CHn_ES")
                    .WithEnumField(0, 8, out channelError, FieldMode.Read, name: "DBE|SBE|SGE|NCE|DOE|DAE|SOE|SAE")
                    .WithReservedBits(8, 23)
                    .WithFlag(31, FieldMode.WriteOneToClear | FieldMode.Read, valueProviderCallback: _ =>
                    {
                        // This field is the logical OR of each error interrupt field (ERR).
                        return channelError.Value != ErrorFlags.NoError;
                    }, writeCallback: (_, val) =>
                    {
                        if(val)
                        {
                            channelError.Value = ErrorFlags.NoError;
                        }
                    }, name: "ERR");

                Registers.ChannelInterruptStatus.Define(dwRegisters, name: "CHn_INT")
                    .WithFlag(0, out interruptRequest, FieldMode.WriteOneToClear | FieldMode.Read, name: "INT")
                    .WithReservedBits(1, 31)
                    .WithWriteCallback((_, __) => UpdateInterrupts());

                Registers.ChannelSystemBus.Define(dwRegisters, 0x00008000 | channels.defaultMasterId, name: "CHn_SBR")
                    .WithValueField(0, 5, out masterId, FieldMode.Read, name: "MID")
                    .WithReservedBits(5, 9)
                    .WithFlag(14, name: "SEC")
                    .WithFlag(15, out privilegedAccessLevel, FieldMode.Read, name: "PAL")
                    .WithFlag(16, out enableMasterIdReplication,
                        changeCallback: (_, __) =>
                        {
                            enableMasterIdReplication.Value &= channels.dma.MasterIdReplicationEnabled;
                        },
                        name: "EMI"
                    )
                    .WithTag("ATTR", 17, 3)
                    .WithReservedBits(20, 12);

                // All fields are marked as RW, because from the software perspective arbitration rules are respected during emulation.
                // Channel preemption and arbitration configuration is ignored, because all DMA transfers finish immediately during emulation.
                Registers.ChannelPriority.Define(dwRegisters, name: "CHn_PRI")
                    .WithValueField(0, 3, name: "APL")
                    .WithReservedBits(3, 27)
                    .WithFlag(30, name: "DPA")
                    .WithFlag(31, name: "ECP");

                if(hasMuxingRegisters)
                {
                    Registers.ChannelMultiplexorConfiguration.Define(dwRegisters, name: "CHn_MUX")
                        .WithValueField(0, 7, writeCallback: (_, value) =>
                        {
                            ServiceRequestSource = 0;
                            if(value == 0)
                            {
                                return;
                            }

                            if(channels.dma.TryGetChannelBySlot((int)value, out var occupiedChannelNumber))
                            {
                                channels.dma.WarningLog("CH{0}: Trying to select a peripheral slot already occupied by CH{1}", ChannelNumber, occupiedChannelNumber);
                                return;
                            }

                            ServiceRequestSource = (int)value;
                        }, name: "SRC")
                        .WithReservedBits(7, 25);
                    ServiceRequestSource = 0;
                }

                Registers.TCDSourceAddress.Define(dwRegisters, name: "TCDn_SADDR")
                    .WithValueField(0, 32, out tcdInMemory.SADDR, name: "SADDR");

                Registers.TCDSignedSourceAddressOffset.Define(wRegisters, name: "TCDn_SOFF")
                    .WithValueField(0, 16, out tcdInMemory.SOFF, name: "SOFF");

                Registers.TCDTransferAttributes.Define(wRegisters, name: "TCDn_ATTR")
                    .WithValueField(0, 3, out tcdInMemory.DSIZE, name: "DSIZE")
                    .WithValueField(3, 5, out tcdInMemory.DMOD, name: "DMOD")
                    .WithValueField(8, 3, out tcdInMemory.SSIZE, name: "SSIZE")
                    .WithValueField(11, 5, out tcdInMemory.SMOD, name: "SMOD");

                // Layout for TCDn_NBYTES_MLOFFYES. TCD_NBYTES_MLOFFNO merges NBYTES and MLOFF into NBYTES.
                // See NBytesWithoutMinorLoopOffsets.
                Registers.TCDTransferSize.Define(dwRegisters, name: "TCDn_NBYTES_MLOFF")
                    .WithValueField(0, 10, out tcdInMemory.NBYTES, name: "NBYTES")
                    .WithValueField(10, 20, out tcdInMemory.MLOFF, name: "MLOFF")
                    .WithFlag(30, out tcdInMemory.DMLOE, name: "DMLOE")
                    .WithFlag(31, out tcdInMemory.SMLOE, name: "SMLOE");

                Registers.TCDLastSourceAddressAdjustment.Define(dwRegisters, name: "TCDn_SLAST_SDA")
                    .WithValueField(0, 32, out tcdInMemory.SLAST_SDA, name: "SLAST_SDA");

                Registers.TCDDestinationAddress.Define(dwRegisters, name: "TCDn_DADDR")
                    .WithValueField(0, 32, out tcdInMemory.DADDR, name: "DADDR");

                Registers.TCDSignedDestinationAddressOffset.Define(wRegisters, name: "TCDn_DOFF")
                    .WithValueField(0, 16, out tcdInMemory.DOFF, name: "DOFF");

                // Layout for TCDn_CITER_ELINKYES. TCDn_CITER_ELINKNO merges CITER, LINKCH and RESERVED into CITER.
                // See CurrentMajorIterationCountELinkNo.
                Registers.TCDCurrentMajorLoopCount.Define(wRegisters, name: "TCDn_CITER_ELINK")
                    .WithValueField(0, 9, out tcdInMemory.CITER_ELINKYES, name: "CITER")
                    .WithValueField(9, 5, out tcdInMemory.CITERLINKCH, name: "LINKCH")
                    .WithValueField(14, 1, out tcdInMemory.CITERRESERVED, name: "RESERVED")
                    .WithFlag(15, out tcdInMemory.CITERELINK, name: "ELINK");

                Registers.TCDLastDestinationAddressAdjustment.Define(dwRegisters, name: "TCDn_DLAST_SGA")
                    .WithValueField(0, 32, out tcdInMemory.DLAST_SGA, name: "DLAST_SGA");

                Registers.TCDControlAndStatus.Define(wRegisters, name: "TCDn_CSR")
                    .WithFlag(0, out tcdInMemory.START, writeCallback: (_, val) =>
                    {
                        if(val)
                        {
                            // ⭐⭐⭐ CAPTURE THE INITIATOR HERE TOO, OR THE TRANSFER
                            // RESOLVES ITS ADDRESSES AGAINST THE WRONG MAP.
                            //
                            // `context` was captured ONLY in the CHn_CSR write callback.
                            // A transfer can also be started from TCDn_CSR[START] -- and
                            // imxrt1180-edma phase1 does exactly that, never touching
                            // CHn_CSR first. With context null the copy resolved through
                            // the GLOBAL map, where this platform's CM33 DTCM (CPU-scoped
                            // at 0x20000000) does not exist.
                            //
                            // MEASURED: CH_CSR = 0x40000000 (DONE SET), src = 0x11111111,
                            // dst = 0x00000000. THE CHANNEL REPORTED COMPLETION AND MOVED
                            // NOTHING -- the third instance tonight of a block signalling
                            // work it did not do, after the ELE ack and my own
                            // EmitToWire-never-called. The failure mode is identical and
                            // the domain is irrelevant.
                            //
                            // And I had ELIMINATED this cause by reading that the model
                            // "captures CPU context" -- without checking on WHICH PATH.
                            // Reading that a mechanism EXISTS is not verifying it RUNS
                            // here; that is the same error as the defect #14 mechanism.
                            if(context == null)
                            {
                                context = GetCurrentCPUOrNull();
                            }
                            // ⭐⭐⭐ A SOFTWARE START IS ONE MINOR LOOP, NOT THE WHOLE
                            // MAJOR LOOP. Each START decrements CITER by one; a channel
                            // with CITER=N needs N of them (RM 5.5.5.2 walks a CITER=2
                            // channel through TWO requests explicitly).
                            //
                            // This ran the ENTIRE major loop on one START -- and that is
                            // INDISTINGUISHABLE FROM CORRECT AT CITER=1, because there one
                            // minor loop *is* the whole major loop. Every eDMA test in this
                            // project used CITER=1, so nothing could see it. The hardware
                            // request path one screen up already passed `true` here; only
                            // the software path did not.
                            //
                            // Fourth single-operating-point blind spot tonight, after the
                            // SAI at one sample rate, the ASRC with one clock source and
                            // the PTP at one addend. The pattern is not a coincidence:
                            // a parameter that is 1 collapses two behaviours into one
                            // observation.
                            channels.dma.DebugLog("CH{0}: Channel started by a software initiated service request", ChannelNumber);
                            ExecuteTransfer(singleIteration: true);
                            tcdInMemory.START.Value = false;
                        }
                    }, name: "START")
                    .WithFlag(1, out tcdInMemory.INTMAJOR, name: "INTMAJOR")
                    .WithFlag(2, out tcdInMemory.INTHALF, name: "INTHALF")
                    .WithFlag(3, out tcdInMemory.DREQ, name: "DREQ")
                    .WithFlag(4, out tcdInMemory.ESG, name: "ESG")
                    .WithFlag(5, out tcdInMemory.MAJORELINK, name: "MAJORELINK")
                    .WithFlag(6, out tcdInMemory.EEOP, name: "EEOP")
                    .WithFlag(7, out tcdInMemory.ESDA, name: "ESDA")
                    .WithValueField(8, 5, out tcdInMemory.MAJORLINKCH, name: "MAJORLINKCH")
                    .WithReservedBits(13, 1)
                    .WithValueField(14, 2, out tcdInMemory.BWC, name: "BWC")
                    .WithWriteCallback((_, __) => UpdateInterrupts());

                // Layout for TCDn_BITER_ELINKYES. TCDn_BITER_ELINKNO merges BITER, LINKCH and RESERVED into BITER.
                Registers.TCDBeginningMajorLoopCount.Define(wRegisters, name: "TCDn_BITER_ELINK")
                    .WithValueField(0, 9, out tcdInMemory.BITER_ELINKYES, name: "BITER")
                    .WithValueField(9, 5, out tcdInMemory.BITERLINKCH, name: "LINKCH")
                    .WithValueField(14, 1, out tcdInMemory.BITERRESERVED, name: "RESERVED")
                    .WithFlag(15, out tcdInMemory.BITERELINK, name: "ELINK");
            }

            private IFlagRegisterField enableDMARequest;
            private IFlagRegisterField enableAsynchronousDMARequest;
            private IFlagRegisterField enableErrorInterrupt;
            private IFlagRegisterField channelDone;
            private IEnumRegisterField<ErrorFlags> channelError;
            private IFlagRegisterField interruptRequest;
            private IValueRegisterField masterId;
            private IFlagRegisterField privilegedAccessLevel;
            private IFlagRegisterField enableMasterIdReplication;

            private TransferControlDescriptorLocal tcd;
            private TransferControlDescriptor tcdInMemory = new TransferControlDescriptor();
            private ICPU context;
            private readonly DoubleWordRegisterCollection dwRegisters;
            private readonly WordRegisterCollection wRegisters;
            // Smallest honest deferral: one microsecond of virtual time. The VALUE
            // is not load-bearing (see the patch note above) -- only that it is
            // nonzero and lands after the guest's W1C.
            private static readonly TimeInterval DoneDeferral = TimeInterval.FromMicroseconds(1);

            private readonly IBusController sysbus;
            private readonly DmaEngine dmaEngine;
            private readonly IMXRT1180_eDMA_Channels channels;

            private const int TransferControlDescriptorSize = 32;

            [Flags]
            public enum ErrorFlags
            {
                NoError = 0,
                DestinationBus = 1 << 0,                // DBE
                SourceBus = 1 << 1,                     // SBE
                ScatterGatherConfiguration = 1 << 2,    // SGE
                NbytesCiterConfiguration = 1 << 3,      // NCE
                DestinationOffset = 1 << 4,             // DOE
                DestinationAddress = 1 << 5,            // DAE
                SourceOffset = 1 << 6,                  // SOE
                SourceAddress = 1 << 7                  // SAE
            }

            private struct TransferControlDescriptor
            {
                public IValueRegisterField SADDR;
                public IValueRegisterField SOFF;
                public IValueRegisterField DSIZE;
                public IValueRegisterField DMOD;
                public IValueRegisterField SSIZE;
                public IValueRegisterField SMOD;
                public IValueRegisterField NBYTES;
                public IFlagRegisterField DMLOE;
                public IFlagRegisterField SMLOE;
                public IValueRegisterField MLOFF;
                public IValueRegisterField SLAST_SDA;
                public IValueRegisterField DADDR;
                public IValueRegisterField DOFF;
                public IValueRegisterField CITER_ELINKYES;
                public IFlagRegisterField CITERELINK;
                public IValueRegisterField CITERLINKCH;
                public IValueRegisterField CITERRESERVED;
                public IValueRegisterField DLAST_SGA;
                public IFlagRegisterField START;
                public IFlagRegisterField INTMAJOR;
                public IFlagRegisterField INTHALF;
                public IFlagRegisterField DREQ;
                public IFlagRegisterField ESG;
                public IFlagRegisterField MAJORELINK;
                public IFlagRegisterField EEOP;
                public IFlagRegisterField ESDA;
                public IValueRegisterField MAJORLINKCH;
                public IValueRegisterField BWC;
                public IValueRegisterField BITER_ELINKYES;
                public IValueRegisterField BITERLINKCH;
                public IValueRegisterField BITERRESERVED;
                public IFlagRegisterField BITERELINK;
            }

            [LeastSignificantByteFirst]
            private struct TransferControlDescriptorLocal
            {
                public override string ToString()
                {
                    var mloff = (DestinationMinorLoopOffsetEnable || SourceMinorLoopOffsetEnable) ? MinorLoopOffset : 0;
                    return ""
                        + $"SADDR=0x{SourceAddress:X},"
                        + $"SOFF=0x{SourceAddressSignedOffset:X}={SourceAddressSignedOffset},"
                        + $"DSIZE={DestinationDataTransferSize},"
                        + $"DMOD={DestinationAddressModulo},"
                        + $"SSIZE={SourceDataTransferSize},"
                        + $"SMOD={SourceAddressModulo},"
                        + $"NBYTES=0x{NBytes:X}={NBytes},"
                        + $"DMLOE={DestinationMinorLoopOffsetEnable},"
                        + $"SMLOE={SourceMinorLoopOffsetEnable},"
                        + $"MLOFF=0x{mloff:X}={(int)MinorLoopOffsetSignExtended},"
                        + $"SLAST_SDA=0x{LastSourceAddressAdjustmentOrStoreDADDRAddress:X}={LastSourceAddressAdjustmentOrStoreDADDRAddress},"
                        + $"DADDR=0x{DestinationAddress:X},"
                        + $"DOFF=0x{DestinationAddressSignedOffset:X}={DestinationAddressSignedOffset},"
                        + $"CITER={CurrentMajorIterationCount},"
                        + $"CITERELINK={EnableLinkCITER},"
                        + $"CITERLINKCH={(EnableLinkCITER ? MinorLoopLinkChannelNumberELinkYesCITER : 0)},"
                        + $"DLAST_SGA=0x{LastDestinationAddressAdjustmentOrScatterGatherAddress:X}={LastDestinationAddressAdjustmentOrScatterGatherAddress},"
                        + $"START={ChannelStart},"
                        + $"INTMAJOR={EnableInterruptIfMajorCounterComplete},"
                        + $"INTHALF={EnableInterruptIfMajorCounterHalfComplete},"
                        + $"DREQ={DisableRequest},"
                        + $"ESG={EnableScatterGatherProcessing},"
                        + $"MAJORELINK={EnableLinkWhenMajorLoopComplete},"
                        + $"EEOP={EnableEndOfPacketProcessing},"
                        + $"ESDA=0x{EnableStoreDestinationAddress},"
                        + $"MAJORLINKCH={MajorLoopLinkChannelNumber},"
                        + $"BWC={BandwidthControl},"
                        + $"BITER={StartingMajorIterationCount},"
                        + $"BITERELINK={EnableLinkBITER},"
                        + $"BITERLINKCH={(EnableLinkBITER ? MinorLoopLinkChannelNumberELinkYesBITER : 0)}"
                    ;
                }

                [PacketField, Offset(doubleWords: 0, bits: 0), Width(bits: 32)]
                public uint SourceAddress;
                [PacketField, Offset(doubleWords: 1, bits: 0), Width(bits: 16)]
                public short SourceAddressSignedOffset;
                [PacketField, Offset(doubleWords: 1, bits: 16), Width(bits: 3)]
                public byte DestinationDataTransferSize;
                [PacketField, Offset(doubleWords: 1, bits: 19), Width(bits: 5)]
                public byte DestinationAddressModulo;
                [PacketField, Offset(doubleWords: 1, bits: 24), Width(bits: 3)]
                public byte SourceDataTransferSize;
                [PacketField, Offset(doubleWords: 1, bits: 27), Width(bits: 5)]
                public byte SourceAddressModulo;
                [PacketField, Offset(doubleWords: 2, bits: 0), Width(bits: 10)]
                public uint NBytesWithMinorLoopOffsets;
                [PacketField, Offset(doubleWords: 2, bits: 10), Width(bits: MinorLoopOffsetFieldWidth)]
                public uint MinorLoopOffset;
                [PacketField, Offset(doubleWords: 2, bits: 30), Width(bits: 1)]
                public bool DestinationMinorLoopOffsetEnable;
                [PacketField, Offset(doubleWords: 2, bits: 31), Width(bits: 1)]
                public bool SourceMinorLoopOffsetEnable;
                [PacketField, Offset(doubleWords: 3, bits: 0), Width(bits: 32)]
                public uint LastSourceAddressAdjustmentOrStoreDADDRAddress;
                [PacketField, Offset(doubleWords: 4, bits: 0), Width(bits: 32)]
                public uint DestinationAddress;
                [PacketField, Offset(doubleWords: 5, bits: 0), Width(bits: 16)]
                public short DestinationAddressSignedOffset;
                [PacketField, Offset(doubleWords: 5, bits: 16), Width(bits: 9)]
                public ushort CurrentMajorIterationCountELinkYes;
                [PacketField, Offset(doubleWords: 5, bits: 25), Width(bits: 5)]
                public ushort MinorLoopLinkChannelNumberELinkYesCITER;
                [PacketField, Offset(doubleWords: 5, bits: 30), Width(bits: 1)]
                public byte ReservedELinkYesCITER;
                [PacketField, Offset(doubleWords: 5, bits: 31), Width(bits: 1)]
                public bool EnableLinkCITER;
                [PacketField, Offset(doubleWords: 6, bits: 0), Width(bits: 32)]
                public uint LastDestinationAddressAdjustmentOrScatterGatherAddress;
                [PacketField, Offset(doubleWords: 7, bits: 0), Width(bits: 1)]
                public bool ChannelStart;
                [PacketField, Offset(doubleWords: 7, bits: 1), Width(bits: 1)]
                public bool EnableInterruptIfMajorCounterComplete;
                [PacketField, Offset(doubleWords: 7, bits: 2), Width(bits: 1)]
                public bool EnableInterruptIfMajorCounterHalfComplete;
                [PacketField, Offset(doubleWords: 7, bits: 3), Width(bits: 1)]
                public bool DisableRequest;
                [PacketField, Offset(doubleWords: 7, bits: 4), Width(bits: 1)]
                public bool EnableScatterGatherProcessing;
                [PacketField, Offset(doubleWords: 7, bits: 5), Width(bits: 1)]
                public bool EnableLinkWhenMajorLoopComplete;
                [PacketField, Offset(doubleWords: 7, bits: 6), Width(bits: 1)]
                public bool EnableEndOfPacketProcessing;
                [PacketField, Offset(doubleWords: 7, bits: 7), Width(bits: 1)]
                public bool EnableStoreDestinationAddress;
                [PacketField, Offset(doubleWords: 7, bits: 8), Width(bits: 5)]
                public byte MajorLoopLinkChannelNumber;
                // bit 13 is reserved
                [PacketField, Offset(doubleWords: 7, bits: 14), Width(bits: 2)]
                public byte BandwidthControl;
                [PacketField, Offset(doubleWords: 7, bits: 16), Width(bits: 9)]
                public ushort StartingMajorIterationCountELinkYes;
                [PacketField, Offset(doubleWords: 7, bits: 25), Width(bits: 5)]
                public ushort MinorLoopLinkChannelNumberELinkYesBITER;
                [PacketField, Offset(doubleWords: 7, bits: 30), Width(bits: 1)]
                public byte ReservedELinkYesBITER;
                [PacketField, Offset(doubleWords: 7, bits: 31), Width(bits: 1)]
                public bool EnableLinkBITER;

                private ushort CurrentMajorIterationCountELinkNo
                {
                    get
                    {
                        return (ushort)(ReservedELinkYesCITER << 13 | MinorLoopLinkChannelNumberELinkYesCITER << 9 | CurrentMajorIterationCountELinkYes);
                    }

                    set
                    {
                        CurrentMajorIterationCountELinkYes = BitHelper.GetValue(value, 0, 9);
                        MinorLoopLinkChannelNumberELinkYesCITER = BitHelper.GetValue(value, 9, 5);
                        ReservedELinkYesCITER = (byte)BitHelper.GetValue((ushort)value, 14, 1);
                    }
                }

                private ushort StartingMajorIterationCountELinkNo
                {
                    get
                    {
                        return (ushort)(ReservedELinkYesBITER << 14 | MinorLoopLinkChannelNumberELinkYesBITER << 9 | StartingMajorIterationCountELinkYes);
                    }

                    set
                    {
                        StartingMajorIterationCountELinkYes = BitHelper.GetValue(value, 0, 9);
                        MinorLoopLinkChannelNumberELinkYesBITER = BitHelper.GetValue(value, 9, 5);
                        ReservedELinkYesBITER = (byte)BitHelper.GetValue(value, 14, 2);
                    }
                }

                private uint NBytesWithoutMinorLoopOffsets
                {
                    get
                    {
                        return MinorLoopOffset << 10 | NBytesWithMinorLoopOffsets;
                    }

                    set
                    {
                        NBytesWithMinorLoopOffsets = BitHelper.GetValue((uint)value, 0, 10);
                        MinorLoopOffset = BitHelper.GetValue((uint)value, 10, 20);
                    }
                }

                public uint NBytes
                {
                    get
                    {
                        return SourceMinorLoopOffsetEnable ? NBytesWithMinorLoopOffsets : NBytesWithoutMinorLoopOffsets;
                    }

                    set
                    {
                        if(SourceMinorLoopOffsetEnable)
                        {
                            NBytesWithMinorLoopOffsets = value;
                        }
                        else
                        {
                            NBytesWithoutMinorLoopOffsets = value;
                        }
                    }
                }

                public uint MinorLoopOffsetSignExtended
                {
                    get
                    {
                        return BitHelper.SignExtend(MinorLoopOffset, MinorLoopOffsetFieldWidth);
                    }
                }

                public ushort CurrentMajorIterationCount
                {
                    get
                    {
                        return EnableLinkCITER ? CurrentMajorIterationCountELinkYes : CurrentMajorIterationCountELinkNo;
                    }

                    set
                    {
                        if(EnableLinkCITER)
                        {
                            CurrentMajorIterationCountELinkYes = value;
                        }
                        else
                        {
                            CurrentMajorIterationCountELinkNo = value;
                        }
                    }
                }

                public ushort StartingMajorIterationCount
                {
                    get
                    {
                        return EnableLinkBITER ? StartingMajorIterationCountELinkYes : StartingMajorIterationCountELinkNo;
                    }

                    set
                    {
                        if(EnableLinkBITER)
                        {
                            StartingMajorIterationCountELinkYes = value;
                        }
                        else
                        {
                            StartingMajorIterationCountELinkNo = value;
                        }
                    }
                }

                public int SourceDataTransferSizeInBytes => 1 << SourceDataTransferSize; // power of two

                public int DestinationDataTransferSizeInBytes => 1 << DestinationDataTransferSize; // power of two

                private const int MinorLoopOffsetFieldWidth = 20;
            }
        }

        public enum Registers
        {
            ChannelControlAndStatus = 0x00,
            ChannelErrorStatus = 0x04,
            ChannelInterruptStatus = 0x08,
            ChannelSystemBus = 0x0C,
            ChannelPriority = 0x10,
            ChannelMultiplexorConfiguration = 0x14,
            TCDSourceAddress = 0x20,
            TCDSignedSourceAddressOffset = 0x24,
            TCDTransferAttributes = 0x26,
            TCDTransferSize = 0x28,
            TCDLastSourceAddressAdjustment = 0x2C,
            TCDDestinationAddress = 0x30,
            TCDSignedDestinationAddressOffset = 0x34,
            TCDCurrentMajorLoopCount = 0x36,
            TCDLastDestinationAddressAdjustment = 0x38,
            TCDControlAndStatus = 0x3C,
            TCDBeginningMajorLoopCount = 0x3E,
        }
    }

    public class IMXRT1180_eDMA : BasicDoubleWordPeripheral, IKnownSize, INumberedGPIOOutput, IHasOwnLife, IGPIOReceiver
    {
        public IMXRT1180_eDMA(IMachine machine, int numberOfChannels) : base(machine)
        {
            if(numberOfChannels < MinimumNumberOfChannels || numberOfChannels > MaximumNumberOfChannels)
            {
                throw new ConstructionException($"The number of channels {numberOfChannels} isn't in the allowed range {MinimumNumberOfChannels}-{MaximumNumberOfChannels}");
            }

            NumberOfChannels = numberOfChannels;

            connections = new Dictionary<int, IGPIO>();
            channels = new Channel[NumberOfChannels];

            DefineRegisters();
        }

        public void Start()
        {
            AssertChannels();
        }

        public void Pause()
        {
            // Intentionally left empty
        }

        public void Resume()
        {
            // Intentionally left empty
        }

        public void OnGPIO(int channel, bool value)
        {
            if(channel < 0 || channel >= MaximumNumberOfChannels)
            {
                this.WarningLog("Channel {0} is outside of allowed range  0-{1}", channel, MaximumNumberOfChannels - 1);
                return;
            }
            if(!value)
            {
                return;
            }
            channels[channel].HardwareServiceRequest();
        }

        public bool TryGetChannelBySlot(int slot, out int channelNumber)
        {
            var channel = channels.FirstOrDefault(x => x.ServiceRequestSource == slot)?.ChannelNumber;
            channelNumber = channel ?? default(int);
            return channel.HasValue;
        }

        public void SetChannel(int channelNumber, Channel channel)
        {
            if(channelNumber < 0 || channelNumber >= MaximumNumberOfChannels)
            {
                throw new ConstructionException($"Peripheral is configured for {MaximumNumberOfChannels} channel, attempted to register channel {channelNumber}");
            }
            channels[channelNumber] = channel;
            connections[channelNumber] = channels[channelNumber].IRQ;
        }

        public bool IsTransferAllowed()
        {
            if(enableDebug.Value)
            {
                // eDMA doesn't transfer data
                return false;
            }

            if(halt.Value)
            {
                return false;
            }

            return true;
        }

        public void ReportErrorOnChannel(int channelNumber)
        {
            if(channelNumber >= NumberOfChannels)
            {
                throw new ArgumentException($"Cannot report error on a nonexistent channel {channelNumber} - allowed channel numbers are in the range 0-{NumberOfChannels - 1}");
            }
            if(haltAfterError.Value)
            {
                halt.Value = true;
            }
            errorChannelNumber.Value = (ulong)channelNumber;
            // Error indicators are sticky and cannot be cleared.
            // They show the last recorded error until the DMA is reset.
            channelError.Value = channels[channelNumber].Errors;
        }

        public void LinkChannel(int initiatorChannelNumber, int linkedChannelNumber)
        {
            if(linkedChannelNumber < 0 || linkedChannelNumber >= NumberOfChannels)
            {
                this.WarningLog("Unable to link to a nonexistent channel {0} from channel {1}", linkedChannelNumber, initiatorChannelNumber);
                return;
            }
            this.DebugLog("Channel linking from CH{0} to CH{1}", initiatorChannelNumber, linkedChannelNumber);
            channels[linkedChannelNumber].ChannelLinkInternalRequest();
        }

        public long Size => 0x1000;

        public bool IsPaused => false;

        public IReadOnlyDictionary<int, IGPIO> Connections
        {
            get
            {
                AssertChannels();
                return connections;
            }
        }

        public bool ProvidesWithMuxingConfiguration
        {
            get
            {
                AssertChannels();
                return channels[0].ServiceRequestSource != null;
            }
        }

        public bool MasterIdReplicationEnabled => globalMasterIdReplicationControl.Value;

        public int NumberOfChannels { get; }

        public bool Halt
        {
            set => halt.Value = value;
        }

        private void AssertChannels()
        {
            if(channelsChecked)
            {
                return;
            }

            var channelsWithMuxing = channels[0]?.ServiceRequestSource.HasValue ?? false;

            for(var i = 0; i < NumberOfChannels; ++i)
            {
                if(channels[i] == null)
                {
                    throw new RecoverableException($"Channel {i} not registered");
                }
                if(channelsWithMuxing != channels[i].ServiceRequestSource.HasValue)
                {
                    throw new RecoverableException($"Mixed multiplexing detected, channel {i} does not match with previous channels");
                }
            }

            channelsChecked = true;
        }

        private void DefineRegisters()
        {
            // Channel preemption and arbitration configuration is ignored, because all DMA transfers finish immediately during emulation.
            Registers.ManagementPageControl.Define(this, 0x00310000, name: "MP_CSR")
                .WithReservedBits(0, 1)
                .WithFlag(1, out enableDebug, name: "EDBG")
                .WithFlag(2, name: "ERCA") // No impact, channel arbitration doesn't matter during emulation.
                .WithReservedBits(3, 1)
                .WithFlag(4, out haltAfterError, name: "HAE")
                .WithFlag(5, out halt, name: "HALT")
                .WithFlag(6, out globalChannelLinkingControl, name: "GCLC")
                .WithFlag(7, out globalMasterIdReplicationControl, name: "GMRC")
                .WithTaggedFlag("ECX", 8) // Minor loops are atomic during emulation, so cancellation is immediate.
                .WithTaggedFlag("CX", 9) // Same as above.
                .WithReservedBits(10, 6)
                .WithReservedBits(16, 8)
                .WithTag("ACTIVE_ID", 24, 5) // Software never observes ACTIVE bit 1 during emulation.
                .WithReservedBits(29, 2)
                .WithFlag(31, FieldMode.Read, valueProviderCallback: _ =>
                {
                    // Transfers are immediate so eDMA is always idle from the software perspective.
                    return false;
                }, name: "ACTIVE");

            // During emulation channel errors can occur only due to an illegal setting in the transfer control descriptor.
            // Bus and memory errors are not possible.
            // Error flags mirror flags in the corresponding channel specified by ERRCHN.
            Registers.ManagementPageErrorStatus.Define(this, name: "MP_ES")
                .WithEnumField<DoubleWordRegister, Channel.ErrorFlags>(0, 8, out channelError, FieldMode.Read, name: "DBE|SBE|SGE|NCE|DOE|DAE|SOE|SAE")
                .WithTaggedFlag("ECX", 8)
                .WithTaggedFlag("UCE", 9)
                .WithReservedBits(10, 6)
                .WithReservedBits(16, 8)
                .WithValueField(24, 5, out errorChannelNumber, FieldMode.Read, name: "ERRCHN")
                .WithReservedBits(29, 2)
                .WithFlag(31, FieldMode.Read, valueProviderCallback: _ =>
                {
                    return channels[(int)errorChannelNumber.Value].Errors != Channel.ErrorFlags.NoError;
                }, name: "VLD");

            Registers.ManagementPageInterruptRequestStatus.Define(this, name: "MP_INT")
                .WithFlags(0, NumberOfChannels, FieldMode.Read, valueProviderCallback: (i, _) => channels[i].IRQ.IsSet, name: "INT")
                .WithReservedBits(NumberOfChannels, 32 - NumberOfChannels);

            // During emulation transfers are executed immediately on peripheral requests,
            // so for software there are no active hardware service requests at any time.
            Registers.ManagementPageHardwareRequestStatus.Define(this, name: "MP_HRS")
                .WithFlags(0, NumberOfChannels, FieldMode.Read, valueProviderCallback: (i, _) => false, name: "HRS")
                .WithReservedBits(NumberOfChannels, 32 - NumberOfChannels);

            // No impact, channel arbitration doesn't matter during emulation, because all DMA requests are handled immediately.
            Registers.ChannelArbitrationGroup.DefineMany(this, (uint)NumberOfChannels, (register, channel) =>
            {
                register
                    .WithValueField(0, 5, name: "GRPRI")
                    .WithReservedBits(5, 27);
            }, name: "CHn_GRPRI");
        }

        private IFlagRegisterField enableDebug;
        private IFlagRegisterField haltAfterError;
        private IFlagRegisterField halt;
        private IFlagRegisterField globalChannelLinkingControl;
        private IFlagRegisterField globalMasterIdReplicationControl;
        private IEnumRegisterField<Channel.ErrorFlags> channelError;
        private IValueRegisterField errorChannelNumber;

        private bool channelsChecked;
        private readonly Channel[] channels;
        private readonly Dictionary<int, IGPIO> connections;

        private const int MinimumNumberOfChannels = 1;
        private const int MaximumNumberOfChannels = 32;

        private enum Registers
        {
            ManagementPageControl = 0x00,
            ManagementPageErrorStatus = 0x04,
            ManagementPageInterruptRequestStatus = 0x08,
            ManagementPageHardwareRequestStatus = 0x0C,
            ChannelArbitrationGroup = 0x100,
        }
    }
}
