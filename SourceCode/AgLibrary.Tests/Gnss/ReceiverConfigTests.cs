using AgLibrary.Gnss;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AgLibrary.Tests.Gnss
{
    public class ReceiverConfigTests
    {
        [Test]
        public void UbxFrame_MonVerPollHasTheKnownChecksum()
        {
            Assert.That(ReceiverConfig.PollMonVer(), Is.EqualTo(new byte[] { 0xB5, 0x62, 0x0A, 0x04, 0x00, 0x00, 0x0E, 0x34 }));
        }

        [Test]
        public void ValueSize_FromTheKey()
        {
            Assert.That(ReceiverConfig.ValueSize(0x10110013), Is.EqualTo(1));   // L (bit)
            Assert.That(ReceiverConfig.ValueSize(0x209102A5), Is.EqualTo(1));   // U1
            Assert.That(ReceiverConfig.ValueSize(0x30210001), Is.EqualTo(2));   // U2
            Assert.That(ReceiverConfig.ValueSize(0x40520001), Is.EqualTo(4));   // U4
            Assert.That(ReceiverConfig.ValueSize(0x50110062), Is.EqualTo(8));   // R8
        }

        [Test]
        public void ValSet_TwoItemsInOneFrame()
        {
            var frames = ReceiverConfig.ValSetFrames(new[]
            {
                new KeyValuePair<uint, ulong>(ReceiverConfig.KeyRawxUart1, 1),
                new KeyValuePair<uint, ulong>(0x40520001, 460800)
            }, ReceiverConfig.LayerRam | ReceiverConfig.LayerFlash);
            Assert.That(frames.Count, Is.EqualTo(1));
            byte[] f = frames[0];
            Assert.That(f[2], Is.EqualTo(0x06));
            Assert.That(f[3], Is.EqualTo(0x8A));
            int len = f[4] | (f[5] << 8);
            Assert.That(len, Is.EqualTo(4 + 4 + 1 + 4 + 4));
            Assert.That(f[7], Is.EqualTo(0x05));   // layers
            Assert.That(f[10], Is.EqualTo(0xA5));   // first key, little endian
            Assert.That(f[14], Is.EqualTo(1));
            Assert.That(f[19] | (f[20] << 8) | (f[21] << 16), Is.EqualTo(460800));
        }

        [Test]
        public void ValSet_SplitsAt64Items()
        {
            var items = Enumerable.Range(0, 130).Select(i => new KeyValuePair<uint, ulong>(0x20910000u + (uint)i, 0));
            Assert.That(ReceiverConfig.ValSetFrames(items, ReceiverConfig.LayerRam).Count, Is.EqualTo(3));
        }

        [Test]
        public void UcenterDump_ReadsKeysAndValues()
        {
            // CFG-VALGET answer: version 1, layer 0, position 0, then U1 key 209102A5 = 1 and U4 key 40520001 = 460800
            string dump = "MON-VER - 0A 04 00 00\n" +
                          "CFG-VALGET - 06 8B 11 00 01 00 00 00 A5 02 91 20 01 01 00 52 40 00 08 07 00\n";
            var items = ReceiverConfig.ParseUcenterDump(dump);
            Assert.That(items.Count, Is.EqualTo(2));
            Assert.That(items[0].Key, Is.EqualTo(0x209102A5u));
            Assert.That(items[0].Value, Is.EqualTo(1ul));
            Assert.That(items[1].Key, Is.EqualTo(0x40520001u));
            Assert.That(items[1].Value, Is.EqualTo(460800ul));
        }

        [Test]
        public void Ack_AndNak()
        {
            byte[] ack = ReceiverConfig.UbxFrame(0x05, 0x01, new byte[] { 0x06, 0x8A });
            Assert.That(ReceiverConfig.TryParseAck(ack, out bool ok, out byte cls, out byte id), Is.True);
            Assert.That(ok, Is.True);
            Assert.That(cls, Is.EqualTo(0x06));
            Assert.That(id, Is.EqualTo(0x8A));
            byte[] nak = ReceiverConfig.UbxFrame(0x05, 0x00, new byte[] { 0x06, 0x8A });
            Assert.That(ReceiverConfig.TryParseAck(nak, out ok, out _, out _), Is.True);
            Assert.That(ok, Is.False);
        }

        [Test]
        public void MonVer_SaysUblox()
        {
            var payload = new byte[40 + 30];
            Encoding.ASCII.GetBytes("EXT CORE 1.00").CopyTo(payload, 0);
            Encoding.ASCII.GetBytes("00190000").CopyTo(payload, 30);
            Encoding.ASCII.GetBytes("MOD=ZED-F9P").CopyTo(payload, 40);
            byte[] f = ReceiverConfig.UbxFrame(0x0A, 0x04, payload);
            Assert.That(ReceiverConfig.TryParseMonVer(f, out string text), Is.True);
            Assert.That(text, Does.Contain("ZED-F9P"));
            Assert.That(ReceiverConfig.KindFromAnswer(f), Is.EqualTo(ReceiverKind.UbloxF9P));
        }

        [Test]
        public void Unicore_AnswerAndKind()
        {
            Assert.That(ReceiverConfig.TryParseUnicoreAnswer("$command,GPGGA COM1 0.1,response: OK*45", out string cmd, out bool ok), Is.True);
            Assert.That(cmd, Is.EqualTo("GPGGA COM1 0.1"));
            Assert.That(ok, Is.True);
            Assert.That(ReceiverConfig.TryParseUnicoreAnswer("$command,FOO,response: PARSING FAILD NO MATCHING FUNC*12", out _, out ok), Is.True);
            Assert.That(ok, Is.False);
            Assert.That(ReceiverConfig.KindFromAnswer(Encoding.ASCII.GetBytes("#VERSIONA,79,GPS,FINE,2190,1;\"UM982\",\"R4.10Build11833\"")),
                Is.EqualTo(ReceiverKind.Um982));
        }

        [Test]
        public void Unicore_FileCommands()
        {
            var cmds = ReceiverConfig.ParseUnicoreCommands("﻿Version 0.1\r\nCONFIG COM1 460800\r\n\r\nGPGGA COM1 0.1 \r\nSAVECONFIG");
            Assert.That(cmds, Is.EqualTo(new[] { "CONFIG COM1 460800", "GPGGA COM1 0.1", "SAVECONFIG" }));
            Assert.That(ReceiverConfig.UnicoreCommand("GPGGA COM1 0.1"), Is.EqualTo(Encoding.ASCII.GetBytes("GPGGA COM1 0.1\r\n")));
        }
    }
}
