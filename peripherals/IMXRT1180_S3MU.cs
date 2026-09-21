//
// i.MX RT1180 S3MU — Messaging Unit to the EdgeLock secure enclave (ELE).
//
// PORTED FROM THE QEMU ORACLE, NOT RE-DERIVED.
// Source: ~/Documents/GitHub/rt1180emulator/hw/misc/imxrt1180_s3mu.c
// Register offsets verified there against MIMXRT1189 CMSIS PERI_S3MU.h
// (TR_COUNT 8, RR_COUNT 4); command IDs against the SDK's
// fsl_ele_base_api.h / ele_crypto_internal.h.
//
// ════════════════════════════════════════════════════════════════════════════
// WHAT THIS MODEL MAY AND MAY NOT SAY  — carried across from the oracle intact,
// because it is the most important thing in the file.
//
// The ELE enclave is proprietary and NOT modelled. Declining to compute is
// fine. Telling the GUEST we computed something we did not is not.
//
// The SDK's ELE_* wrappers validate a reply with exactly
//     if(rmsg[0] == <CMD>_RESPONSE_HDR && rmsg[1] == RESPONSE_SUCCESS)
//         -> kStatus_Success, and the caller then USES the output buffer
// so a blanket SUCCESS reply is not a harmless stub — it is a fabricated
// cryptographic result. The oracle records that this exact bug once made
// ELE_RngGetRandom() return kStatus_Success while handing its caller
// UN-INITIALISED MEMORY AS CRYPTOGRAPHIC RANDOMNESS, silently.
//
// Logging the truth to the HOST while reporting SUCCESS to the GUEST is not
// honesty: the guest is the thing we are pretending to be hardware for.
//
// Therefore: an explicit WHITELIST of commands whose real-silicon OUTCOME this
// model genuinely reproduces. Everything else gets a well-formed reply with a
// NON-SUCCESS status. That INFORMS without GATING — the handshake completes,
// firmware never hangs, and the stock driver returns kStatus_Fail, which is
// the truth. A new/unknown ELE command MUST fail closed.
// ════════════════════════════════════════════════════════════════════════════
//
using System;
using System.Security.Cryptography;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class IMXRT1180_S3MU : IDoubleWordPeripheral, IKnownSize
    {
        public IMXRT1180_S3MU(IMachine machine)
        {
            this.machine = machine;
            Reset();
        }

        public long Size => 0x1000;

        public void Reset()
        {
            cr = sr = fcr = fsr = gier = gcr = gsr = tcr = rcr = 0;
            txCount = 0;
            txExpected = 1;
            rrFull = 0;
            Array.Clear(txBuf, 0, txBuf.Length);
            Array.Clear(rr, 0, rr.Length);
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset >= MU_RR0 && offset < MU_RR0 + 4 * RrCount)
            {
                var idx = (int)((offset - MU_RR0) / 4);
                rrFull &= ~(1u << idx);            // consume this reply word
                return rr[idx];
            }
            if(offset >= MU_TR0 && offset < MU_TR0 + 4 * TrCount)
            {
                return txBuf[(offset - MU_TR0) / 4];
            }

            switch(offset)
            {
                case MU_VER:  return 0x00000100;               // plausible version
                case MU_PAR:  return (RrCount << 8) | TrCount; // TR/RR counts
                case MU_CR:   return cr;
                case MU_SR:   return sr;
                case MU_FCR:  return fcr;
                case MU_FSR:  return fsr;
                case MU_GIER: return gier;
                case MU_GCR:  return gcr;
                case MU_GSR:  return gsr;
                case MU_TCR:  return tcr;
                case MU_TSR:  return TsrAllEmpty;              // TX always ready
                case MU_RCR:  return rcr;
                case MU_RSR:  return rrFull;                   // RX-full bits
                default:
                    this.Log(LogLevel.Warning, "Unhandled read @0x{0:X}", offset);
                    return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset >= MU_TR0 && offset < MU_TR0 + 4 * TrCount)
            {
                // Reassemble the outgoing request; the header (first word)
                // carries the total word count in its size byte.
                if(txCount < TrCount)
                {
                    txBuf[txCount] = value;
                }
                if(txCount == 0)
                {
                    txExpected = (int)((value >> 8) & 0xFF);
                    if(txExpected == 0 || txExpected > TrCount)
                    {
                        txExpected = 1;
                    }
                }
                txCount++;
                if(txCount >= txExpected)
                {
                    BuildResponse();
                }
                return;
            }

            switch(offset)
            {
                case MU_CR:   cr = value; break;
                case MU_FCR:  fcr = value; break;
                case MU_GIER: gier = value; break;
                case MU_GCR:  gcr = value; break;
                case MU_TCR:  tcr = value; break;
                case MU_RCR:  rcr = value; break;
                case MU_VER: case MU_PAR: case MU_SR:
                case MU_FSR: case MU_GSR: case MU_TSR: case MU_RSR:
                    break;                                     // read-only
                default:
                    this.Log(LogLevel.Warning, "Unhandled write @0x{0:X} = 0x{1:X8}", offset, value);
                    break;
            }
        }

        // A message header word is 0xTT_CC_SS_VV = {tag, command, size, version}.
        private void BuildResponse()
        {
            var header = txBuf[0];
            var command = (byte)((header >> 16) & 0xFF);
            var version = (byte)(header & 0xFF);
            var respSize = ResponseSize(command);

            var truthful = CommandIsTruthful(command);
            if(command == CmdGetRngRandom)
            {
                // The ONE enclave service we can honour COMPLETELY, so we do.
                // A fault is the honest answer only when you cannot compute;
                // entropy you CAN compute. If delivery fails, we must NOT
                // report success. [oracle]
                truthful = DoRng();
            }

            if(!truthful)
            {
                this.Log(LogLevel.Warning,
                    "ELE command 0x{0:X2} is NOT reproduced by this model — replying NON-SUCCESS " +
                    "(0x{1:X2}) rather than fabricating a result. The stock driver will return " +
                    "kStatus_Fail, which is the truth.", command, ResponseFailure);
            }

            rr[0] = (uint)((MsgTagResp << 24) | (command << 16) | (respSize << 8) | version);
            rr[1] = truthful ? ResponseSuccess : ResponseFailure;
            for(var i = 2; i < respSize && i < RrCount; i++)
            {
                rr[i] = 0;   // e.g. GET_FW_STATUS: 0 == "no ELE FW in place",
                             // which is exactly true of this model. [oracle]
            }

            rrFull = (1u << respSize) - 1u;
            txCount = 0;
            txExpected = 1;
        }

        // GET_RNG_RANDOM: tx[2] = destination address, tx[3] = byte count.
        private bool DoRng()
        {
            var addr = txBuf[2];
            var len = txBuf[3];
            if(len == 0 || len > (1u << 20))
            {
                this.Log(LogLevel.Warning, "GET_RNG_RANDOM implausible size {0}", len);
                return false;
            }
            var bytes = new byte[len];
            using(var rngProvider = RandomNumberGenerator.Create())
            {
                rngProvider.GetBytes(bytes);   // real entropy, not a seeded PRNG
            }
            // ⭐⭐⭐ WRITE IN THE REQUESTING CPU'S ADDRESS CONTEXT, NOT THE GLOBAL ONE.
            // This was a FABRICATED SUCCESS of the exact kind this project exists to
            // catch, and it was mine.
            //
            // The CM33's DTCM is registered CPU-SCOPED at 0x20000000 (and globally
            // at 0x20200000). The enclave is asked to fill a buffer the firmware
            // placed in DTCM, so the destination it hands us -- 0x20000010 -- is a
            // CPU-LOCAL address. A context-less SystemBus.WriteBytes() resolves that
            // against the GLOBAL map, where nothing is mapped at 0x20000010, so the
            // bytes went into a hole.
            //
            // MEASURED: the model computed 32 real random bytes, wrote them to
            // 0x20000010, reported kStatus_Success with the correct response header
            // 0xE1CD0207 -- and the guest's buffer still read 0xA5A5A5A5. The
            // oracle's tests/imxrt1180-ele caught it:
            //   "ELE: FAIL - enclave reported SUCCESS but produced NO randomness"
            //   "the guest would seed a crypto stack with un-computed data"
            //
            // ⚠ AND MY OWN IMXRT1180_SRC.cs CARRIES A COMMENT WARNING ABOUT EXACTLY
            // THIS -- "READ IN THE M7's OWN ADDRESS CONTEXT ... reading without the
            // context yields garbage". I wrote that warning and then made the same
            // mistake in a different peripheral. A lesson recorded in one file does
            // not generalise by itself.
            var sysbus = machine.GetSystemBus(this);
            ICPU requester;
            if(sysbus.TryGetCurrentCPU(out requester))
            {
                sysbus.WriteBytes(bytes, addr, context: requester);
            }
            else
            {
                // No requesting CPU means we cannot resolve a CPU-local address, and
                // a write we cannot place must NOT be reported as success.
                this.Log(LogLevel.Warning,
                    "GET_RNG_RANDOM: no requesting CPU context, so a CPU-local destination " +
                    "0x{0:X8} cannot be resolved. Failing rather than reporting success over " +
                    "a buffer that may never have been written.", addr);
                return false;
            }
            return true;
        }

        // Commands whose real-silicon OUTCOME this model genuinely reproduces.
        // Whitelist ON PURPOSE — unknown commands fail closed. [oracle]
        private static bool CommandIsTruthful(byte command)
        {
            switch(command)
            {
                case 0x01:   // PING                 — pure coordination
                case 0x10:   // CLOCK_CHANGE_START   — brackets PLL changes;
                case 0x11:   // CLOCK_CHANGE_FINISH    no enclave result
                case 0x12:   // VOLTAGE_CHANGE_START — same class
                case 0x13:   // VOLTAGE_CHANGE_FINISH
                    return true;
                case 0xA3:   // START_RNG      — the RNG is real, so this is true
                case 0xCD:   // GET_RNG_RANDOM — actually computed (DoRng)
                    return true;
                case 0xC4:   // RELEASE_RDC — TRDC grants full access anyway, so
                             // the outcome the guest checks for genuinely holds
                    return true;
                case 0xC5:   // GET_FW_STATUS — reply 0 == "no ELE FW in place",
                             // which is exactly true of this model
                    return true;
                case 0xD2:   // KICK CM7 — coordination whose real outcome (the
                             // M7 boots) this model reproduces via the
                             // M7_CFG.WAIT clear MCMGR issues right after.
                             // Reporting FAILURE would be the dishonest answer.
                    return true;
                default:
                    return false;
            }
        }

        private static byte ResponseSize(byte command)
        {
            switch(command)
            {
                case 0x9D: return 4;   // GET_FW_VERSION
                case 0xC5: return 3;   // GET_FW_STATUS
                default:   return 2;   // header + status
            }
        }

        private readonly IMachine machine;
        private readonly uint[] txBuf = new uint[TrCount];
        private readonly uint[] rr = new uint[RrCount];
        private uint cr, sr, fcr, fsr, gier, gcr, gsr, tcr, rcr;
        private uint rrFull;
        private int txCount;
        private int txExpected;

        private const int TrCount = 8;
        private const int RrCount = 4;
        private const uint TsrAllEmpty = (1u << TrCount) - 1u;
        private const uint MsgTagResp = 0xE1u;
        private const uint ResponseSuccess = 0xD6u;
        // Non-success status. The SDK never enumerates ELE's abort codes; every
        // wrapper tests only `rmsg[1] == RESPONSE_SUCCESS`. 0x29 is the low byte
        // of the SDK's one non-success constant RESPONSE_ERROR_SIZE (0x1d29) —
        // an INFERENCE about the encoding, flagged as such. What is NOT an
        // inference: it makes the stock driver return kStatus_Fail. [oracle]
        private const uint ResponseFailure = 0x29u;
        private const byte CmdGetRngRandom = 0xCD;

        private const long MU_VER = 0x000, MU_PAR = 0x004, MU_CR = 0x008, MU_SR = 0x00C;
        private const long MU_FCR = 0x100, MU_FSR = 0x104;
        private const long MU_GIER = 0x110, MU_GCR = 0x114, MU_GSR = 0x118;
        private const long MU_TCR = 0x120, MU_TSR = 0x124, MU_RCR = 0x128, MU_RSR = 0x12C;
        private const long MU_TR0 = 0x200, MU_RR0 = 0x280;
    }
}
