using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace AgLibrary.Localization
{
    /// <summary>
    /// Translates the fixed captions of the helper programs (AgIO, GPS_Out, ModSim, Updater, AgDiag), whose texts are
    /// written straight into the form designers, and the hard-coded messages of AgOpenGPS. The texts of each language
    /// are in the gStr resources of AgOpenGPS.Core (entries Ui*_nnnn, see the Language class); English, or a language
    /// without translations, keeps the English texts unchanged. Technical words (PGN, UDP, NTRIP, RTCM, baud, GGA ...) and anything that is not in the
    /// table are left alone. Input fields (text boxes, combo boxes) are never touched, so values and the code that
    /// reads them are not affected.
    /// </summary>
    public static class UiTranslator
    {
        private static Control marshal;
        private static bool active;

        // The language can be changed while the programs run (AgOpenGPS stores it in the registry at once).
        // A watcher on that registry key reports every change, so nothing has to be polled.
        private static volatile Language currentLanguage;
        private static volatile string currentCode;
        private static bool languageKnown;
        private static System.Threading.Thread languageWatcher;

        private static Language CurrentLanguage()
        {
            if (!languageKnown)
            {
                currentCode = LanguageCode();
                currentLanguage = Language.Get(currentCode);
                languageKnown = true;
            }
            return currentLanguage;
        }

        private static bool Translating()
        {
            return CurrentLanguage() != null;
        }

        /// <summary>The language chosen in AgOpenGPS (stored in the registry), or the language this program runs in.</summary>
        public static string LanguageCode()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\AgOpenGPS"))
                {
                    string lang = key?.GetValue("Language") as string;
                    if (!string.IsNullOrEmpty(lang)) return lang;
                }
            }
            catch (Exception) { }

            try
            {
                return System.Threading.Thread.CurrentThread.CurrentUICulture.Name;
            }
            catch (Exception)
            {
                return "en";
            }
        }

        [System.Runtime.InteropServices.DllImport("advapi32.dll")]
        private static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle hKey, bool watchSubtree, int notifyFilter, IntPtr hEvent, bool asynchronous);

        // A window is translated just before it is first activated (shown), so the English captions never appear
        // for a moment; the text hook below catches texts that the program sets later.
        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        private static HookProc activateHook;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private static void StartActivateHook()
        {
            try
            {
                IntPtr handle = IntPtr.Zero;
                activateHook = (nCode, wParam, lParam) =>
                {
                    try
                    {
                        const int HCBT_ACTIVATE = 5;
                        if (nCode == HCBT_ACTIVATE)
                        {
                            Form form = Control.FromHandle(wParam) as Form;
                            if (form != null)
                            {
                                if (Translating()) Apply(form);
                                UiTheme.Apply(form);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // never let the translation disturb the program
                    }
                    return CallNextHookEx(handle, nCode, wParam, lParam);
                };
                const int WH_CBT = 5;
                handle = SetWindowsHookEx(WH_CBT, activateHook, IntPtr.Zero, GetCurrentThreadId());
            }
            catch (Exception)
            {
                // without the hook the windows stay in English
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct CwpRetStruct
        {
            public IntPtr lResult;
            public IntPtr lParam;
            public IntPtr wParam;
            public uint message;
            public IntPtr hwnd;
        }

        private static HookProc textHook;

        // Every caption that the program sets later (a status label, for example) is translated right after it is set.
        private static void StartTextHook()
        {
            try
            {
                IntPtr handle = IntPtr.Zero;
                textHook = (nCode, wParam, lParam) =>
                {
                    try
                    {
                        const uint WM_SETTEXT = 0x000C;
                        if (nCode >= 0 && Translating())
                        {
                            var msg = (CwpRetStruct)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(CwpRetStruct));
                            if (msg.message == WM_SETTEXT)
                            {
                                Control c = Control.FromHandle(msg.hwnd);
                                if (c != null && !(c is TextBoxBase) && !(c is ComboBox) && !(c is ListBox) && !(c is UpDownBase))
                                    SetControl(c);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // never let the translation disturb the program
                    }
                    return CallNextHookEx(handle, nCode, wParam, lParam);
                };
                const int WH_CALLWNDPROCRET = 12;
                handle = SetWindowsHookEx(WH_CALLWNDPROCRET, textHook, IntPtr.Zero, GetCurrentThreadId());
            }
            catch (Exception)
            {
                // texts set later are then translated when the window is shown next time
            }
        }

        // The language was changed while the program runs: restore the texts of the old language, then translate
        // everything that is open into the new one.
        private static void LanguageChanged(Language previous, Language next)
        {
            try
            {
                if (marshal == null || !marshal.IsHandleCreated) return;
                marshal.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        SetUiCulture();
                        if (previous != null)
                        {
                            foreach (Form form in Application.OpenForms) RevertForm(form, previous);
                        }
                        if (next != null)
                        {
                            foreach (Form form in Application.OpenForms) Apply(form);
                        }
                        UiTheme.Refresh();
                    }
                    catch (Exception)
                    {
                    }
                }));
            }
            catch (Exception)
            {
            }
        }

        // Windows opened after a language change read their texts (resources) in the newly chosen language.
        private static void SetUiCulture()
        {
            try
            {
                string lang = null;
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\AgOpenGPS"))
                {
                    lang = key?.GetValue("Language") as string;
                }
                if (string.IsNullOrEmpty(lang)) return;
                System.Threading.Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(lang);
            }
            catch (Exception)
            {
            }
        }

        private static void RevertItems(ToolStripItemCollection items, Language lang)
        {
            foreach (ToolStripItem item in items)
            {
                string en = Original(lang, item.Text);
                if (en != null) item.Text = en;
                en = Original(lang, item.ToolTipText);
                if (en != null) item.ToolTipText = en;
                var drop = item as ToolStripDropDownItem;
                if (drop != null && drop.DropDownItems.Count > 0) RevertItems(drop.DropDownItems, lang);
            }
        }

        private static void RevertForm(Form form, Language lang)
        {
            if (form == null || form.IsDisposed) return;
            try
            {
                string en = Original(lang, form.Text);
                if (en != null) form.Text = en;
            }
            catch (Exception)
            {
            }
            Revert(form.Controls, lang);
        }

        // A value of HKCU\SOFTWARE\AgOpenGPS was set (on the watcher thread).
        private static void OnKeyChanged(ref bool lastNight)
        {
            // the key holds the day / night mode: the windows follow it at once. Only when the mode really changed, or
            // when a window was themed with the other mode in the meantime (AgOpenGPS writes day and then night again
            // within a moment while it starts): other values (profile names, folders ...) must not repaint everything
            bool night = UiTheme.ReadNight();
            if (night != lastNight || UiTheme.ThemedWithOtherMode(night)) ThemeChanged();
            lastNight = night;

            // only a different language needs the texts redone
            string code = LanguageCode();
            if (languageKnown && string.Equals(code, currentCode, StringComparison.OrdinalIgnoreCase)) return;
            Language previous = languageKnown ? currentLanguage : null;
            Language next = Language.Get(code);
            currentCode = code;
            currentLanguage = next;
            languageKnown = true;
            LanguageChanged(previous, next);
        }

        // The day / night mode changed (AgOpenGPS wrote it to the registry): every open window follows on the UI thread.
        private static void ThemeChanged()
        {
            try
            {
                if (marshal == null || !marshal.IsHandleCreated) return;
                marshal.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        UiTheme.Refresh();
                    }
                    catch (Exception)
                    {
                    }
                }));
            }
            catch (Exception)
            {
            }
        }

        private static void StartLanguageWatcher()
        {
            if (languageWatcher != null) return;
            languageWatcher = new System.Threading.Thread(() =>
            {
                bool lastNight = UiTheme.ReadNight();
                // the key is opened again whenever it goes away: a full settings reset deletes it while the helper
                // programs keep running, and they must go on following the language and the day / night mode
                while (true)
                {
                    try
                    {
                        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\AgOpenGPS"))
                        {
                            while (true)
                            {
                                // blocks until a value of the key is set (REG_NOTIFY_CHANGE_LAST_SET); fails once the key is deleted
                                if (RegNotifyChangeKeyValue(key.Handle, false, 4, IntPtr.Zero, false) != 0) break;
                                OnKeyChanged(ref lastNight);
                            }
                        }
                        // the key was deleted (or could not be watched): its values are gone, follow what is there now
                        OnKeyChanged(ref lastNight);
                    }
                    catch (Exception)
                    {
                    }
                    System.Threading.Thread.Sleep(1000);
                }
            });
            languageWatcher.IsBackground = true;
            languageWatcher.Start();
        }


        /// <summary>Call once on the UI thread before Application.Run. Does nothing visible while the language is English.</summary>
        public static void Install()
        {
            if (active) return;
            active = true;
            CurrentLanguage();
            StartLanguageWatcher();
            StartActivateHook();
            StartTextHook();

            // Nothing is polled: windows are translated when they are shown, texts that the program sets later are
            // translated at the moment they are set, and a language change is reported by the registry watcher.
            marshal = new Control();
            IntPtr created = marshal.Handle;   // the handle lets the watcher thread hand work over to the UI thread
        }

        // Drop-in replacements for MessageBox.Show: the native message box is not a Form of the application,
        // so its texts are translated before it is opened.
        public static DialogResult Show(string text)
        {
            return MessageBox.Show(Tr(text));
        }

        public static DialogResult Show(string text, string caption)
        {
            return MessageBox.Show(Tr(text), Tr(caption));
        }

        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons)
        {
            return MessageBox.Show(Tr(text), Tr(caption), buttons);
        }

        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return MessageBox.Show(Tr(text), Tr(caption), buttons, icon);
        }

        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        {
            return MessageBox.Show(Tr(text), Tr(caption), buttons, icon, defaultButton);
        }

        /// <summary>Translates a text without Install(): for programs (AgOpenGPS) that only need their own warnings translated.</summary>
        public static string TrText(string english)
        {
            if (english == null) return null;
            if (!Translating()) return english;
            return Lookup(english) ?? english;
        }

        public static string Tr(string english)
        {
            return TrText(english);
        }

        public static void Apply(Form form)
        {
            if (form == null || form.IsDisposed) return;
            Set(form.Text, t => form.Text = t);
            ApplyControls(form.Controls);
        }

        private static void ApplyControls(Control.ControlCollection controls)
        {
            foreach (Control c in controls)
            {
                // editable / data controls are skipped on purpose
                if (!(c is TextBoxBase) && !(c is ComboBox) && !(c is ListBox) && !(c is UpDownBase))
                {
                    SetControl(c);
                }

                var strip = c as ToolStrip;
                if (strip != null) ApplyItems(strip.Items);

                var list = c as ListView;
                if (list != null)
                {
                    foreach (ColumnHeader h in list.Columns)
                    {
                        ColumnHeader header = h;
                        Set(header.Text, t => header.Text = t);
                    }
                }

                var grid = c as DataGridView;
                if (grid != null)
                {
                    foreach (DataGridViewColumn col in grid.Columns)
                    {
                        DataGridViewColumn column = col;
                        Set(column.HeaderText, t => column.HeaderText = t);
                    }
                }

                if (c.Controls.Count > 0) ApplyControls(c.Controls);
            }
        }

        private static void ApplyItems(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                ToolStripItem it = item;
                Set(it.Text, t => it.Text = t);
                Set(it.ToolTipText, t => it.ToolTipText = t);
                var drop = it as ToolStripDropDownItem;
                if (drop != null && drop.DropDownItems.Count > 0) ApplyItems(drop.DropDownItems);
            }
        }

        // Translated texts are often longer than the English ones, so a translated caption that no longer fits its control is
        // shown in a slightly smaller font (down to about two thirds) instead of being cut off or running into its neighbour.
        private sealed class FontState
        {
            public Font Original;
            public Font Fitted;
            public string English;
            public string Translated;
            // how far the caption was moved to the left to make room for its text; undone relative to where the caption
            // is then, because the layout may have moved it in the meantime (an anchor in a resized window)
            public int GrownLeftBy;
        }

        private static readonly ConditionalWeakTable<Control, FontState> FontStates = new ConditionalWeakTable<Control, FontState>();

        private static void SetControl(Control c)
        {
            string current = c.Text;
            if (string.IsNullOrEmpty(current)) return;
            string tr = Lookup(current);
            if (tr == null || tr == current)
            {
                FitTranslated(c, current);
                return;
            }

            FontState state;
            if (!FontStates.TryGetValue(c, out state))
            {
                state = new FontState { Original = c.Font };
                FontStates.Add(c, state);
            }

            state.English = current;
            state.Translated = tr;
            Remember(tr, current);
            c.Text = tr;
            Fit(c, current, tr, state);
        }

        // A caption the program has already set in the chosen language (from its own gStr resources) is not translated
        // here, but it must fit just the same: its English original is looked up and it is fitted once.
        private static void FitTranslated(Control c, string current)
        {
            if (!(c is Label) && !(c is ButtonBase) && !(c is GroupBox)) return;
            Language lang = CurrentLanguage();
            if (lang == null) return;
            string english = Original(lang, current) ?? OriginalOfCore(lang, current);
            if (string.IsNullOrEmpty(english) || english == current) return;

            FontState state;
            if (!FontStates.TryGetValue(c, out state))
            {
                state = new FontState { Original = c.Font };
                FontStates.Add(c, state);
            }
            else if (state.Translated == current && state.English == english)
            {
                return;   // fitted already: fitting again would move its neighbours a second time
            }

            state.English = english;
            state.Translated = current;
            Fit(c, english, current, state);
        }

        // A caption the program composed around a translated word ("Megművelt:" from gsApplied + ":", "> KI <" from
        // "> " + gsOff + " <"): the English of the word in the middle, with the same marks around it.
        private static string OriginalOfCore(Language lang, string text)
        {
            int start = 0, end = text.Length;
            while (start < end && !char.IsLetterOrDigit(text[start])) start++;
            while (end > start && !char.IsLetterOrDigit(text[end - 1])) end--;
            if (end <= start || (start == 0 && end == text.Length)) return null;
            string core = text.Substring(start, end - start);
            string en = Original(lang, core);
            if (string.IsNullOrEmpty(en) || en == core) return null;
            return text.Substring(0, start) + en + text.Substring(end);
        }

        private static void Revert(Control.ControlCollection controls, Language lang)
        {
            foreach (Control c in controls)
            {
                try
                {
                    FontState state;
                    bool done = false;
                    if (FontStates.TryGetValue(c, out state) && state.Translated != null && c.Text == state.Translated)
                    {
                        done = true;
                        c.Text = state.English;
                        if (!ReferenceEquals(c.Font, state.Original))
                        {
                            Font fitted = state.Fitted;
                            c.Font = state.Original;
                            if (fitted != null) fitted.Dispose();
                            state.Fitted = null;
                        }
                        if (state.GrownLeftBy != 0)
                        {
                            c.Left += state.GrownLeftBy;
                            state.GrownLeftBy = 0;
                        }
                        state.Translated = null;
                    }

                    if (!done && !(c is TextBoxBase) && !(c is ComboBox) && !(c is ListBox) && !(c is UpDownBase))
                    {
                        string en = Original(lang, c.Text);
                        if (en != null) c.Text = en;
                    }

                    var strip = c as ToolStrip;
                    if (strip != null) RevertItems(strip.Items, lang);

                    var list = c as ListView;
                    if (list != null)
                    {
                        foreach (ColumnHeader h in list.Columns)
                        {
                            string en = Original(lang, h.Text);
                            if (en != null) h.Text = en;
                        }
                    }

                    var grid = c as DataGridView;
                    if (grid != null)
                    {
                        foreach (DataGridViewColumn col in grid.Columns)
                        {
                            string en = Original(lang, col.HeaderText);
                            if (en != null) col.HeaderText = en;
                        }
                    }
                }
                catch (Exception)
                {
                }
                if (c.Controls.Count > 0) Revert(c.Controls, lang);
            }
        }

        private static void Fit(Control c, string english, string translated, FontState state)
        {
            if (!(c is Label) && !(c is ButtonBase) && !(c is GroupBox)) return;

            try
            {
                Font original = state.Original;
                // a fixed-size button cannot grow at all, so it may go a little smaller before its caption is cut off
                bool fixedButton = c is ButtonBase && !IsAutoSized(c);
                float min = Math.Max(6f, original.Size * (fixedButton ? 0.55f : 0.65f));
                Font font = original;
                Font created = null;

                // a caption that grew to the left for an earlier text starts again from its own place
                if (state.GrownLeftBy != 0)
                {
                    c.Left += state.GrownLeftBy;
                    state.GrownLeftBy = 0;
                }

                // An auto-sized "Caption:" in front of its value that would run into the value: when the space on its
                // left is free, it grows to the left instead, so its right edge stays where it was and the font stays.
                if (IsAutoSized(c) && RunsIntoNeighbour(c, english, original, translated, original) && TryGrowLeft(c, english, original, translated, original, state))
                {
                    if (!ReferenceEquals(original, c.Font)) c.Font = original;
                    return;
                }

                for (float size = original.Size - 0.5f; !Fits(c, english, translated, font, original) && size >= min; size -= 0.5f)
                {
                    Font next = new Font(original.FontFamily, size, original.Style, original.Unit);
                    if (created != null) created.Dispose();
                    created = next;
                    font = next;
                }

                // an auto-sized caption that still collides with its right-hand neighbours: rather than a tiny font,
                // use a slightly smaller one and push those neighbours to the right, when the window has room
                if (!Fits(c, english, translated, font, original) && IsAutoSized(c))
                {
                    Font alt = new Font(original.FontFamily, Math.Max(6f, original.Size * 0.85f), original.Style, original.Unit);
                    if (TryMakeRoom(c, english, original, translated, alt) || TryGrowLeft(c, english, original, translated, alt, state))
                    {
                        if (created != null) created.Dispose();
                        created = alt;
                        font = alt;
                    }
                    else
                    {
                        alt.Dispose();
                    }
                }

                if (!ReferenceEquals(font, c.Font))
                {
                    c.Font = font;
                    if (state.Fitted != null && !ReferenceEquals(state.Fitted, font)) state.Fitted.Dispose();
                    state.Fitted = ReferenceEquals(font, original) ? null : font;
                }
            }
            catch (Exception)
            {
                // a caption that is a little too long is better than a crash
            }
        }

        private static bool IsAutoSized(Control c)
        {
            return c.Dock == DockStyle.None && ((c is Label && ((Label)c).AutoSize) || (c is ButtonBase && ((ButtonBase)c).AutoSize));
        }

        // Moves an auto-sized caption to the left by as much as its text has grown, so that its right edge stays where the
        // English caption ended, when nothing visible lies in the strip it moves into. Remembers where it was.
        private static bool TryGrowLeft(Control c, string english, Font originalFont, string text, Font font, FontState state)
        {
            try
            {
                Control parent = c.Parent;
                if (parent == null) return false;

                const TextFormatFlags oneLine = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
                int need = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, 0), oneLine).Width + c.Padding.Horizontal + 2;
                int had = TextRenderer.MeasureText(english, originalFont, new Size(int.MaxValue, 0), oneLine).Width + c.Padding.Horizontal;
                int grow = need - had;
                if (grow <= 0) return false;

                int newLeft = c.Left - grow;
                if (newLeft < 2) return false;

                var strip = new Rectangle(newLeft, c.Top, grow, Math.Max(c.Height, 1));
                foreach (Control s in parent.Controls)
                {
                    if (ReferenceEquals(s, c) || !Shown(s)) continue;
                    if (s.Bounds.IntersectsWith(strip)) return false;
                }

                state.GrownLeftBy += c.Left - newLeft;
                c.Left = newLeft;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Whether an auto-sized caption, grown to the width of its new text, would touch a neighbour it did not touch before
        // or run out of its window on the right.
        // (The label has already grown with its new text, so where it was is worked out from the English caption.)
        private static bool RunsIntoNeighbour(Control c, string english, Font originalFont, string text, Font font)
        {
            if (c.Parent == null) return false;
            const TextFormatFlags oneLine = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            Size need = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, 0), oneLine);
            var before = new Rectangle(c.Left, c.Top, PreferredWidth(c, english, originalFont), c.Height);
            var grown = new Rectangle(c.Left, c.Top, PreferredWidth(c, text, font), Math.Max(c.Height, need.Height));
            int width = c.Parent.ClientSize.Width;
            if (grown.Right > width && before.Right <= width) return true;
            foreach (Control s in c.Parent.Controls)
            {
                if (ReferenceEquals(s, c) || !Shown(s)) continue;
                // a neighbour it now covers by more than the English caption did
                if (Rectangle.Intersect(grown, s.Bounds).Width > Rectangle.Intersect(before, s.Bounds).Width &&
                    Rectangle.Intersect(grown, s.Bounds).Height > 0) return true;
            }
            return false;
        }

        // The width an auto-sized label takes with a given text and font, measured on a hidden label with the same
        // settings (the size stored in the designer is not always the size the label really takes).
        [ThreadStatic] private static Label probe;

        private static int PreferredWidth(Control c, string text, Font font)
        {
            var label = c as Label;
            if (label == null)
                return TextRenderer.MeasureText(text, font, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + Extra(c);
            try
            {
                if (probe == null) probe = new Label { AutoSize = true };
                probe.UseMnemonic = label.UseMnemonic;
                probe.UseCompatibleTextRendering = label.UseCompatibleTextRendering;
                probe.FlatStyle = label.FlatStyle;
                probe.BorderStyle = label.BorderStyle;
                probe.Padding = label.Padding;
                probe.Font = font;
                probe.Text = text;
                return probe.GetPreferredSize(Size.Empty).Width;
            }
            catch (Exception)
            {
                return TextRenderer.MeasureText(text, font).Width + c.Padding.Horizontal;
            }
        }

        // How much wider an auto-sized caption is than its bare text (its padding and the margin the label keeps around
        // the text); measured on the control itself, so the width of a caption with another text can be worked out.
        private static int Extra(Control c)
        {
            try
            {
                if (string.IsNullOrEmpty(c.Text)) return c.Padding.Horizontal;
                int bare = TextRenderer.MeasureText(c.Text, c.Font, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                return Math.Max(c.Padding.Horizontal, c.GetPreferredSize(Size.Empty).Width - bare);
            }
            catch (Exception)
            {
                return c.Padding.Horizontal;
            }
        }

        // Whether a control is meant to be visible: its own Visible setting and that of its parents up to the window.
        // (Control.Visible is false for everything while the window itself is not shown yet, which is exactly when the
        // texts are translated, so it cannot be used to find the neighbours a caption must not run into.)
        private static readonly System.Reflection.MethodInfo GetStateMethod = typeof(Control).GetMethod("GetState",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new[] { typeof(int) }, null);

        private static bool Shown(Control c)
        {
            if (GetStateMethod == null) return c.Visible;
            try
            {
                for (Control p = c; p != null && !(p is Form); p = p.Parent)
                    if (!(bool)GetStateMethod.Invoke(p, new object[] { 0x2 })) return false;   // STATE_VISIBLE
                return true;
            }
            catch (Exception)
            {
                return c.Visible;
            }
        }

        // Moves the controls to the right of a caption that has become wider, as far as the parent window allows.
        private static bool TryMakeRoom(Control c, string english, Font originalFont, string text, Font font)
        {
            try
            {
                Control parent = c.Parent;
                if (parent == null) return false;

                Size need = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                int newRight = c.Left + need.Width + c.Padding.Horizontal + 2;
                // the label has already grown with the new text, so measure where its English caption ended
                int oldRight = c.Left + TextRenderer.MeasureText(english, originalFont, new Size(int.MaxValue, 0), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + c.Padding.Horizontal;
                if (newRight <= oldRight) return true;

                int limit = parent.ClientSize.Width - 2;
                var moves = new List<KeyValuePair<Control, int>>();
                if (!Collect(parent, c, oldRight, newRight, limit, moves, 0)) return false;

                foreach (var move in moves) move.Key.Left += move.Value;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool Collect(Control parent, Control caption, int oldRight, int newRight, int limit, List<KeyValuePair<Control, int>> moves, int depth)
        {
            if (depth > 6) return false;

            foreach (Control s in parent.Controls)
            {
                if (ReferenceEquals(s, caption) || !Shown(s)) continue;
                if (s.Left < oldRight - 2) continue;                              // was not to the right of the caption
                if (s.Top >= caption.Bottom || s.Bottom <= caption.Top) continue; // another row
                if (s.Left >= newRight + 3) continue;                             // still clear of it

                if (s.Dock != DockStyle.None) return false;
                foreach (var done in moves) if (ReferenceEquals(done.Key, s)) goto next;

                int shift = newRight + 3 - s.Left;
                if (s.Right + shift > limit) return false;
                moves.Add(new KeyValuePair<Control, int>(s, shift));
                if (!Collect(parent, s, s.Right, s.Right + shift, limit, moves, depth + 1)) return false;
                next:;
            }
            return true;
        }

        // The picture on a button or check box takes room away from its text.
        private static void ReserveImageSpace(Control c, ref int width, ref int height)
        {
            var button = c as ButtonBase;
            if (button == null || button.Image == null) return;

            Image image = button.Image;
            switch (button.TextImageRelation)
            {
                case TextImageRelation.ImageAboveText:
                case TextImageRelation.TextAboveImage:
                    height -= image.Height + 4;
                    break;
                case TextImageRelation.ImageBeforeText:
                case TextImageRelation.TextBeforeImage:
                    width -= image.Width + 4;
                    break;
                default:
                    // text lies over the picture: keep it out of the part the picture covers
                    if (IsMiddle(button.ImageAlign) && !IsMiddle(button.TextAlign))
                        height = Math.Min(height, (c.ClientSize.Height - image.Height) / 2 - 2);
                    else if (IsCenterColumn(button.TextAlign) && !IsCenterColumn(button.ImageAlign))
                        width -= image.Width + 4;
                    break;
            }
        }

        private static bool IsMiddle(ContentAlignment a)
        {
            return a == ContentAlignment.MiddleLeft || a == ContentAlignment.MiddleCenter || a == ContentAlignment.MiddleRight;
        }

        private static bool IsCenterColumn(ContentAlignment a)
        {
            return a == ContentAlignment.TopCenter || a == ContentAlignment.MiddleCenter || a == ContentAlignment.BottomCenter;
        }

        private static bool Fits(Control c, string english, string translated, Font font, Font original)
        {
            const TextFormatFlags oneLine = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

            bool autoSize = (c is Label && ((Label)c).AutoSize) || (c is ButtonBase && ((ButtonBase)c).AutoSize);
            if (autoSize)
            {
                // it grows with the text: keep it about as wide as the English caption was
                int allowed = TextRenderer.MeasureText(english, original, new Size(int.MaxValue, 0), oneLine).Width * 115 / 100 + 4;
                Size need = TextRenderer.MeasureText(translated, font, new Size(int.MaxValue, 0), oneLine);
                if (need.Width > allowed) return false;

                // and it must not grow into a neighbour (an icon, for instance) or out of the window, measured at this font
                return !RunsIntoNeighbour(c, english, original, translated, font);
            }

            bool isButton = c is ButtonBase;
            int availWidth = c.ClientSize.Width - 8;   // the text is drawn with a few pixels of padding on each side
            int availHeight = c.ClientSize.Height - (isButton ? 6 : 0);
            ReserveImageSpace(c, ref availWidth, ref availHeight);
            if (availWidth <= 0 || availHeight <= 0) return true;

            Size wrapped = TextRenderer.MeasureText(translated, font, new Size(availWidth, 0), oneLine | TextFormatFlags.WordBreak);
            if (wrapped.Height > availHeight) return false;

            // a single long word cannot wrap (Chinese, Japanese and Korean text breaks between its characters, so a
            // run of those characters is not one word)
            foreach (string word in translated.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (HasCjk(word)) continue;
                if (TextRenderer.MeasureText(word, font, new Size(int.MaxValue, 0), oneLine).Width > availWidth) return false;
            }
            return true;
        }

        private static bool HasCjk(string s)
        {
            foreach (char ch in s)
            {
                if ((ch >= '⺀' && ch <= '鿿') || (ch >= '가' && ch <= '힯') || (ch >= '豈' && ch <= '﫿') || (ch >= '＀' && ch <= '￯'))
                    return true;
            }
            return false;
        }

        private static void Set(string current, Action<string> assign)
        {
            if (string.IsNullOrEmpty(current)) return;
            string tr = Lookup(current);
            if (tr != null && tr != current)
            {
                Remember(tr, current);
                assign(tr);
            }
        }

        // Every text this program translated, translated -> English. A text built from a pattern or a template
        // ("AgIO v1.0 Profil: default") is not in any table, so a language change restores it from here.
        private static readonly Dictionary<string, string> Applied = new Dictionary<string, string>(StringComparer.Ordinal);

        private static void Remember(string translated, string english)
        {
            lock (Applied)
            {
                if (Applied.Count > 4000) Applied.Clear();
                Applied[translated] = english;
            }
        }

        /// <summary>The English text behind a text on screen: what this program translated, else the language's tables.</summary>
        private static string Original(Language lang, string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            lock (Applied)
            {
                string en;
                if (Applied.TryGetValue(text, out en)) return en;
            }
            return lang.ReverseLookup(text);
        }

        private static string Lookup(string text)
        {
            Language lang = CurrentLanguage();
            return lang == null ? null : lang.Lookup(text);
        }

        /// <summary>
        /// The texts of one language. They are in the gStr resources of AgOpenGPS.Core (gStr.resx holds the English
        /// text, gStr.&lt;code&gt;.resx the translation), under names Ui&lt;kind&gt;_&lt;nnnn&gt;:
        ///   UiS_  exact caption / message
        ///   UiF_  piece of a longer composed text
        ///   UiT_  sentence with changing parts: {} in the English, {0} {1} ... in the translation
        ///   UiP_  the English is a regex; the translation uses {1} (raw group), {1d} (number with the local
        ///         decimal sign), {1t} (translated group)
        /// For UiT_ and UiP_ the number gives the order: the first match wins.
        /// All other gStr texts are used too, but only for whole texts.
        /// </summary>
        private sealed class Language
        {
            public string Code;
            private string decimalSign = ".";
            private readonly Dictionary<string, string> table = new Dictionary<string, string>(StringComparer.Ordinal);
            // the program's other gStr texts: used only for whole texts, not for pieces
            private readonly Dictionary<string, string> resources = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly List<KeyValuePair<string, string>> fragments = new List<KeyValuePair<string, string>>();
            private readonly List<Template> templates = new List<Template>();
            private readonly List<KeyValuePair<System.Text.RegularExpressions.Regex, string>> patterns = new List<KeyValuePair<System.Text.RegularExpressions.Regex, string>>();
            private readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.Ordinal);
            private List<KeyValuePair<string, string>> tableFragments;
            private Dictionary<string, string> reverse;

            private static readonly Dictionary<string, Language> Loaded = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);

            /// <summary>The language for a code such as "de" or "pt-BR"; null for English or when nothing is translated.</summary>
            public static Language Get(string code)
            {
                if (string.IsNullOrEmpty(code) || code.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return null;
                lock (Loaded)
                {
                    Language known;
                    if (Loaded.TryGetValue(code, out known)) return known;

                    var lang = new Language { Code = code };
                    try
                    {
                        lang.decimalSign = new System.Globalization.CultureInfo(code).NumberFormat.NumberDecimalSeparator;
                    }
                    catch (Exception)
                    {
                    }

                    lang.LoadResources();

                    if (lang.table.Count == 0 && lang.resources.Count == 0 && lang.templates.Count == 0 && lang.patterns.Count == 0 && lang.fragments.Count == 0) lang = null;
                    Loaded[code] = lang;
                    return lang;
                }
            }

            private void LoadResources()
            {
                var rm = ProgramResourceManager();
                if (rm == null) return;
                try
                {
                    var en = rm.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, true);
                    if (en == null) return;

                    // this language and its parents ("pt-BR", then "pt"): the more specific text wins
                    var own = new SortedDictionary<string, string>(StringComparer.Ordinal);
                    for (var culture = new System.Globalization.CultureInfo(Code);
                         !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
                    {
                        var local = rm.GetResourceSet(culture, true, false);
                        if (local == null) continue;
                        foreach (System.Collections.DictionaryEntry e in local)
                        {
                            string name = (string)e.Key;
                            string localText = e.Value as string;
                            if (string.IsNullOrEmpty(localText)) continue;
                            if (IsUiName(name))
                            {
                                if (!own.ContainsKey(name)) own[name] = localText;
                                continue;
                            }
                            string enText = en.GetString(name);
                            if (string.IsNullOrWhiteSpace(enText) || localText == enText || resources.ContainsKey(enText)) continue;
                            resources[enText] = localText;
                        }
                    }

                    // in number order, so templates and patterns keep their order
                    foreach (var kv in own)
                    {
                        string english = en.GetString(kv.Key);
                        if (!string.IsNullOrEmpty(english)) Add(kv.Key[2], english, kv.Value);
                    }
                }
                catch (Exception)
                {
                }
            }

            private static bool IsUiName(string name)
            {
                return name.Length > 4 && name[0] == 'U' && name[1] == 'i' && name[3] == '_' && "SFTP".IndexOf(name[2]) >= 0;
            }

            private void Add(char kind, string english, string translation)
            {
                try
                {
                    switch (kind)
                    {
                        case 'S':
                            table[english] = translation;
                            break;
                        case 'F':
                            fragments.Add(new KeyValuePair<string, string>(english, translation));
                            break;
                        case 'T':
                            templates.Add(T(english, translation));
                            break;
                        case 'P':
                            patterns.Add(new KeyValuePair<System.Text.RegularExpressions.Regex, string>(
                                new System.Text.RegularExpressions.Regex(english, System.Text.RegularExpressions.RegexOptions.CultureInvariant), translation));
                            break;
                    }
                }
                catch (Exception)
                {
                    // a broken entry (a bad regex, for example) is skipped, the rest of the language still works
                }
            }

            private static System.Resources.ResourceManager programResources;
            private static bool programResourcesSearched;

            /// <summary>
            /// The gStr texts: first the copy embedded in this program (Localization/UiStrings.targets), so every helper
            /// program has them wherever it is started from; otherwise gStr of AgOpenGPS.Core.
            /// </summary>
            private static System.Resources.ResourceManager ProgramResourceManager()
            {
                if (!programResourcesSearched)
                {
                    programResources = EmbeddedResourceManager() ?? CoreResourceManager();
                    programResourcesSearched = programResources != null;
                }
                return programResources;
            }

            private static System.Resources.ResourceManager EmbeddedResourceManager()
            {
                try
                {
                    var asm = typeof(UiTranslator).Assembly;
                    const string suffix = ".Localization.gStr.resources";
                    foreach (string name in asm.GetManifestResourceNames())
                    {
                        if (name.EndsWith(suffix, StringComparison.Ordinal))
                            return new System.Resources.ResourceManager(name.Substring(0, name.Length - ".resources".Length), asm);
                    }
                }
                catch (Exception)
                {
                }
                return null;
            }

            private static System.Resources.ResourceManager CoreResourceManager()
            {
                try
                {
                    Type t = null;
                    foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        t = asm.GetType("AgOpenGPS.Core.Translations.gStr", false);
                        if (t != null) break;
                    }
                    if (t == null)
                    {
                        // not loaded yet, or a helper program (AgIO ...) that has it next to it in the install folder
                        try
                        {
                            t = System.Reflection.Assembly.Load("AgOpenGPS.Core").GetType("AgOpenGPS.Core.Translations.gStr", false);
                        }
                        catch (Exception)
                        {
                        }
                    }
                    if (t == null) return null;

                    var prop = t.GetProperty("ResourceManager", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    return prop?.GetValue(null, null) as System.Resources.ResourceManager;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            /// <summary>The gStr resources of AgOpenGPS.Core in this language and in English, when that assembly is there.</summary>
            private static bool ProgramResources(string code, out System.Resources.ResourceSet local, out System.Resources.ResourceSet en)
            {
                local = null;
                en = null;
                try
                {
                    var rm = ProgramResourceManager();
                    if (rm == null) return false;
                    local = rm.GetResourceSet(new System.Globalization.CultureInfo(code), true, false);
                    en = rm.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, true);
                    return local != null && en != null;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            public string Lookup(string text)
            {
                if (string.IsNullOrEmpty(text)) return null;

                // the same captions are looked at again and again, so remember the answers
                lock (cache)
                {
                    string known;
                    if (cache.TryGetValue(text, out known)) return known;
                }

                string result = LookupCore(text);

                lock (cache)
                {
                    if (cache.Count > 4000) cache.Clear();
                    cache[text] = result;
                }
                return result;
            }

            private bool Exact(string text, out string tr)
            {
                return table.TryGetValue(text, out tr) || resources.TryGetValue(text, out tr);
            }

            // exact text first; then the same text without its leading/trailing blanks ("Roll: " -> "Dőlés: ")
            private string LookupCore(string text)
            {
                string tr;
                if (Exact(text, out tr)) return tr;

                string core = text.Trim();
                if (core.Length > 0 && core.Length != text.Length && Exact(core, out tr))
                {
                    int start = text.IndexOf(core, StringComparison.Ordinal);
                    return text.Substring(0, start) + tr + text.Substring(start + core.Length);
                }

                string translated = LookupPattern(text) ?? LookupTemplate(text);
                return translated ?? LookupFragments(text);
            }

            private string LookupTemplate(string text)
            {
                foreach (Template t in templates)
                {
                    System.Text.RegularExpressions.Match m = t.Pattern.Match(text);
                    if (!m.Success) continue;

                    string result = t.Translation;
                    for (int i = 1; i < m.Groups.Count; i++)
                    {
                        string value = m.Groups[i].Value;
                        result = result.Replace("{" + (i - 1) + "}", Lookup(value) ?? value);
                    }
                    return result;
                }
                return null;
            }

            // texts with a changing part (numbers, names): the fixed words are translated, the rest is kept
            private string LookupPattern(string text)
            {
                foreach (var rule in patterns)
                {
                    System.Text.RegularExpressions.Match m = rule.Key.Match(text);
                    if (!m.Success) continue;

                    string result = rule.Value;
                    for (int i = m.Groups.Count - 1; i >= 1; i--)
                    {
                        string value = m.Groups[i].Value;
                        result = result.Replace("{" + i + "d}", value.Replace(".", decimalSign));
                        result = result.Replace("{" + i + "t}", Lookup(value) ?? value);
                        result = result.Replace("{" + i + "}", value);
                    }
                    return result;
                }

                // "Roll: 1.5", "Steer: -3.0°" ...: a known caption with a value behind it
                System.Text.RegularExpressions.Match caption = CaptionWithValue.Match(text);
                if (caption.Success)
                {
                    string tr;
                    if (Exact(caption.Groups[1].Value, out tr)) return tr + caption.Groups[2].Value;
                }
                return null;
            }

            private static readonly System.Text.RegularExpressions.Regex CaptionWithValue =
                new System.Text.RegularExpressions.Regex(@"^([^:\r\n]{1,30}:)(\s*.*)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

            private string LookupFragments(string text)
            {
                string result = text;
                foreach (var f in fragments) result = result.Replace(f.Key, f.Value);

                // longer captions that are part of a composed text (a headline in front of an address ...)
                foreach (var f in TableFragments()) result = result.Replace(f.Key, f.Value);
                return result == text ? null : result;
            }

            private List<KeyValuePair<string, string>> TableFragments()
            {
                if (tableFragments == null)
                {
                    var list = new List<KeyValuePair<string, string>>();
                    foreach (var entry in table)
                    {
                        if (entry.Key.Length >= 14 && entry.Key.IndexOf(' ') > 0) list.Add(entry);
                    }
                    list.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
                    tableFragments = list;
                }
                return tableFragments;
            }

            // translated text -> English text: built from the table and from the program's own resources in this
            // language, so a window that is already open can be switched back to English (or to another language)
            public string ReverseLookup(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return null;
                if (reverse == null) reverse = BuildReverse();
                string en;
                if (reverse.TryGetValue(text, out en)) return en;
                string core = text.Trim();
                if (core.Length != text.Length && reverse.TryGetValue(core, out en))
                {
                    int lead = text.IndexOf(core, StringComparison.Ordinal);
                    return text.Substring(0, lead) + en + text.Substring(lead + core.Length);
                }
                return null;
            }

            private Dictionary<string, string> BuildReverse()
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in table)
                {
                    if (kv.Value != kv.Key && !map.ContainsKey(kv.Value)) map[kv.Value] = kv.Key;
                }

                System.Resources.ResourceSet local, en;
                if (ProgramResources(Code, out local, out en))
                {
                    try
                    {
                        foreach (System.Collections.DictionaryEntry e in local)
                        {
                            string localText = e.Value as string;
                            string enText = en.GetString((string)e.Key);
                            if (string.IsNullOrWhiteSpace(localText) || string.IsNullOrWhiteSpace(enText) || localText == enText) continue;
                            if (!map.ContainsKey(localText)) map[localText] = enText;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                return map;
            }
        }

        // Sentences with changing parts: {} is a value, {0}, {1} ... put the values back into the translated text.
        private sealed class Template
        {
            public System.Text.RegularExpressions.Regex Pattern;
            public string Translation;
        }

        private static Template T(string english, string translation)
        {
            string escaped = System.Text.RegularExpressions.Regex.Escape(english.Replace("{}", "\u0001")).Replace("\u0001", "(.*?)");
            return new Template
            {
                Pattern = new System.Text.RegularExpressions.Regex("^" + escaped + "$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Singleline),
                Translation = translation,
            };
        }
    }
}
