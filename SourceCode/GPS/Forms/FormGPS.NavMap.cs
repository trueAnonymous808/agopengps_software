using AgOpenGPS.Properties;
using System;
using System.IO;

namespace AgOpenGPS
{
    // The navigation map: while no field is open the ground of the 3D view is a map of the surroundings (NavGround)
    // instead of the grid; the tractor, the buttons and everything else in the 3D view stay as they are.
    // While a route is followed the map is on and shows the street map, whatever the display settings say; when the
    // route ends the settings apply again. While a field is open nothing of it runs.
    public partial class FormGPS
    {
        private NavGround navGround;
        private bool navGroundShown;

        /// <summary>True while the map is the ground of the 3D view (the grid is not drawn then).</summary>
        public bool IsNavGroundShown => navGroundShown && navGround != null && navGround.HasMap;

        private string NavGroundSource => NavMapRules.SourceFor(Settings.Default.setNavMap_Source, IsNavigating, Settings.Default.setNavMap_NavigationSource);

        /// <summary>The map shown while a route is followed (navigation menu): saved and applied at once.</summary>
        public void SetNavigationMapSource(string source)
        {
            Settings.Default.setNavMap_NavigationSource = NavMapRules.NormalizeSource(source);
            Settings.Default.Save();
            UpdateNavigationMapVisibility();
        }

        private string NavGroundAttribution => NavGroundSource == NavMapRules.BingSatellite
            ? "(c) Microsoft Bing" : "(c) OpenStreetMap contributors";

        private bool NavGroundWanted => NavMapRules.ShouldShow(Settings.Default.setNavMap_Enabled, isJobStarted, isFirstFixPositionSet, IsNavigating);

        /// <summary>Shows or hides the map for the current state: setting, route, open field, GPS fix.</summary>
        public void UpdateNavigationMapVisibility()
        {
            bool show = NavGroundWanted;
            try
            {
                if (show && navGround == null)
                    navGround = new NavGround(this, Path.Combine(RegistrySettings.baseDirectory, "MapCache"), NavGroundSource);
                if (navGround != null) navGround.SetSource(NavGroundSource);
                if (!show && navGround != null) navGround.Clear();   // a field opened: no map texture kept while working
                navGroundShown = show;
            }
            catch (Exception)
            {
                // the map is a help on the way to the field: it must never stop the program
                navGroundShown = false;
            }
        }

        /// <summary>Called on every GPS fix: follows the state (setting, route), and starts a new map square when the
        /// tractor nears the edge of the present one.</summary>
        public void UpdateNavigationMap()
        {
            if (NavGroundWanted != navGroundShown) UpdateNavigationMapVisibility();
            if (!navGroundShown || navGround == null) return;
            try
            {
                navGround.SetSource(NavGroundSource);   // the street map while a route is followed (no work when unchanged)
                navGround.Update(AppModel.CurrentLatLon);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>Draws the map on the ground (OpenGL paint, after the floor).</summary>
        private void DrawNavigationMap()
        {
            if (!navGroundShown || navGround == null) return;
            try
            {
                navGround.Draw(AppModel.LocalPlane, !isDay);
            }
            catch (Exception)
            {
            }
        }
    }
}
