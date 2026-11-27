using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AgLibrary.Firmware
{
    /// <summary>
    /// An Intel HEX firmware image (as the Arduino / Teensyduino build makes it): the bytes it sets, by address.
    /// Data records (00), end (01), extended segment (02) and extended linear (04) addresses; start addresses
    /// (03, 05) are read and ignored.
    /// </summary>
    public sealed class IntelHex
    {
        private readonly SortedDictionary<uint, byte> bytes = new SortedDictionary<uint, byte>();

        public int Count => bytes.Count;
        public uint Lowest { get; private set; } = uint.MaxValue;
        public uint Highest { get; private set; }

        public bool TryGet(uint address, out byte value) => bytes.TryGetValue(address, out value);

        public static IntelHex Load(string path)
        {
            using (var reader = new StreamReader(path))
                return Parse(reader);
        }

        public static IntelHex Parse(TextReader reader)
        {
            var hex = new IntelHex();
            uint upper = 0;
            int lineNo = 0;
            bool ended = false;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                lineNo++;
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line[0] != ':' || line.Length < 11 || (line.Length - 1) % 2 != 0)
                    throw new FormatException("HEX line " + lineNo + ": not a record");
                byte[] rec = new byte[(line.Length - 1) / 2];
                for (int i = 0; i < rec.Length; i++)
                    rec[i] = byte.Parse(line.Substring(1 + i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                int len = rec[0];
                if (rec.Length != len + 5) throw new FormatException("HEX line " + lineNo + ": wrong length");
                byte sum = 0;
                foreach (byte b in rec) sum += b;
                if (sum != 0) throw new FormatException("HEX line " + lineNo + ": checksum");
                uint offset = (uint)((rec[1] << 8) | rec[2]);
                switch (rec[3])
                {
                    case 0x00:
                        for (int i = 0; i < len; i++) hex.Set(upper + offset + (uint)i, rec[4 + i]);
                        break;
                    case 0x01:
                        ended = true;
                        break;
                    case 0x02:
                        upper = (uint)((rec[4] << 8) | rec[5]) << 4;
                        break;
                    case 0x04:
                        upper = (uint)((rec[4] << 8) | rec[5]) << 16;
                        break;
                    case 0x03:
                    case 0x05:
                        break;
                    default:
                        throw new FormatException("HEX line " + lineNo + ": record type " + rec[3]);
                }
                if (ended) break;
            }
            if (!ended) throw new FormatException("HEX file has no end record");
            if (hex.Count == 0) throw new FormatException("HEX file holds no data");
            return hex;
        }

        private void Set(uint address, byte value)
        {
            bytes[address] = value;
            if (address < Lowest) Lowest = address;
            if (address > Highest) Highest = address;
        }

        /// <summary>The image from start to its last byte, unset bytes 0xFF (what an update over the network sends).</summary>
        public byte[] Image(uint start, uint maxLength)
        {
            if (Lowest < start || Highest >= start + maxLength)
                throw new InvalidOperationException("The firmware does not fit this board (address " + Lowest.ToString("X8") + "-" + Highest.ToString("X8") + ")");
            var image = new byte[Highest - start + 1];
            for (int i = 0; i < image.Length; i++) image[i] = 0xFF;
            foreach (KeyValuePair<uint, byte> kv in bytes) image[kv.Key - start] = kv.Value;
            return image;
        }

        /// <summary>CRC-32 (IEEE, as zip): what the firmware checks the received image with.</summary>
        public static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1u)));
            }
            return ~crc;
        }

        /// <summary>
        /// The image as blocks for a loader: block i covers [start + i*size, start + (i+1)*size), unset bytes 0xFF.
        /// Blocks with nothing set are left out (null), except the first (it starts the erase).
        /// </summary>
        public List<byte[]> Blocks(uint start, int blockSize, uint maxLength)
        {
            if (Lowest < start || Highest >= start + maxLength)
                throw new InvalidOperationException("The firmware does not fit this board (address " + Lowest.ToString("X8") + "-" + Highest.ToString("X8") + ")");
            int count = (int)((Highest - start) / (uint)blockSize) + 1;
            var blocks = new List<byte[]>(count);
            for (int i = 0; i < count; i++) blocks.Add(null);
            foreach (KeyValuePair<uint, byte> kv in bytes)
            {
                int index = (int)((kv.Key - start) / (uint)blockSize);
                if (blocks[index] == null)
                {
                    blocks[index] = new byte[blockSize];
                    for (int k = 0; k < blockSize; k++) blocks[index][k] = 0xFF;
                }
                blocks[index][(kv.Key - start) % (uint)blockSize] = kv.Value;
            }
            if (blocks[0] == null)
            {
                blocks[0] = new byte[blockSize];
                for (int k = 0; k < blockSize; k++) blocks[0][k] = 0xFF;
            }
            return blocks;
        }
    }
}
