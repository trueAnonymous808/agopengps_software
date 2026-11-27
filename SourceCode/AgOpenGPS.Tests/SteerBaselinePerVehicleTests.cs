using AgOpenGPS.Properties;
using NUnit.Framework;
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace AgOpenGPS.Tests
{
    // The fully automatic tuning's starting values belong to a vehicle profile: loading another profile brings its
    // own, or none - never the previous vehicle's.
    public class SteerBaselinePerVehicleTests
    {
        private string folder, oldFolder, oldProfile;

        [SetUp]
        public void Folder()
        {
            folder = Path.Combine(Path.GetTempPath(), "agog-veh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            oldFolder = RegistrySettings.vehiclesDirectory;
            oldProfile = RegistrySettings.vehicleProfileName;
            RegistrySettings.vehiclesDirectory = folder;
        }

        [TearDown]
        public void Clean()
        {
            RegistrySettings.vehiclesDirectory = oldFolder;
            RegistrySettings.vehicleProfileName = oldProfile;
            Directory.Delete(folder, true);
        }

        [Test]
        public void Baseline_IsKeptPerVehicleProfile()
        {
            var vs = new VehicleSettings { setSteerAuto_Baseline = "tractor-values" };
            vs.Save("Tractor");
            vs.setSteerAuto_Baseline = "harvester-values";
            vs.Save("Harvester");

            vs.Load("Tractor");
            Assert.That(vs.setSteerAuto_Baseline, Is.EqualTo("tractor-values"));
            vs.Load("Harvester");
            Assert.That(vs.setSteerAuto_Baseline, Is.EqualTo("harvester-values"));
        }

        [Test]
        public void AProfileWithoutABaseline_DoesNotKeepThePreviousOne()
        {
            var vs = new VehicleSettings { setSteerAuto_Baseline = "tractor-values" };
            vs.Save("Tractor");
            vs.Save("Old");
            // a profile saved by an older version: the setting is not in the file at all
            string old = Path.Combine(folder, "Old.xml");
            string xml = File.ReadAllText(old);
            string stripped = Regex.Replace(xml, @"<setting name=""setSteerAuto_Baseline""[\s\S]*?</setting>\s*", "");
            Assert.That(stripped, Is.Not.EqualTo(xml), "the setting was in the file");
            File.WriteAllText(old, stripped);

            vs.Load("Tractor");
            vs.Load("Old");
            Assert.That(vs.setSteerAuto_Baseline, Is.Empty);
        }
    }
}
