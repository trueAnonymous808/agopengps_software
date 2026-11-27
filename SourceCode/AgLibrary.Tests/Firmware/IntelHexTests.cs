using AgLibrary.Firmware;
using NUnit.Framework;
using System;
using System.IO;

namespace AgLibrary.Tests.Firmware
{
    public class IntelHexTests
    {
        // two data records at 0x60000000 via an extended linear address, then the end
        private const string Sample =
            ":020000046000" + "9A\n" +
            ":0400000001020304F2\n" +
            ":02040000AABB95\n" +
            ":00000001FF\n";

        [Test]
        public void Parse_ReadsAddressesAndBytes()
        {
            IntelHex hex = IntelHex.Parse(new StringReader(Sample));
            Assert.That(hex.Count, Is.EqualTo(6));
            Assert.That(hex.Lowest, Is.EqualTo(0x60000000u));
            Assert.That(hex.Highest, Is.EqualTo(0x60000401u));
            Assert.That(hex.TryGet(0x60000002, out byte b), Is.True);
            Assert.That(b, Is.EqualTo(0x03));
        }

        [Test]
        public void Blocks_FillGapsAndSkipEmptyBlocks()
        {
            IntelHex hex = IntelHex.Parse(new StringReader(Sample));
            var blocks = hex.Blocks(0x60000000, 1024, 8126464);
            Assert.That(blocks.Count, Is.EqualTo(2));
            Assert.That(blocks[0][0], Is.EqualTo(0x01));
            Assert.That(blocks[0][4], Is.EqualTo(0xFF));
            Assert.That(blocks[1][0], Is.EqualTo(0xAA));
            Assert.That(blocks[1][1], Is.EqualTo(0xBB));
        }

        [Test]
        public void Blocks_EmptyMiddleBlockIsSkipped()
        {
            string hex = ":020000046000" + "9A\n" + ":0100000011EE\n" + ":0108000022D5\n" + ":00000001FF\n";
            var blocks = IntelHex.Parse(new StringReader(hex)).Blocks(0x60000000, 1024, 8126464);
            Assert.That(blocks.Count, Is.EqualTo(3));
            Assert.That(blocks[1], Is.Null);
            Assert.That(blocks[2][0], Is.EqualTo(0x22));
        }

        [Test]
        public void Image_IsContiguousFromTheStart()
        {
            byte[] image = IntelHex.Parse(new StringReader(Sample)).Image(0x60000000, 0x400000);
            Assert.That(image.Length, Is.EqualTo(0x402));
            Assert.That(image[3], Is.EqualTo(0x04));
            Assert.That(image[4], Is.EqualTo(0xFF));
            Assert.That(image[0x401], Is.EqualTo(0xBB));
        }

        [Test]
        public void Crc32_IsTheStandardOne()
        {
            // the check value of CRC-32/ISO-HDLC, as the firmware computes it too
            Assert.That(IntelHex.Crc32(System.Text.Encoding.ASCII.GetBytes("123456789")), Is.EqualTo(0xCBF43926u));
        }

        [Test]
        public void Parse_BadChecksumIsRefused()
        {
            Assert.Throws<FormatException>(() => IntelHex.Parse(new StringReader(":0400000001020304F3\n:00000001FF\n")));
        }

        [Test]
        public void Parse_NoEndRecordIsRefused()
        {
            Assert.Throws<FormatException>(() => IntelHex.Parse(new StringReader(":0400000001020304F2\n")));
        }

        [Test]
        public void Blocks_OutsideTheBoardIsRefused()
        {
            IntelHex hex = IntelHex.Parse(new StringReader(":0400000001020304F2\n:00000001FF\n"));   // address 0
            Assert.Throws<InvalidOperationException>(() => hex.Blocks(0x60000000, 1024, 8126464));
        }

        [Test]
        public void TheBuiltFirmwaresFitATeensy41()
        {
            // the fork of Config-O-Matic next to this repository, where its FirmwareSource/build.ps1 puts them
            string dir = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "..", "..", "AOGConfigOMatic", "Firmwares", "AgOpenGPS-OTA-RawGNSS"));
            if (!Directory.Exists(dir)) Assert.Ignore("No built firmwares at " + dir);
            string[] files = Directory.GetFiles(dir, "*.hex", SearchOption.AllDirectories);
            Assert.That(files, Is.Not.Empty, dir);
            foreach (string f in files)
            {
                IntelHex hex = IntelHex.Load(f);
                var blocks = hex.Blocks(0x60000000, 1024, 8126464);
                Assert.That(blocks[0], Is.Not.Null, f);
                Assert.That(hex.Count, Is.GreaterThan(100000), f);
            }
        }
    }
}
