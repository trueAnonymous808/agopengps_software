using AgLibrary.Firmware;
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace AgIO.Configurator
{
    /// <summary>
    /// Firmware update over Ethernet, for the firmwares with AogNetUpdate (UDP port 2235 on the module): the image
    /// goes over in 1 KB pieces, each confirmed; the module keeps it apart from its running program and installs it
    /// only when all of it is there and its CRC-32 is right. Answers come back in PGN 0xB1. The query goes to the
    /// whole subnet; the update only to the module that answered it, so no other module with this firmware takes it.
    /// </summary>
    public sealed class NetFirmwareUpdater
    {
        public const int Port = 2235;
        public const uint FlashBase = 0x60000000;
        public const uint MaxImage = 0x00400000;
        private const int Piece = 1024;

        private readonly Func<byte[], IPAddress, bool> send;
        private readonly object sync = new object();
        private readonly AutoResetEvent got = new AutoResetEvent(false);
        private byte[] answer;
        private IPAddress answerFrom;
        private char awaited;
        private IPAddress target;   // the module the query found; null: to the whole subnet

        /// <param name="send">Sends one datagram to port 2235 of a module (null: the whole subnet); false when there is
        /// no network.</param>
        public NetFirmwareUpdater(Func<byte[], IPAddress, bool> send)
        {
            this.send = send;
        }

        /// <summary>
        /// The board a firmware is for, from its name: "AIO v4 (OTA & RawGNSS), Nov 26 2026" -> "aio v4". Firmwares of
        /// different boards drive their pins differently (the steering motor on another pin).
        /// </summary>
        public static string BoardFamily(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int cut = name.Length;
            int paren = name.IndexOf('('), comma = name.IndexOf(',');
            if (paren >= 0) cut = Math.Min(cut, paren);
            if (comma >= 0) cut = Math.Min(cut, comma);
            return name.Substring(0, cut).Trim().ToLowerInvariant();
        }

        /// <summary>Whether a firmware is for the same board as the one running (unknown names: not the same).</summary>
        public static bool SameBoard(string running, string firmware)
        {
            string a = BoardFamily(running), b = BoardFamily(firmware);
            return a.Length > 0 && b.Length > 0 && (a == b || a.Contains(b) || b.Contains(a));
        }

        /// <summary>The module the last query found (the one an update goes to).</summary>
        public IPAddress Module { get { lock (sync) return target; } }

        /// <summary>The body of a PGN 0xB1 answer (command letter, status, extra) and who sent it.</summary>
        public void Answer(byte[] body, IPAddress from)
        {
            if (body == null || body.Length < 2) return;
            lock (sync)
            {
                if ((char)body[0] != awaited) return;
                // while updating: only the module being updated
                if (target != null && from != null && !from.Equals(target)) return;
                answer = body;
                answerFrom = from;
                awaited = '\0';
            }
            got.Set();
        }

        private static byte[] Message(char cmd, byte[] payload)
        {
            payload = payload ?? new byte[0];
            var m = new byte[6 + payload.Length];
            Encoding.ASCII.GetBytes("AOGFW").CopyTo(m, 0);
            m[5] = (byte)cmd;
            payload.CopyTo(m, 6);
            return m;
        }

        // one message, its answer (null if none in time); resent every second unless it must not be repeated
        // (beginning erases the space again, the end installs)
        // (armed once before the first send: an answer coming between two waits is kept, not cleared)
        private byte[] Ask(char cmd, byte[] payload, int timeoutMs, Func<byte[], bool> match = null, bool resend = true)
        {
            DateTime giveUp = DateTime.Now.AddMilliseconds(timeoutMs);
            char letter = char.ToLowerInvariant(cmd);
            IPAddress to;
            lock (sync)
            {
                awaited = letter;
                answer = null;
                to = target;
            }
            got.Reset();
            bool sent = false;
            while (DateTime.Now < giveUp)
            {
                if (resend || !sent)
                {
                    if (!send(Message(cmd, payload), to)) throw new IOException("No network to the module");
                    sent = true;
                }
                int wait = (int)Math.Max(1, Math.Min(1000, (giveUp - DateTime.Now).TotalMilliseconds));
                got.WaitOne(wait);
                lock (sync)
                {
                    byte[] a = answer;
                    if (a != null && (match == null || match(a))) return a;
                    if (a != null)
                    {
                        // an answer to an earlier send (another piece): wait on for the right one
                        answer = null;
                        awaited = letter;
                    }
                }
            }
            lock (sync) awaited = '\0';
            return null;
        }

        /// <summary>The firmware's name ("AIO v4 + TDCP, Nov 26 2026"), or null when no module answers (no update support).</summary>
        public string Query(int timeoutMs = 1500)
        {
            lock (sync) target = null;   // asked on the whole subnet
            byte[] a = Ask('Q', null, timeoutMs);
            if (a == null || a[1] != 0 || a.Length < 4) return null;
            lock (sync) target = answerFrom;
            return Encoding.ASCII.GetString(a, 4, a.Length - 4);
        }

        private static string Status(byte s)
        {
            switch (s)
            {
                case 1: return "bad message";
                case 2: return "image too big";
                case 3: return "not started";
                case 4: return "piece out of order";
                case 5: return "CRC does not match - nothing was installed";
                case 6: return "not a Teensy 4 firmware - nothing was installed";
                default: return "error " + s;
            }
        }

        /// <summary>Sends and installs the firmware. progress: 0..1 and a line to show.</summary>
        public void Update(IntelHex hex, Action<double, string> progress, CancellationToken cancel)
        {
            if (Module == null) throw new IOException("No module found: ask it first");
            byte[] image = hex.Image(FlashBase, MaxImage);
            uint crc = IntelHex.Crc32(image);

            progress?.Invoke(0, "Making room on the module");
            var begin = new byte[8];
            BitConverter.GetBytes((uint)image.Length).CopyTo(begin, 0);
            BitConverter.GetBytes(crc).CopyTo(begin, 4);
            byte[] a = Ask('B', begin, 15000, null, false);
            if (a == null) throw new IOException("The module did not answer: is a firmware with network update on it?");
            if (a[1] != 0) throw new IOException("The module refused: " + Status(a[1]));

            for (int offset = 0; offset < image.Length; offset += Piece)
            {
                if (cancel.IsCancellationRequested)
                    throw new OperationCanceledException("Stopped: the module keeps its firmware (nothing was installed)");
                int len = Math.Min(Piece, image.Length - offset);
                var payload = new byte[6 + len];
                BitConverter.GetBytes((uint)offset).CopyTo(payload, 0);
                BitConverter.GetBytes((ushort)len).CopyTo(payload, 4);
                Array.Copy(image, offset, payload, 6, len);
                int expected = offset;
                byte[] d = Ask('D', payload, 3000, r => r.Length >= 6 && BitConverter.ToInt32(r, 2) == expected);
                if (d == null) throw new IOException("A piece was not confirmed - the module keeps its firmware");
                if (d[1] != 0) throw new IOException("The module refused a piece: " + Status(d[1]));
                progress?.Invoke((double)(offset + len) / image.Length, "Sending " + (offset + len) / 1024 + " / " + image.Length / 1024 + " KB");
            }

            progress?.Invoke(1, "Checking and installing on the module");
            byte[] e = Ask('E', null, 5000, null, false);
            if (e == null) throw new IOException("No answer to the end - check the module (it may be installing)");
            if (e[1] != 0) throw new IOException("Not installed: " + Status(e[1]));
            progress?.Invoke(1, "Installing: do not switch off for 30 seconds");
        }
    }
}
