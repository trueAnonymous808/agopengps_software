using System;
using System.Collections.Generic;

namespace AgLibrary.Gnss
{
    /// <summary>
    /// Binary GNSS frames in a byte stream that also carries NMEA text: RTCM 3 (0xD3, a 10 bit length, the payload
    /// and a CRC-24Q) and u-blox UBX (0xB5 0x62, class, id, a 16 bit length, the payload and a Fletcher checksum).
    /// Feed the bytes as they come; complete frames with a good checksum come out, and everything else comes out as
    /// text (NMEA never uses bytes above 127, so 0xD3 / 0xB5 only start a frame or are noise).
    /// </summary>
    public sealed class Rtcm3Frames
    {
        public const byte Preamble = 0xD3;
        public const byte UbxSync1 = 0xB5, UbxSync2 = 0x62;
        public const int MaxPayload = 1023;
        public const int MaxUbxPayload = 8192;
        public const int LocalPort = 15556;   // AgIO sends the frames to AgOpenGPS on this loopback port

        /// <summary>
        /// The first byte of each loopback datagram: where the frame comes from. The rover's own data is used whole;
        /// from the correction stream (the base station's data) only the ephemerides are taken.
        /// </summary>
        public const byte TagRover = (byte)'R', TagCorrections = (byte)'N';

        private readonly List<byte> buffer = new List<byte>();

        /// <summary>The frames found and the bytes that are not part of one (to go on as NMEA text).</summary>
        public void Feed(byte[] data, int count, List<byte[]> frames, List<byte> text)
        {
            for (int i = 0; i < count; i++) buffer.Add(data[i]);

            int pos = 0;
            while (pos < buffer.Count)
            {
                byte b = buffer[pos];
                if (b == Preamble)
                {
                    if (buffer.Count - pos < 3) break;   // the length is not here yet
                    int length = ((buffer[pos + 1] & 0x03) << 8) | buffer[pos + 2];
                    if ((buffer[pos + 1] & 0xFC) != 0 || length > MaxPayload)
                    {
                        pos++;   // not a frame after all
                        continue;
                    }
                    int total = 3 + length + 3;
                    if (buffer.Count - pos < total) break;   // wait for the rest

                    var frame = new byte[total];
                    buffer.CopyTo(pos, frame, 0, total);
                    if (CheckCrc(frame))
                    {
                        frames.Add(frame);
                        pos += total;
                    }
                    else
                    {
                        pos++;   // a 0xD3 in noise: skip it and look on
                    }
                }
                else if (b == UbxSync1)
                {
                    if (buffer.Count - pos < 2) break;
                    if (buffer[pos + 1] != UbxSync2)
                    {
                        pos++;
                        continue;
                    }
                    if (buffer.Count - pos < 6) break;
                    int length = buffer[pos + 4] | (buffer[pos + 5] << 8);
                    if (length > MaxUbxPayload)
                    {
                        pos++;
                        continue;
                    }
                    int total = 6 + length + 2;
                    if (buffer.Count - pos < total) break;

                    var frame = new byte[total];
                    buffer.CopyTo(pos, frame, 0, total);
                    if (CheckUbx(frame))
                    {
                        frames.Add(frame);
                        pos += total;
                    }
                    else
                    {
                        pos++;
                    }
                }
                else
                {
                    text.Add(b);
                    pos++;
                }
            }
            buffer.RemoveRange(0, pos);
            if (buffer.Count > 4 * (MaxUbxPayload + 8)) buffer.Clear();   // never grow without bound
        }

        /// <summary>The RTCM message number of a frame (0 when it is not an RTCM frame).</summary>
        public static int RtcmType(byte[] frame)
        {
            if (frame == null || frame.Length < 6 || frame[0] != Preamble) return 0;
            return (frame[3] << 4) | (frame[4] >> 4);
        }

        /// <summary>Broadcast ephemerides: what the correction stream is worth to TDCP.</summary>
        public static bool IsEphemeris(int rtcmType) =>
            rtcmType == 1019 || rtcmType == 1020 || rtcmType == 1042 || rtcmType == 1045 || rtcmType == 1046;

        /// <summary>CRC-24Q over the header and payload, compared with the frame's last three bytes.</summary>
        public static bool CheckCrc(byte[] frame)
        {
            if (frame == null || frame.Length < 6) return false;
            int n = frame.Length - 3;
            uint crc = Crc24Q(frame, n);
            uint stored = ((uint)frame[n] << 16) | ((uint)frame[n + 1] << 8) | frame[n + 2];
            return crc == stored;
        }

        public static uint Crc24Q(byte[] data, int length)
        {
            uint crc = 0;
            for (int i = 0; i < length; i++)
            {
                crc ^= (uint)data[i] << 16;
                for (int b = 0; b < 8; b++)
                {
                    crc <<= 1;
                    if ((crc & 0x1000000) != 0) crc ^= 0x1864CFB;
                }
            }
            return crc & 0xFFFFFF;
        }

        /// <summary>The UBX Fletcher checksum over class, id, length and payload.</summary>
        public static bool CheckUbx(byte[] frame)
        {
            if (frame == null || frame.Length < 8 || frame[0] != UbxSync1 || frame[1] != UbxSync2) return false;
            int length = frame[4] | (frame[5] << 8);
            if (frame.Length != length + 8) return false;
            UbxChecksum(frame, 2, length + 4, out byte a, out byte b);
            return frame[length + 6] == a && frame[length + 7] == b;
        }

        private static void UbxChecksum(byte[] data, int start, int count, out byte a, out byte b)
        {
            int ca = 0, cb = 0;
            for (int i = start; i < start + count; i++)
            {
                ca = (ca + data[i]) & 0xFF;
                cb = (cb + ca) & 0xFF;
            }
            a = (byte)ca;
            b = (byte)cb;
        }

        /// <summary>A whole frame around a payload (for tests and tools).</summary>
        public static byte[] Wrap(byte[] payload)
        {
            if (payload.Length > MaxPayload) throw new ArgumentException("payload too long");
            var frame = new byte[payload.Length + 6];
            frame[0] = Preamble;
            frame[1] = (byte)((payload.Length >> 8) & 0x03);
            frame[2] = (byte)(payload.Length & 0xFF);
            Array.Copy(payload, 0, frame, 3, payload.Length);
            uint crc = Crc24Q(frame, payload.Length + 3);
            frame[payload.Length + 3] = (byte)(crc >> 16);
            frame[payload.Length + 4] = (byte)(crc >> 8);
            frame[payload.Length + 5] = (byte)crc;
            return frame;
        }

        /// <summary>A whole UBX frame around a payload (for tests and tools).</summary>
        public static byte[] WrapUbx(byte cls, byte id, byte[] payload)
        {
            var frame = new byte[payload.Length + 8];
            frame[0] = UbxSync1;
            frame[1] = UbxSync2;
            frame[2] = cls;
            frame[3] = id;
            frame[4] = (byte)(payload.Length & 0xFF);
            frame[5] = (byte)(payload.Length >> 8);
            Array.Copy(payload, 0, frame, 6, payload.Length);
            UbxChecksum(frame, 2, payload.Length + 4, out byte a, out byte b);
            frame[payload.Length + 6] = a;
            frame[payload.Length + 7] = b;
            return frame;
        }

        /// <summary>A loopback datagram: the tag and the frame.</summary>
        public static byte[] Tagged(byte tag, byte[] frame)
        {
            var d = new byte[frame.Length + 1];
            d[0] = tag;
            Array.Copy(frame, 0, d, 1, frame.Length);
            return d;
        }
    }
}
