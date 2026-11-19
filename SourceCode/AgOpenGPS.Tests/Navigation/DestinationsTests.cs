using AgOpenGPS.Navigation;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;

namespace AgOpenGPS.Tests.Navigation
{
    public class DestinationsTests
    {
        private string folder;

        [SetUp]
        public void MakeFields()
        {
            folder = Path.Combine(Path.GetTempPath(), "agog-nav-fields-" + Guid.NewGuid().ToString("N"));
            WriteField("Far", "47.100,19.000");
            WriteField("Near", "47.010,19.000");
            Directory.CreateDirectory(Path.Combine(folder, "NoFieldFile"));
        }

        [TearDown]
        public void RemoveFields()
        {
            Directory.Delete(folder, true);
        }

        private void WriteField(string name, string startFix)
        {
            string dir = Path.Combine(folder, name);
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "Field.txt"), new[]
            {
                "2026-November-18 04:48:41 AM", "$FieldDir", name, "$Offsets", "0,0", "Convergence", "0", "StartFix", startFix
            });
        }

        [Test]
        public void Fields_NearestFirstAndOnlyRealFields()
        {
            List<Destination> fields = Destinations.Fields(folder, 47.000, 19.000);
            Assert.That(fields.Count, Is.EqualTo(2));
            Assert.That(fields[0].Name, Is.EqualTo("Near"));
            Assert.That(fields[0].Lat, Is.EqualTo(47.010));
            Assert.That(fields[0].DistanceKm, Is.EqualTo(1.11).Within(0.02));
            Assert.That(fields[1].Name, Is.EqualTo("Far"));
        }

        [Test]
        public void Fields_MissingFolderGivesAnEmptyList()
        {
            Assert.That(Destinations.Fields(Path.Combine(folder, "nothing"), 47, 19), Is.Empty);
        }

        [Test]
        public void ParseNominatim_ReadsNamesAndPositions()
        {
            const string json = @"[
                { ""lat"": ""47.4979"", ""lon"": ""19.0402"", ""display_name"": ""Budapest, Magyarország"" },
                { ""lat"": ""46.2530"", ""lon"": ""20.1414"", ""display_name"": ""Szeged, Csongrád-Csanád"" } ]";
            List<Destination> found = Destinations.ParseNominatim(json);
            Assert.That(found.Count, Is.EqualTo(2));
            Assert.That(found[0].Name, Is.EqualTo("Budapest, Magyarország"));
            Assert.That(found[0].Lat, Is.EqualTo(47.4979));
            Assert.That(found[1].Lon, Is.EqualTo(20.1414));
        }

        [Test]
        public void NominatimUrl_EscapesTheQuery()
        {
            string url = Destinations.NominatimUrl("Fő utca 1, Kecel", "hu");
            Assert.That(url, Does.StartWith("https://nominatim.openstreetmap.org/search?"));
            Assert.That(url, Does.Contain("q=F%C5%91%20utca%201%2C%20Kecel"));
            Assert.That(url, Does.Contain("format=jsonv2"));
            Assert.That(url, Does.Contain("limit=8"));
            Assert.That(url, Does.Contain("accept-language=hu"));
        }
    }
}
