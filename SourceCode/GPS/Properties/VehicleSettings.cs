using AgLibrary.Logging;
using AgLibrary.Settings;
using AgOpenGPS.Core.Models;
using System.IO;

namespace AgOpenGPS.Properties
{
    public sealed class VehicleSettings
    {
        private static VehicleSettings settings_ = new VehicleSettings();

        public static VehicleSettings Default
        {
            get { return settings_; }
        }

        // Vehicle dimensions
        public double setVehicle_wheelbase = 3.3;
        public double setVehicle_antennaHeight = 3;
        public double setVehicle_antennaPivot = 0.1;
        public double setVehicle_antennaOffset = 0;
        public double setVehicle_maxSteerAngle = 30;
        public double setVehicle_maxAngularVelocity = 0.64;
        public double setVehicle_trackWidth = 1.9;

        // AutoSteer settings
        public byte setAS_Kp = 50;
        public byte setAS_KpFast = 0;              // Kp from 12 km/h (0: one Kp for all speeds)
        public double setAS_WasLinMiddle = 1;      // steer angle sensor linearity: factor at 12-20 degrees (1: linear)
        public double setAS_WasLinBig = 1;         // and from 20 degrees
        public string setSteerAuto_Baseline = "";  // this vehicle's steering values when fully automatic was switched on
        public string setSteerAuto_ToolTunes = ""; // this vehicle's steering values kept for each implement
        public double setAS_LeadS = 0;             // dead-time compensation of the angle sent (s); 0: off
        public double setAS_RollShiftS = 0;        // roll compensation timing: + the roll arrives late (s)
        public double setAS_RollGain = 1;          // roll compensation: the antenna height that really cancels, as a factor
        public double setAS_PitRateDegS = 4;       // pit mode: how fast the angle sent may change after a rock (deg/s)
        public bool setDir_RememberForward = true; // single antenna: the heading at the start remembered from the last time
        public double setDir_LastHeadingDeg = -1;  // where the front pointed when the tractor was left (-1: nothing)
        public double setDir_LastLat = 0;
        public double setDir_LastLon = 0;
        public double setDir_LastImuDeg = -1;        // the IMU's heading then (-1: none) - the turn since is the IMU's now minus this
        public double setDir_LastModuleMs = -1;      // the module's time since switched on then (-1: unknown)
        public double setDir_LastSavedUtcS = 0;      // when it was saved (s since 1970, UTC)
        public double setDir_AccAxisX = 0;           // the IMU's forward axis as learnt (sum, not unit): forward or reverse at the first move
        public double setDir_AccAxisY = 0;
        public double setDir_AccAxisZ = 0;
        public double setDir_AccWeight = 0;          // how much it learnt from
        public byte setAS_countsPerDegree = 110;
        public byte setAS_minSteerPWM = 25;
        public byte setAS_highSteerPWM = 180;
        public byte setAS_lowSteerPWM = 30;
        public int setAS_wasOffset = 3;
        public double setAS_sideHillComp = 0.0;
        public byte setAS_ackerman = 100;
        public double setAS_ModeXTE = 0.1;
        public int setAS_ModeTime = 1;
        public double setAS_functionSpeedLimit = 12;
        public double setAS_maxSteerSpeed = 15;
        public double setAS_minSteerSpeed = 0;
        public bool setAS_isSteerInReverse = false;

        // IMU settings
        public double setIMU_rollZero = 0.0;
        public double setIMU_rollFilter = 0.0;
        public bool setIMU_invertRoll = false;
        public bool setIMU_isDualAsIMU = false;
        public double setIMU_fusionWeight2 = 0.06;

        // GPS settings
        public string setGPS_headingFromWhichSource = "Fix";
        public double setGPS_forwardComp = 0.15;
        public double setGPS_reverseComp = 0.3;
        public double setGPS_dualHeadingOffset = 0.0;
        public double setGPS_dualReverseDetectionDistance = 0.25;
        public double setGPS_minimumStepLimit = 0.05;

        // Arduino Steer
        public byte setArdSteer_setting0 = 56;
        public byte setArdSteer_setting1 = 0;
        public byte setArdSteer_maxPulseCounts = 3;
        public bool setArdMac_isDanfoss = false;

        // Brands
        public TractorBrand setBrand_TBrand = TractorBrand.AGOpenGPS;
        public HarvesterBrand setBrand_HBrand = HarvesterBrand.AgOpenGPS;
        public ArticulatedBrand setBrand_WDBrand = ArticulatedBrand.AgOpenGPS;

        // Vehicle type
        public int setVehicle_vehicleType = 0;
        public double setVehicle_panicStopSpeed = 0;

        public LoadResult Load(string vehicleFileName)
        {
            string path = Path.Combine(RegistrySettings.vehiclesDirectory, vehicleFileName + ".xml");
            // a load sets only what the file holds: a profile without a baseline must not keep the one before
            setSteerAuto_Baseline = "";
            setSteerAuto_ToolTunes = "";
            setAS_KpFast = 0;
            setAS_WasLinMiddle = 1;
            setAS_WasLinBig = 1;
            setAS_LeadS = 0;
            setAS_RollShiftS = 0;
            setAS_RollGain = 1;
            setAS_PitRateDegS = 4;
            setDir_RememberForward = true;
            setDir_LastHeadingDeg = -1;
            setDir_LastLat = 0;
            setDir_LastLon = 0;
            setDir_LastImuDeg = -1;
            setDir_LastModuleMs = -1;
            setDir_LastSavedUtcS = 0;
            setDir_AccAxisX = 0;
            setDir_AccAxisY = 0;
            setDir_AccAxisZ = 0;
            setDir_AccWeight = 0;
            var result = XmlSettingsHandler.LoadXMLFile(path, this);
            bool loadedFromBackup = false;

            // Fallback to last known good backup when current profile is missing/corrupt.
            if (result == LoadResult.Failed || result == LoadResult.MissingFile)
            {
                string backupPath = Path.ChangeExtension(path, ".last");
                if (File.Exists(backupPath))
                {
                    var backupResult = XmlSettingsHandler.LoadXMLFile(backupPath, this);
                    if (backupResult == LoadResult.Ok)
                    {
                        Log.EventWriter($"Vehicle profile '{vehicleFileName}': main file failed ({result}), loaded from backup '{backupPath}'");
                        result = LoadResult.Ok;
                        loadedFromBackup = true;
                    }
                    else
                    {
                        Log.EventWriter($"Vehicle profile '{vehicleFileName}': backup '{backupPath}' also failed ({backupResult})");
                    }
                }
            }

            if (result == LoadResult.MissingFile)
            {
                // Try loading from old format and migrate
                return CSettingsMigration.MigrateVehicle(vehicleFileName, this);
            }

            // Update registry with the loaded vehicle file name
            if (result == LoadResult.Ok)
            {
                RegistrySettings.vehicleProfileName = vehicleFileName;
            }

            RegistrySettings.vehicleProfileLoadResult = result;
            RegistrySettings.vehicleProfileLoadedFromBackup = loadedFromBackup;

            return result;
        }

        /// <summary>
        /// Save vehicle settings to file using the file name from registry.
        /// Uses "DefaultVehicle" as fallback if registry value is empty.
        /// </summary>
        public void Save()
        {
            // Read file name from registry, use fallback if empty
            string fileName = string.IsNullOrEmpty(RegistrySettings.vehicleProfileName)
                ? "DefaultVehicle"
                : RegistrySettings.vehicleProfileName;

            string path = Path.Combine(RegistrySettings.vehiclesDirectory, fileName + ".xml");

            // Keep a last-known-good copy before writing, unless this profile was
            // recovered from .last (primary .xml was unreadable).
            if (File.Exists(path) && !RegistrySettings.vehicleProfileLoadedFromBackup)
            {
                string backupPath = Path.ChangeExtension(path, ".last");
                File.Copy(path, backupPath, true);
            }

            XmlSettingsHandler.SaveXMLFile(path, this);
            RegistrySettings.vehicleProfileLoadedFromBackup = false;
        }

        /// <summary>
        /// Save vehicle settings to a specific file (used during migration).
        /// This overload saves to a custom file name without updating the registry.
        /// </summary>
        /// <param name="fileName">The file name to save to (without extension)</param>
        public void Save(string fileName)
        {
            string path = Path.Combine(RegistrySettings.vehiclesDirectory, fileName + ".xml");

            // Keep a last-known-good copy before writing.
            if (File.Exists(path))
            {
                string backupPath = Path.ChangeExtension(path, ".last");
                File.Copy(path, backupPath, true);
            }

            XmlSettingsHandler.SaveXMLFile(path, this);
        }

        public void Reset()
        {
            settings_ = new VehicleSettings();
        }
    }
}
