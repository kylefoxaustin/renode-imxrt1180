//
// NXP FXLS8974CF -- 3-axis 12-bit accelerometer on I2C (MIMXRT1180-EVK sensor U115).
//
// PORTED, register-for-register, from the QEMU oracle's hw/sensor/fxls8974.c.
// Nothing here is re-derived: every offset, reset value and the device ID come
// from that file, which is itself register-accurate against the MCUXpresso
// fsl_fxls driver. The brief's rule -- "do NOT re-derive RT1180 behaviour from
// scratch" -- applies to board devices as much as to SoC blocks.
//
// WHY THIS EXISTS: the QEMU corpus row for demo_apps/bubble_peripheral is
// annotated "(sensor not modeled)", but hw/arm/imxrt1180_soc.c:736 DOES create
// this device at 0x19 on LPI2C2. MEASURED here: QEMU prints the banner and a
// steady "x=  0 y =  0"; Renode without the sensor printed "Sensor device
// initialize failed! / Please check the sensor chip U115" and never reached the
// banner. The note is stale -- the model is real and load-bearing.
//
// HONESTY (carried over verbatim in intent from the QEMU model): the axes report
// a FIXED "board flat, at rest" orientation -- +1 g on Z, 0 on X/Y. That is the
// honest reading of a stationary sensor; gravity is real. There is no motion
// input, so the board never tilts. That is FLAGGED, not faked.
//
using System;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.I2C;

namespace Antmicro.Renode.Peripherals.Sensors
{
    public class FXLS8974 : II2CPeripheral
    {
        public FXLS8974()
        {
            regs = new byte[NumberOfRegisters];
            Reset();
        }

        public void Reset()
        {
            Array.Clear(regs, 0, regs.Length);
            regs[(int)Registers.WhoAmI]  = DeviceId;
            regs[(int)Registers.ProdRev] = 0x11;
            // Static orientation: X = Y = 0, Z = +1 g (little-endian LSB then MSB).
            regs[(int)Registers.OutZLsb] = (byte)(OneG & 0xFF);
            regs[(int)Registers.OutZMsb] = (byte)(OneG >> 8);
            pointer = 0;
            addressed = false;
            loggedFixedOrientation = false;
        }

        // The first byte of a write selects the register; the rest are data, with
        // the pointer auto-incrementing (burst writes), exactly as fxls_send() does.
        public void Write(byte[] data)
        {
            if(data.Length == 0)
            {
                return;
            }
            var offset = 0;
            if(!addressed)
            {
                pointer = data[0];
                addressed = true;
                offset = 1;
            }
            for(var i = offset; i < data.Length; i++)
            {
                if(pointer < NumberOfRegisters)
                {
                    regs[pointer] = data[i];
                }
                pointer++;
            }
        }

        public byte[] Read(int count = 1)
        {
            var result = new byte[count];
            for(var i = 0; i < count; i++)
            {
                result[i] = ReadRegister(pointer);
                pointer++;   // auto-increment for burst reads
            }
            return result;
        }

        public void FinishTransmission()
        {
            addressed = false;
        }

        private byte ReadRegister(byte reg)
        {
            if(reg >= (byte)Registers.OutXLsb && reg <= (byte)Registers.OutZMsb
                && !loggedFixedOrientation)
            {
                loggedFixedOrientation = true;
                this.Log(LogLevel.Warning, "Reporting a FIXED 'board flat at rest' orientation "
                    + "(+1 g on Z, 0 on X/Y); no motion input drives the axes. Flagged, not faked.");
            }

            switch((Registers)reg)
            {
                case Registers.WhoAmI:
                    return DeviceId;
                case Registers.IntStatus:
                    return SrcDataReady;   // always a sample ready to read
                default:
                    return reg < NumberOfRegisters ? regs[reg] : (byte)0;
            }
        }

        private readonly byte[] regs;
        private byte pointer;
        private bool addressed;
        private bool loggedFixedOrientation;

        private const int NumberOfRegisters = 0x20;
        private const byte DeviceId = 0x86;
        private const byte SrcDataReady = 0x80;   // INT_STATUS.SRC_DRDY
        private const ushort OneG = 0x0800;       // +1 g on Z (12-bit-ish)

        private enum Registers : byte
        {
            IntStatus   = 0x00,
            OutXLsb     = 0x04,
            OutZLsb     = 0x08,
            OutZMsb     = 0x09,
            ProdRev     = 0x12,
            WhoAmI      = 0x13,
            SensConfig1 = 0x15,
        }
    }
}
