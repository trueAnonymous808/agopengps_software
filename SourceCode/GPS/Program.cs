using AgLibrary.Logging;
using AgOpenGPS.Forms;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgOpenGPS
{
    internal static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        private static readonly Mutex Mutex = new Mutex(true, "{516-0AC5-B9A1-55fd-A8CE-72F04E6BDE8F}");

        public static readonly string Version = Assembly.GetEntryAssembly().GetName().Version.ToString(3); // Major.Minor.Patch
        public static readonly string SemVer = Application.ProductVersion.Split('+').First();
        public static readonly bool IsPreRelease = Application.ProductVersion.Contains('-');
        public static readonly bool IsDevelopVersion = Application.ProductVersion == "1.0.0.0";

        private static DateTime lastErrorDialogUtc = DateTime.MinValue;

        // Without these, an unexpected exception in an event handler ends in the raw .NET crash dialog.
        // Log it and keep running: the machine is working in a field and the autosteer module has its own timeout.
        private static void InstallExceptionHandlers()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ReportException("UI thread", e.Exception, true);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportException("Background thread", e.ExceptionObject as Exception, false);
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                ReportException("Background task", e.Exception, false);
                e.SetObserved();
            };
        }

        private static void ReportException(string where, Exception ex, bool showDialog)
        {
            try { Log.EventWriter("UNHANDLED EXCEPTION (" + where + "): " + (ex?.ToString() ?? "unknown")); }
            catch (Exception) { /* logging must never throw from here */ }

            // A repeating error (every GPS fix) must not stack up modal dialogs: at most one a minute
            if (!showDialog || (DateTime.UtcNow - lastErrorDialogUtc).TotalSeconds < 60) return;
            lastErrorDialogUtc = DateTime.UtcNow;

            try
            {
                FormDialog.Show(
                    "Error",
                    "An unexpected error occurred and was written to the event log. AgOpenGPS keeps running.\r\n\r\n" + ex?.Message,
                    DialogSeverity.Error);
            }
            catch (Exception) { /* nothing more we can do */ }
        }

        [STAThread]
        private static void Main()
        {
            if (Mutex.WaitOne(TimeSpan.Zero, true))
            {
                RegistrySettings.Load();
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo(RegistrySettings.culture);
                Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(RegistrySettings.culture);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                InstallExceptionHandlers();
                AgLibrary.Localization.UiTranslator.Install();
                Application.Run(new FormGPS());
                // the main window is closed and everything is saved: nothing left (a library's foreground thread,
                // a hook) may keep the process running without a window
                Environment.Exit(0);
            }
            else
            {
                FormDialog.Show(
                    "Warning",
                    "AgOpenGPS is Already Running",
                    DialogSeverity.Warning);
            }
        }
    }
}