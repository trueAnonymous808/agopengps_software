using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AgLibrary.Gnss
{
    public enum ReceiverKind { Unknown, Um982, UbloxF9P }

    /// <summary>
    /// What the configurator sends to a GNSS receiver and reads back: u-blox UBX frames (CFG-VALSET built from a
    /// u-center / Config-O-Matic configuration dump, MON-VER, ACK/NAK) and Unicore text commands with their answers.
    /// </summary>
    public static class ReceiverConfig
    {
        // ---- u-blox ----

        public const byte LayerRam = 0x01, LayerBbr = 0x02, LayerFlash = 0x04;
        public const int MaxItemsPerValSet = 64;

        // CFG-MSGOUT keys of the raw measurements on UART1 (for TDCP)
        public const uint KeyRawxUart1 = 0x209102A5;
        public const uint KeySfrbxUart1 = 0x20910232;

        public static byte[] UbxFrame(byte cls, byte id, byte[] payload)
        {
            payload = payload ?? new byte[0];
            var f = new byte[8 + payload.Length];
            f[0] = 0xB5;
            f[1] = 0x62;
            f[2] = cls;
            f[3] = id;
            f[4] = (byte)(payload.Length & 0xFF);
            f[5] = (byte)(payload.Length >> 8);
            Array.Copy(payload, 0, f, 6, payload.Length);
            byte a = 0, b = 0;
            for (int i = 2; i < 6 + payload.Length; i++)
            {
                a += f[i];
                b += a;
            }
            f[6 + payload.Length] = a;
            f[7 + payload.Length] = b;
            return f;
        }

        public static byte[] PollMonVer() => UbxFrame(0x0A, 0x04, null);

        /// <summary>The byte size of a configuration item's value, from its key (u-blox: bits 28-30).</summary>
        public static int ValueSize(uint key)
        {
            switch ((key >> 28) & 0x7)
            {
                case 1:
                case 2: return 1;
                case 3: return 2;
                case 4: return 4;
                case 5: return 8;
                default: throw new ArgumentException("Configuration key " + key.ToString("X8") + " has no valid size");
            }
        }

        /// <summary>CFG-VALSET frames (at most 64 items each) writing the items to the given layers.</summary>
        public static List<byte[]> ValSetFrames(IEnumerable<KeyValuePair<uint, ulong>> items, byte layers)
        {
            var frames = new List<byte[]>();
            var list = items.ToList();
            for (int start = 0; start < list.Count; start += MaxItemsPerValSet)
            {
                var payload = new List<byte> { 0x00, layers, 0x00, 0x00 };
                foreach (KeyValuePair<uint, ulong> kv in list.Skip(start).Take(MaxItemsPerValSet))
                {
                    payload.AddRange(BitConverter.GetBytes(kv.Key));
                    int size = ValueSize(kv.Key);
                    byte[] v = BitConverter.GetBytes(kv.Value);
                    for (int i = 0; i < size; i++) payload.Add(v[i]);
                }
                frames.Add(UbxFrame(0x06, 0x8A, payload.ToArray()));
            }
            return frames;
        }

        /// <summary>
        /// The configuration items of a u-center "Generation 9 configuration" dump, as Config-O-Matic ships them:
        /// lines "CFG-VALGET - 06 8B len len version layer pos pos (key value)...". Other lines are skipped.
        /// </summary>
        public static List<KeyValuePair<uint, ulong>> ParseUcenterDump(string text)
        {
            var items = new List<KeyValuePair<uint, ulong>>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("CFG-VALGET", StringComparison.OrdinalIgnoreCase)) continue;
                int dash = line.IndexOf('-', "CFG-VALGET".Length);
                if (dash < 0) continue;
                byte[] b = line.Substring(dash + 1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(h => byte.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
                if (b.Length < 8 || b[0] != 0x06 || b[1] != 0x8B) continue;
                int len = b[2] | (b[3] << 8);
                int end = Math.Min(b.Length, 4 + len);
                int i = 4 + 4;   // version, layer, position
                while (i + 4 <= end)
                {
                    uint key = BitConverter.ToUInt32(b, i);
                    i += 4;
                    int size = ValueSize(key);
                    if (i + size > end) break;
                    ulong value = 0;
                    for (int k = 0; k < size; k++) value |= (ulong)b[i + k] << (8 * k);
                    i += size;
                    items.Add(new KeyValuePair<uint, ulong>(key, value));
                }
            }
            return items;
        }

        /// <summary>UBX-ACK-ACK / ACK-NAK: whether it was taken, and for which message.</summary>
        public static bool TryParseAck(byte[] f, out bool acknowledged, out byte cls, out byte id)
        {
            acknowledged = false;
            cls = id = 0;
            if (f == null || f.Length < 10 || f[0] != 0xB5 || f[1] != 0x62 || f[2] != 0x05 || (f[3] != 0x01 && f[3] != 0x00)) return false;
            acknowledged = f[3] == 0x01;
            cls = f[6];
            id = f[7];
            return true;
        }

        /// <summary>UBX-MON-VER: software, hardware and the extension lines (FWVER=..., MOD=ZED-F9P, ...).</summary>
        public static bool TryParseMonVer(byte[] f, out string text)
        {
            text = null;
            if (f == null || f.Length < 8 + 40 || f[0] != 0xB5 || f[1] != 0x62 || f[2] != 0x0A || f[3] != 0x04) return false;
            int len = f[4] | (f[5] << 8);
            var parts = new List<string> { Ascii(f, 6, 30), Ascii(f, 36, 10) };
            for (int o = 40; o + 30 <= len && 6 + o + 30 <= f.Length; o += 30) parts.Add(Ascii(f, 6 + o, 30));
            text = string.Join(", ", parts.Where(p => p.Length > 0));
            return true;
        }

        private static string Ascii(byte[] f, int start, int max)
        {
            int n = 0;
            while (n < max && start + n < f.Length && f[start + n] != 0) n++;
            return Encoding.ASCII.GetString(f, start, n).Trim();
        }

        // ---- Unicore (UM982) ----

        /// <summary>The raw measurements for TDCP on the port the receiver talks to the module on: MSM4 and ephemerides.</summary>
        public static readonly string[] Um982TdcpOn =
        {
            "RTCM1074 COM1 0.2", "RTCM1084 COM1 0.2", "RTCM1094 COM1 0.2", "RTCM1124 COM1 0.2",
            "RTCM1019 COM1 10", "RTCM1020 COM1 10", "RTCM1042 COM1 10", "RTCM1046 COM1 10"
        };

        public static readonly string[] Um982TdcpOff =
        {
            "UNLOG COM1 RTCM1074", "UNLOG COM1 RTCM1084", "UNLOG COM1 RTCM1094", "UNLOG COM1 RTCM1124",
            "UNLOG COM1 RTCM1019", "UNLOG COM1 RTCM1020", "UNLOG COM1 RTCM1042", "UNLOG COM1 RTCM1046"
        };

        public const string Um982Version = "VERSIONA";
        public const string Um982Save = "SAVECONFIG";

        /// <summary>The commands of a Config-O-Matic UM982 file (one per line; its "Version" header skipped).</summary>
        public static List<string> ParseUnicoreCommands(string text)
        {
            return text.Split('\n')
                .Select(l => l.Trim().TrimStart('﻿'))
                .Where(l => l.Length > 0 && !l.StartsWith("Version", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("//", StringComparison.Ordinal))
                .ToList();
        }

        public static byte[] UnicoreCommand(string command) => Encoding.ASCII.GetBytes(command.Trim() + "\r\n");

        /// <summary>"$command,GPGGA COM1 0.1,response: OK*xx": the command and whether it was taken.</summary>
        public static bool TryParseUnicoreAnswer(string line, out string command, out bool ok)
        {
            command = null;
            ok = false;
            if (line == null || !line.StartsWith("$command,", StringComparison.OrdinalIgnoreCase)) return false;
            int resp = line.IndexOf(",response:", StringComparison.OrdinalIgnoreCase);
            if (resp < 0) return false;
            command = line.Substring(9, resp - 9).Trim();
            string answer = line.Substring(resp + 10);
            int star = answer.IndexOf('*');
            if (star >= 0) answer = answer.Substring(0, star);
            ok = answer.Trim().StartsWith("OK", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        /// <summary>Which receiver answered: a Unicore #VERSIONA log or a u-blox MON-VER.</summary>
        public static ReceiverKind KindFromAnswer(byte[] data)
        {
            if (data == null || data.Length == 0) return ReceiverKind.Unknown;
            if (TryParseMonVer(data, out _)) return ReceiverKind.UbloxF9P;   // u-blox: what the AiO boards carry is an F9P
            string s = Encoding.ASCII.GetString(data);
            if (s.StartsWith("#VERSIONA", StringComparison.OrdinalIgnoreCase) && s.IndexOf("UM982", StringComparison.OrdinalIgnoreCase) >= 0) return ReceiverKind.Um982;
            if (s.StartsWith("$command", StringComparison.OrdinalIgnoreCase)) return ReceiverKind.Um982;
            return ReceiverKind.Unknown;
        }
    }
}
