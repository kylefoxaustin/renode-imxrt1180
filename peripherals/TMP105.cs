//
// TI TMP105 — I2C digital temperature sensor.
//
// Written to close the oracle's tests/imxrt1180-lpi2c and -lpi2c-dma, which failed
// here with "TMP105 read did not ACK / no data". MEASURED before this model: those
// two rows produced NO unmapped-peripheral accesses at all -- LPI2C4 is modelled
// and mapped at 0x42540000 -- so the controller was fine and the BUS WAS EMPTY.
// That distinction (missing slave on a present controller vs missing controller)
// is invisible through a "FAIL" grep and obvious in the unmapped-access log.
//
// ⭐ ANCHORS SUPPLIED BY @rt1180emulator, whose QEMU model these tests pass against.
// Taking them rather than re-deriving is the standing rule of this project: their
// models encode WHAT THE DRIVER WAITS ON, which the reference manual does not say.
//
//   7-bit address 0x48 (write 0x90 / read 0x91)
//   Pointer byte selects the register:
//       0x00 temperature, 0x01 config, 0x02 T_low, 0x03 T_high
//   Read protocol: START+addr(W), pointer byte, repeated-START+addr(R), 2 bytes,
//   MSB FIRST.
//   Temperature register: 12-bit value LEFT-JUSTIFIED in the 16-bit word ([15:4]),
//   LSB = 0.0625 degC. 25.0 degC => 0x1900 (0x190 = 400; 400 * 0.0625 = 25.0),
//   which is the exact value their test asserts.
//
using System;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.I2C;

namespace Antmicro.Renode.Peripherals.Sensors
{
    public class TMP105 : II2CPeripheral
    {
        public TMP105()
        {
            Reset();
        }

        public void Reset()
        {
            pointer = RegTemperature;
            config = 0x00;
            tLow = 0x4B00;      // 75 degC, the part's reset value
            tHigh = 0x5000;     // 80 degC
            TemperatureCelsius = 25.0;
            pendingPointerWrite = true;
        }

        // ⭐ The temperature is a PROPERTY, not a constant, so a platform or a test
        // can drive it. A sensor that can only ever report one number is a constant
        // wearing a device's costume -- and this project has spent a long time
        // finding values that looked measured and were not.
        public double TemperatureCelsius { get; set; }

        public void Write(byte[] data)
        {
            if(data.Length == 0)
            {
                return;
            }
            // First byte after a START is always the register pointer. Any further
            // bytes in the same transaction write that register (MSB first).
            this.Log(LogLevel.Info, "I2CDIAG Write len={0} bytes=[{1}]", data.Length, string.Join(" ", System.Array.ConvertAll(data, b => b.ToString("X2"))));
            pointer = (byte)(data[0] & 0x03);
            pendingPointerWrite = false;

            if(data.Length >= 3)
            {
                var value = (ushort)((data[1] << 8) | data[2]);
                switch(pointer)
                {
                case RegConfig:  config = data[1];   break;
                case RegTLow:    tLow = value;       break;
                case RegTHigh:   tHigh = value;      break;
                default:
                    // The temperature register is READ-ONLY on silicon. Accepting a
                    // write here would make the model agree with any firmware that
                    // tried it -- and disagree with the part.
                    this.Log(LogLevel.Warning,
                        "Write to the read-only temperature register ignored, as on silicon");
                    break;
                }
            }
        }

        public byte[] Read(int count = 1)
        {
            var value = CurrentRegister();
            this.Log(LogLevel.Info, "I2CDIAG Read count={0} pointer=0x{1:X2} value=0x{2:X4}", count, pointer, value);
            // MSB FIRST. Getting this backwards yields a plausible-looking number
            // (0x0019 instead of 0x1900) that decodes to 1.5625 degC rather than 25 --
            // wrong by 16x and entirely believable in a log.
            var full = new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) };
            if(count >= full.Length)
            {
                return full;
            }
            var partial = new byte[count];
            Array.Copy(full, partial, count);
            return partial;
        }

        public void FinishTransmission()
        {
            // The pointer PERSISTS across transactions on this part: a bare
            // address(R) with no preceding pointer write re-reads the last-selected
            // register. Resetting it here would break repeat reads.
            pendingPointerWrite = true;
        }

        private ushort CurrentRegister()
        {
            switch(pointer)
            {
            case RegConfig:  return (ushort)(config << 8);
            case RegTLow:    return tLow;
            case RegTHigh:   return tHigh;
            default:         return EncodeTemperature(TemperatureCelsius);
            }
        }

        // 12-bit two's-complement, LEFT-JUSTIFIED in [15:4], 0.0625 degC per LSB.
        private static ushort EncodeTemperature(double celsius)
        {
            var counts = (int)Math.Round(celsius / 0.0625);
            if(counts > 2047)  { counts = 2047; }
            if(counts < -2048) { counts = -2048; }
            return (ushort)((counts & 0x0FFF) << 4);
        }

        private byte pointer;
        private byte config;
        private ushort tLow;
        private ushort tHigh;
        private bool pendingPointerWrite;

        private const byte RegTemperature = 0x00;
        private const byte RegConfig = 0x01;
        private const byte RegTLow = 0x02;
        private const byte RegTHigh = 0x03;
    }
}
