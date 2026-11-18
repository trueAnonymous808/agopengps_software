using AgLibrary.Localization;
using AgOpenGPS.Core.Translations;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AgOpenGPS
{
    public partial class FormConfig
    {
        private void HideSubMenu()
        {
            panelVehicleSubMenu.Visible = false;
            panelToolSubMenu.Visible = false;
            panelDataSourcesSubMenu.Visible = false;
            panelArduinoSubMenu.Visible = false;
        }

        private void ShowSubMenu(Panel subMenu, Button btn)
        {
            ClearVehicleSubBackgrounds();
            ClearToolSubBackgrounds();
            ClearMachineSubBackgrounds();
            ClearDataSubBackgrounds();
            ClearNoSubBackgrounds();

            if (subMenu.Visible == false)
            {
                HideSubMenu();
                subMenu.Visible = true;
                if (subMenu.Name == "panelVehicleSubMenu")
                {
                    tab1.SelectedTab = tabVConfig;
                }
                else if (subMenu.Name == "panelToolSubMenu")
                {
                    tab1.SelectedTab = tabTConfig;
                }
                else if (subMenu.Name == "panelDataSourcesSubMenu")
                {
                    tab1.SelectedTab = tabDHeading;
                }
                else if (subMenu.Name == "panelArduinoSubMenu")
                {
                    tab1.SelectedTab = tabAMachine;
                }
                else if (btn.Name == "btnUTurn") tab1.SelectedTab = tabUTurn;
                else if (btn.Name == "btnFeatureHides") tab1.SelectedTab = tabBtns;
                else if (btn.Name == "btnDisplay") tab1.SelectedTab = tabDisplay;
            }
            else
            {
                tab1.SelectedTab = tabSummary;
                subMenu.Visible = false;
            }
        }

        private void UpdateSummary()
        {
            configSummaryControl.UpdateSummary(mf);
            labelCurrentVehicle.Text = "Vehicle: " + RegistrySettings.vehicleProfileName;
            labelCurrentTool.Text = "Tool: " + RegistrySettings.toolProfileName;
        }

        #region No Sub menu Buttons

        private void ClearNoSubBackgrounds()
        {
            btnTram.BackColor = UiTheme.MenuInactive;
            btnUTurn.BackColor = UiTheme.MenuInactive;
            btnDisplay.BackColor = UiTheme.MenuInactive;
            btnFeatureHides.BackColor = UiTheme.MenuInactive;
        }
        private void btnTram_Click(object sender, EventArgs e)
        {
            HideSubMenu();
            ClearNoSubBackgrounds();
            if (tab1.SelectedTab == tabTram)
            {
                tab1.SelectedTab = tabSummary;
            }
            else
            {
                tab1.SelectedTab = tabTram;
                btnTram.BackColor = UiTheme.MenuActive;
            }
        }

        private void btnUTurn_Click(object sender, EventArgs e)
        {
            HideSubMenu();
            ClearNoSubBackgrounds();
            if (tab1.SelectedTab == tabUTurn)
            {
                tab1.SelectedTab = tabSummary;
            }
            else
            {
                tab1.SelectedTab = tabUTurn;
                btnUTurn.BackColor = UiTheme.MenuActive;
            }
        }

        private void btnFeatureHides_Click(object sender, EventArgs e)
        {
            HideSubMenu();
            ClearNoSubBackgrounds();
            if (tab1.SelectedTab == tabBtns)
            {
                tab1.SelectedTab = tabSummary;
            }
            else
            {
                tab1.SelectedTab = tabBtns;
                btnFeatureHides.BackColor = UiTheme.MenuActive;
            }
        }

        private void btnDisplay_Click(object sender, EventArgs e)
        {
            HideSubMenu();
            ClearNoSubBackgrounds();
            if (tab1.SelectedTab == tabDisplay)
            {
                tab1.SelectedTab = tabSummary;
            }
            else
            {
                tab1.SelectedTab = tabDisplay;
                btnDisplay.BackColor = UiTheme.MenuActive;
            }
        }

        #endregion

        #region Vehicle Sub Menu Btns
        private void btnVehicle_Click(object sender, EventArgs e)
        {
            ShowSubMenu(panelVehicleSubMenu, btnVehicle);
            btnSubVehicleType.BackColor = UiTheme.MenuActive;
            UpdateSummary();
        }

        private void ClearVehicleSubBackgrounds()
        {
            btnSubVehicleType.BackColor = UiTheme.MenuInactive;
            btnSubAntenna.BackColor = UiTheme.MenuInactive;
            btnSubDimensions.BackColor = UiTheme.MenuInactive;
            //btnSubGuidance.BackColor = UiTheme.MenuInactive;
        }
        private void btnSubVehicleType_Click(object sender, EventArgs e)
        {
            ClearVehicleSubBackgrounds();
            tab1.SelectedTab = tabVConfig;
            btnSubVehicleType.BackColor = UiTheme.MenuActive;
        }

        private void btnSubDimensions_Click(object sender, EventArgs e)
        {
            ClearVehicleSubBackgrounds();
            tab1.SelectedTab = tabVDimensions;
            btnSubDimensions.BackColor = UiTheme.MenuActive;
        }

        private void btnSubAntenna_Click(object sender, EventArgs e)
        {
            ClearVehicleSubBackgrounds();
            tab1.SelectedTab = tabVAntenna;
            btnSubAntenna.BackColor = UiTheme.MenuActive;
        }

        private void btnSubGuidance_Click(object sender, EventArgs e)
        {
            ClearVehicleSubBackgrounds();
            tab1.SelectedTab = tabVGuidance;
            //btnSubGuidance.BackColor = UiTheme.MenuActive;               
        }

        #endregion Region

        #region Tool Sub Menu
        private void btnTool_Click(object sender, EventArgs e)
        {
            ShowSubMenu(panelToolSubMenu, btnTool);
            btnSubToolType.BackColor = UiTheme.MenuActive;
        }

        private void ClearToolSubBackgrounds()
        {
            btnSubToolType.BackColor = UiTheme.MenuInactive;
            btnSubHitch.BackColor = UiTheme.MenuInactive;
            btnSubSections.BackColor = UiTheme.MenuInactive;
            btnSubSwitches.BackColor = UiTheme.MenuInactive;
            btnSubToolSettings.BackColor = UiTheme.MenuInactive;
            btnSubToolOffset.BackColor = UiTheme.MenuInactive;
            btnSubPivot.BackColor = UiTheme.MenuInactive;
        }

        private void btnSubToolType_Click(object sender, EventArgs e)
        {
            ClearToolSubBackgrounds();
            tab1.SelectedTab = tabTConfig;
            btnSubToolType.BackColor = UiTheme.MenuActive;
        }

        private void btnSubHitch_Click(object sender, EventArgs e)
        {
            ClearToolSubBackgrounds();
            tab1.SelectedTab = tabTHitch;
            btnSubHitch.BackColor = UiTheme.MenuActive;
        }

        private void btnSubToolOffset_Click
            (object sender, EventArgs e)
        {
            ClearToolSubBackgrounds();
            tab1.SelectedTab = tabToolOffset;
            btnSubToolOffset.BackColor = UiTheme.MenuActive;
        }

        private void btnSubPivot_Click(object sender, EventArgs e)
        {
            ClearToolSubBackgrounds();
            tab1.SelectedTab = tabToolPivot;
            btnSubPivot.BackColor = UiTheme.MenuActive;
        }

        private void btnSubSections_Click(object sender, EventArgs e)
        {
            ClearToolSubBackgrounds();
            tab1.SelectedTab = tabTSections;
            btnSubSections.BackColor = UiTheme.MenuActive;
        }

        private void btnSubSwitches_Click(object sender, EventArgs e)
        {
            ClearToolSubBackgrounds();
            tab1.SelectedTab = tabTSwitches;
            btnSubSwitches.BackColor = UiTheme.MenuActive;
        }

        private void btnSubToolSettings_Click(object sender, EventArgs e)
        {
            ClearToolSubBackgrounds();
            tab1.SelectedTab = tabTSettings;
            btnSubToolSettings.BackColor = UiTheme.MenuActive;
        }
        #endregion

        #region SubMenu Data Sources

        private void ClearDataSubBackgrounds()
        {
            btnSubHeading.BackColor = UiTheme.MenuInactive;
            btnSubRoll.BackColor = UiTheme.MenuInactive;
        }
        private void btnDataSources_Click(object sender, EventArgs e)
        {
            ShowSubMenu(panelDataSourcesSubMenu, btnDataSources);
            btnSubHeading.BackColor = UiTheme.MenuActive;
        }

        private void btnSubHeading_Click(object sender, EventArgs e)
        {
            ClearDataSubBackgrounds();
            tab1.SelectedTab = tabDHeading;
            btnSubHeading.BackColor = UiTheme.MenuActive;
        }

        private void btnSubRoll_Click(object sender, EventArgs e)
        {
            ClearDataSubBackgrounds();
            tab1.SelectedTab = tabDRoll;
            btnSubRoll.BackColor = UiTheme.MenuActive;
        }

        #endregion

        #region Module
        private void ClearMachineSubBackgrounds()
        {
            btnMachineModule.BackColor = UiTheme.MenuInactive;
            btnMachineRelay.BackColor = UiTheme.MenuInactive;
        }

        private void btnArduino_Click(object sender, EventArgs e)
        {
            ShowSubMenu(panelArduinoSubMenu, btnArduino);
            btnMachineModule.BackColor = UiTheme.MenuActive;
        }

        private void btnMachineModule_Click(object sender, EventArgs e)
        {
            ClearMachineSubBackgrounds();
            tab1.SelectedTab = tabAMachine;
            btnMachineModule.BackColor = UiTheme.MenuActive;
        }

        private void btnMachineRelay_Click(object sender, EventArgs e)
        {
            ClearMachineSubBackgrounds();
            tab1.SelectedTab = tabRelay;
            btnMachineRelay.BackColor = UiTheme.MenuActive;
        }
        #endregion
    }
}
