using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace AgLibrary.Localization
{
    /// <summary>
    /// Translates the fixed captions of the helper programs (AgIO, GPS_Out, ModSim, Updater, AgDiag), whose texts are
    /// written straight into the form designers. It runs only when the language is Hungarian, so every other language
    /// keeps the English texts unchanged. Technical words (PGN, UDP, NTRIP, RTCM, baud, GGA ...) and anything that is
    /// not in the table are left alone. Input fields (text boxes, combo boxes) are never touched, so values and the
    /// code that reads them are not affected.
    /// </summary>
    public static class UiTranslator
    {
        private static Control marshal;
        private static bool active;

        // The language can be changed while the programs run (AgOpenGPS stores it in the registry at once).
        // A watcher on that registry key reports every change, so nothing has to be polled.
        private static volatile bool languageIsHungarian;
        private static bool languageKnown;
        private static System.Threading.Thread languageWatcher;

        private static bool CurrentHungarian()
        {
            if (!languageKnown)
            {
                languageIsHungarian = IsHungarian();
                languageKnown = true;
            }
            return languageIsHungarian;
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
                        if (nCode == HCBT_ACTIVATE && CurrentHungarian())
                        {
                            Form form = Control.FromHandle(wParam) as Form;
                            if (form != null) Apply(form);
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
                        if (nCode >= 0 && CurrentHungarian())
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

        // The language was changed while the program runs: translate (or restore) everything that is open.
        private static void LanguageChanged()
        {
            try
            {
                if (marshal == null || !marshal.IsHandleCreated) return;
                marshal.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        SetUiCulture();
                        if (CurrentHungarian())
                        {
                            foreach (Form form in Application.OpenForms) Apply(form);
                        }
                        else
                        {
                            foreach (Form form in Application.OpenForms) RevertForm(form);
                        }
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
                if (lang.StartsWith("hu", StringComparison.OrdinalIgnoreCase))
                    System.Threading.Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo("hu");
                else if (lang.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                    System.Threading.Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo("en");
            }
            catch (Exception)
            {
            }
        }

        // Hungarian text -> English text: built from the table and from the program's own Hungarian resources,
        // so a window that is already open can be switched back to English as well.
        private static Dictionary<string, string> reverse;

        private static Dictionary<string, string> BuildReverse()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in Table)
            {
                if (kv.Value != kv.Key && !map.ContainsKey(kv.Value)) map[kv.Value] = kv.Key;
            }

            try
            {
                foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = asm.GetType("AgOpenGPS.Core.Translations.gStr", false);
                    if (t == null) continue;
                    var prop = t.GetProperty("ResourceManager", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    var rm = prop?.GetValue(null, null) as System.Resources.ResourceManager;
                    if (rm == null) break;
                    var hu = rm.GetResourceSet(new System.Globalization.CultureInfo("hu"), true, true);
                    var en = rm.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, true, true);
                    if (hu == null || en == null) break;
                    foreach (System.Collections.DictionaryEntry e in hu)
                    {
                        string huText = e.Value as string;
                        string enText = en.GetString((string)e.Key);
                        if (string.IsNullOrWhiteSpace(huText) || string.IsNullOrWhiteSpace(enText) || huText == enText) continue;
                        if (!map.ContainsKey(huText)) map[huText] = enText;
                    }
                    break;
                }
            }
            catch (Exception)
            {
            }
            return map;
        }

        private static string ReverseLookup(string text)
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

        private static void RevertItems(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                string en = ReverseLookup(item.Text);
                if (en != null) item.Text = en;
                en = ReverseLookup(item.ToolTipText);
                if (en != null) item.ToolTipText = en;
                var drop = item as ToolStripDropDownItem;
                if (drop != null && drop.DropDownItems.Count > 0) RevertItems(drop.DropDownItems);
            }
        }

        private static void RevertForm(Form form)
        {
            if (form == null || form.IsDisposed) return;
            try
            {
                string en = ReverseLookup(form.Text);
                if (en != null) form.Text = en;
            }
            catch (Exception)
            {
            }
            Revert(form.Controls);
        }

        private static void StartLanguageWatcher()
        {
            if (languageWatcher != null) return;
            languageWatcher = new System.Threading.Thread(() =>
            {
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\AgOpenGPS"))
                    {
                        while (true)
                        {
                            // blocks until a value of the key is set (REG_NOTIFY_CHANGE_LAST_SET)
                            if (RegNotifyChangeKeyValue(key.Handle, false, 4, IntPtr.Zero, false) != 0) break;
                            languageIsHungarian = IsHungarian();
                            languageKnown = true;
                            LanguageChanged();
                        }
                    }
                }
                catch (Exception)
                {
                    // without the watcher the language is simply read at start-up
                }
            });
            languageWatcher.IsBackground = true;
            languageWatcher.Start();
        }

        public static bool IsHungarian()
        {
            // The language chosen in AgOpenGPS decides (it is stored in the registry), not the Windows language.
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\AgOpenGPS"))
                {
                    string lang = key?.GetValue("Language") as string;
                    if (!string.IsNullOrEmpty(lang)) return lang.StartsWith("hu", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception) { }

            // nothing stored yet: fall back to the language this program runs in
            try
            {
                return System.Threading.Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName == "hu";
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Call once on the UI thread before Application.Run. Does nothing unless the language is Hungarian.</summary>
        public static void Install()
        {
            if (active) return;
            active = true;
            CurrentHungarian();
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

        /// <summary>The Hungarian text for an English caption, or the caption itself when there is none (or not Hungarian).</summary>

        /// <summary>Translates a text without Install(): for programs (AgOpenGPS) that only need their own warnings translated.</summary>
        public static string TrText(string english)
        {
            if (english == null) return null;
            if (!CurrentHungarian()) return english;
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

        // Hungarian texts are longer than the English ones, so a translated caption that no longer fits its control is
        // shown in a slightly smaller font (down to about two thirds) instead of being cut off or running into its neighbour.
        private sealed class FontState
        {
            public Font Original;
            public Font Fitted;
            public string English;
            public string Hungarian;
        }

        private static readonly ConditionalWeakTable<Control, FontState> FontStates = new ConditionalWeakTable<Control, FontState>();

        private static void SetControl(Control c)
        {
            string current = c.Text;
            if (string.IsNullOrEmpty(current)) return;
            string hu = Lookup(current);
            if (hu == null || hu == current) return;

            FontState state;
            if (!FontStates.TryGetValue(c, out state))
            {
                state = new FontState { Original = c.Font };
                FontStates.Add(c, state);
            }

            state.English = current;
            state.Hungarian = hu;
            c.Text = hu;
            Fit(c, current, hu, state);
        }

        private static void Revert(Control.ControlCollection controls)
        {
            foreach (Control c in controls)
            {
                try
                {
                    FontState state;
                    bool done = false;
                    if (FontStates.TryGetValue(c, out state) && state.Hungarian != null && c.Text == state.Hungarian)
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
                        state.Hungarian = null;
                    }

                    if (!done && !(c is TextBoxBase) && !(c is ComboBox) && !(c is ListBox) && !(c is UpDownBase))
                    {
                        string en = ReverseLookup(c.Text);
                        if (en != null) c.Text = en;
                    }

                    var strip = c as ToolStrip;
                    if (strip != null) RevertItems(strip.Items);

                    var list = c as ListView;
                    if (list != null)
                    {
                        foreach (ColumnHeader h in list.Columns)
                        {
                            string en = ReverseLookup(h.Text);
                            if (en != null) h.Text = en;
                        }
                    }

                    var grid = c as DataGridView;
                    if (grid != null)
                    {
                        foreach (DataGridViewColumn col in grid.Columns)
                        {
                            string en = ReverseLookup(col.HeaderText);
                            if (en != null) col.HeaderText = en;
                        }
                    }
                }
                catch (Exception)
                {
                }
                if (c.Controls.Count > 0) Revert(c.Controls);
            }
        }

        private static void Fit(Control c, string english, string hungarian, FontState state)
        {
            if (!(c is Label) && !(c is ButtonBase) && !(c is GroupBox)) return;

            try
            {
                Font original = state.Original;
                float min = Math.Max(6f, original.Size * 0.65f);
                Font font = original;
                Font created = null;

                for (float size = original.Size - 0.5f; !Fits(c, english, hungarian, font, original) && size >= min; size -= 0.5f)
                {
                    Font next = new Font(original.FontFamily, size, original.Style, original.Unit);
                    if (created != null) created.Dispose();
                    created = next;
                    font = next;
                }

                // an auto-sized caption that still collides with its right-hand neighbours: rather than a tiny font,
                // use a slightly smaller one and push those neighbours to the right, when the window has room
                if (!Fits(c, english, hungarian, font, original) && IsAutoSized(c))
                {
                    Font alt = new Font(original.FontFamily, Math.Max(6f, original.Size * 0.85f), original.Style, original.Unit);
                    if (TryMakeRoom(c, english, original, hungarian, alt))
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
                if (ReferenceEquals(s, caption) || !s.Visible) continue;
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

        private static bool Fits(Control c, string english, string hungarian, Font font, Font original)
        {
            const TextFormatFlags oneLine = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

            bool autoSize = (c is Label && ((Label)c).AutoSize) || (c is ButtonBase && ((ButtonBase)c).AutoSize);
            if (autoSize)
            {
                // it grows with the text: keep it about as wide as the English caption was
                int allowed = TextRenderer.MeasureText(english, original, new Size(int.MaxValue, 0), oneLine).Width * 115 / 100 + 4;
                Size need = TextRenderer.MeasureText(hungarian, font, new Size(int.MaxValue, 0), oneLine);
                if (need.Width > allowed) return false;

                // and it must not grow into a neighbour (an icon, for instance) that it did not touch before
                Rectangle grown = new Rectangle(c.Left, c.Top, Math.Max(c.Width, need.Width + c.Padding.Horizontal), Math.Max(c.Height, need.Height));
                if (c.Parent != null)
                {
                    foreach (Control sibling in c.Parent.Controls)
                    {
                        if (ReferenceEquals(sibling, c) || !sibling.Visible) continue;
                        if (grown.IntersectsWith(sibling.Bounds) && !c.Bounds.IntersectsWith(sibling.Bounds)) return false;
                    }
                }
                return true;
            }

            bool isButton = c is ButtonBase;
            int availWidth = c.ClientSize.Width - 8;   // the text is drawn with a few pixels of padding on each side
            int availHeight = c.ClientSize.Height - (isButton ? 6 : 0);
            ReserveImageSpace(c, ref availWidth, ref availHeight);
            if (availWidth <= 0 || availHeight <= 0) return true;

            Size wrapped = TextRenderer.MeasureText(hungarian, font, new Size(availWidth, 0), oneLine | TextFormatFlags.WordBreak);
            if (wrapped.Height > availHeight) return false;

            // a single long word cannot wrap
            foreach (string word in hungarian.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (TextRenderer.MeasureText(word, font, new Size(int.MaxValue, 0), oneLine).Width > availWidth) return false;
            }
            return true;
        }

        private static void Set(string current, Action<string> assign)
        {
            if (string.IsNullOrEmpty(current)) return;
            string hu = Lookup(current);
            if (hu != null && hu != current) assign(hu);
        }

        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>(StringComparer.Ordinal);

        private static string Lookup(string text)
        {
            // the same captions are looked at again and again (every 150 ms), so remember the answers
            lock (Cache)
            {
                string known;
                if (Cache.TryGetValue(text, out known)) return known;
            }

            string result = LookupCore(text);

            lock (Cache)
            {
                if (Cache.Count > 4000) Cache.Clear();
                Cache[text] = result;
            }
            return result;
        }

        // exact text first; then the same text without its leading/trailing blanks ("Roll: " -> "Dőlés: ")
        private static string LookupCore(string text)
        {
            string hu;
            if (Table.TryGetValue(text, out hu)) return hu;

            string core = text.Trim();
            if (core.Length > 0 && core.Length != text.Length && Table.TryGetValue(core, out hu))
            {
                int start = text.IndexOf(core, StringComparison.Ordinal);
                return text.Substring(0, start) + hu + text.Substring(start + core.Length);
            }

            string translated = LookupPattern(text) ?? LookupTemplate(text);
            return translated ?? LookupFragments(text);
        }

        // Sentences with changing parts: {} is a value, {0}, {1} ... put the values back into the Hungarian text.
        private sealed class Template
        {
            public System.Text.RegularExpressions.Regex Pattern;
            public string Hungarian;
        }

        private static Template T(string english, string hungarian)
        {
            string escaped = System.Text.RegularExpressions.Regex.Escape(english.Replace("{}", "\u0001")).Replace("\u0001", "(.*?)");
            return new Template
            {
                Pattern = new System.Text.RegularExpressions.Regex("^" + escaped + "$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Singleline),
                Hungarian = hungarian,
            };
        }

        private static string LookupTemplate(string text)
        {
            foreach (Template t in Templates)
            {
                System.Text.RegularExpressions.Match m = t.Pattern.Match(text);
                if (!m.Success) continue;

                string result = t.Hungarian;
                for (int i = 1; i < m.Groups.Count; i++)
                {
                    string value = m.Groups[i].Value;
                    result = result.Replace("{" + (i - 1) + "}", Lookup(value) ?? value);
                }
                return result;
            }
            return null;
        }

        private static readonly Template[] Templates =
        {
            T("Update failed: {}\nBackup was restored.", "A frissítés nem sikerült: {0}\nA biztonsági mentés vissza lett állítva."),
            T("Update failed: {}\nBackup restoration also failed!", "A frissítés nem sikerült: {0}\nA biztonsági mentés visszaállítása sem sikerült!"),
            T("Update failed: {}", "A frissítés nem sikerült: {0}"),
            T("{}\nBackup was restored.", "{0}\nA biztonsági mentés vissza lett állítva."),
            T("{}\nBackup restoration also failed!", "{0}\nA biztonsági mentés visszaállítása sem sikerült!"),
            T("Update installed successfully.\n{}\nBackup: {}", "A frissítés sikeresen települt.\n{0}\nBiztonsági mentés: {1}"),
            T("Update available: {} ({})", "Elérhető frissítés: {0} ({1})"),
            T("Latest Version: {} (New!)", "Legújabb verzió: {0} (új!)"),
            T("Current Version: {}", "Jelenlegi verzió: {0}"),
            T("Local File {}", "Helyi fájl {0}"),
            T("Local update found: {}", "Helyi frissítés található: {0}"),
            T("Web update (Local v{} available)", "Internetes frissítés (helyi v{0} is elérhető)"),
            T("Release Notes - {}", "Kiadási megjegyzések – {0}"),
            T("Warning: {}", "Figyelem: {0}"),
            T("{} closed the connection without answering.", "A(z) {0} válasz nélkül megszakította a kapcsolatot."),
            T("Cannot reach {}. Is the base station or caster switched on?", "A(z) {0} nem érhető el. Be van kapcsolva a bázisállomás vagy a szerver?"),
            T("Cannot find {}. Check the address and your internet connection.", "A(z) {0} nem található. Ellenőrizd a címet és az internetkapcsolatot."),
            T("No answer from {}.", "A(z) {0} nem válaszol."),
            T("Cannot reach {} (no route).", "A(z) {0} nem érhető el (nincs útvonal)."),
            T("Cannot connect to {} ({}).", "Nem sikerült csatlakozni ide: {0} ({1})."),
            T("{} closed the connection.", "A(z) {0} megszakította a kapcsolatot."),
            T("Cannot open the radio on {}.", "A rádió nem nyitható meg ezen a porton: {0}."),
            T("The radio on {} stopped. Reconnecting.", "A(z) {0} porton lévő rádió leállt. Újracsatlakozás..."),
            T("{}. The fallback works. Switch to it? You can do that in the NTRIP panel.", "{0}. A tartalék működik. Átváltasz rá? Ezt az NTRIP panelen teheted meg."),
            T("The main service has a problem ({}).", "A fő szolgáltatóval gond van ({0})."),
            T("{} Now using {}. Your GPS may lose RTK for a moment, and your position can shift a little if the two base stations use different coordinates.", "{0} Mostantól ezt használja: {1}. A GPS egy pillanatra elveszítheti az RTK-t, és a pozíció kissé eltolódhat, ha a két bázisállomás eltérő koordinátákat használ."),
            T("Connecting to {}...", "Csatlakozás ide: {0}..."),
            T("Corrections arrive after {} seconds.", "A korrekciós jel {0} másodperc után megérkezett."),
            T("No corrections within {} seconds.", "{0} másodpercen belül nem érkezett korrekciós jel."),
            T("{} messages received, no errors.", "{0} üzenet érkezett, hiba nélkül."),
            T("{} messages received, {} damaged.", "{0} üzenet érkezett, ebből {1} sérült."),
            T("The service answered: {}.", "A szolgáltató válasza: {0}."),
            T("{} observations (MSM{})", "{0}-mérések (MSM{1})"),
            T("Error during shutdown: {}\n\nAttempting force exit...", "Hiba a leállítás közben: {0}\n\nKényszerített kilépés..."),
            T("Field data save failed:\n{}\n\nContinue shutdown anyway? (data may be lost)", "A tábla adatainak mentése nem sikerült:\n{0}\n\nMégis folytatod a kilépést? (az adatok elveszhetnek)"),
            T("Failed to start updater: {}", "A frissítő indítása nem sikerült: {0}"),
            T("Below Minimum Safe Steering Speed: {} Kmh", "A sebesség a biztonságos minimum alatt van: {0} km/h"),
            T("Below Minimum Safe Steering Speed: {} MPH", "A sebesség a biztonságos minimum alatt van: {0} mph"),
            T("Above Maximum Safe Steering Speed: {} Kmh", "A sebesség a biztonságos maximum felett van: {0} km/h"),
            T("Above Maximum Safe Steering Speed: {} MPH", "A sebesség a biztonságos maximum felett van: {0} mph"),
            T("Field Start is {} km From current position", "A tábla kezdőpontja {0} km-re van a jelenlegi pozíciótól"),
            T("New profile '{}' created", "Új profil létrehozva: '{0}'"),
            T("Profile '{}' loaded", "Profil betöltve: '{0}'"),
            T("Applied {}° ({} counts)", "Alkalmazva: {0}° ({1} egység)"),
            T("Error: {}", "Hiba: {0}"),
            T("Downloading... {}%", "Letöltés folyamatban... {0}%"),
            T("Copying... {}%", "Másolás folyamatban... {0}%"),
            T("Installing... {}%", "Telepítés folyamatban... {0}%"),
            T("Failed to download update:\n\n{}", "A frissítés letöltése nem sikerült:\n\n{0}"),
            T("Failed to copy from USB:\n\n{}", "A másolás az USB-ről nem sikerült:\n\n{0}"),
            T("Failed to install update:\n\n{}", "A frissítés telepítése nem sikerült:\n\n{0}"),
            T("An error occurred:\n\n{}", "Hiba történt:\n\n{0}"),
            T("Failed to check for updates: {}", "A frissítések keresése nem sikerült: {0}"),
            T("Download failed: {}", "A letöltés nem sikerült: {0}"),
            T("Installation failed: {}", "A telepítés nem sikerült: {0}"),
            T("Successfully downloaded {}", "Letöltve: {0}"),
            T("Failed to close applications: {}", "A programok bezárása nem sikerült: {0}"),
            T("Failed to restart AgOpenGPS: {}", "Az AgOpenGPS újraindítása nem sikerült: {0}"),
            T("Extracted {} files", "{0} fájl kicsomagolva"),
            T(", skipped {} preserved files", ", {0} fájl kimaradt (megőrzött beállítások)"),
            T(", {} locked files", ", {0} fájl használatban volt"),
            T("Version: {}\n", "Verzió: {0}\n"),
            T("Size: {}\n", "Méret: {0}\n"),
            T("Location: {}", "Hely: {0}"),
            T("Error checking USB: {}", "Hiba az USB ellenőrzésekor: {0}"),
            T("Serial radio on {}", "Soros rádió ezen a porton: {0}"),
            T("Switches by itself after {} failed tries", "Automatikusan vált {0} sikertelen próba után"),
            T("{} mountpoints, nearest first.", "{0} mountpoint, a legközelebbi elöl."),
            T("{} mountpoints.", "{0} mountpoint."),
            T("Can't Find {}", "Nem található: {0}"),
            T("Check failed: {}", "Az ellenőrzés nem sikerült: {0}"),
        };

        // sentences that appear inside longer messages (for example in front of an exception text)
        private static readonly KeyValuePair<string, string>[] Fragments =
        {
            new KeyValuePair<string, string>("\r\n\r\n  Exiting", "\r\n\r\n  Kilépés"),
            new KeyValuePair<string, string>(" profile could not be fully loaded (file may be corrupt).\r\n", " profil nem töltődött be teljesen (a fájl sérült lehet).\r\n"),
            new KeyValuePair<string, string>(" profile was recovered from .last backup because the main file was unreadable.", " profil a .last biztonsági mentésből lett helyreállítva, mert a fő fájl nem volt olvasható."),
            new KeyValuePair<string, string>("Choose the port of the main radio first.", "Előbb válaszd ki a fő rádió portját."),
            new KeyValuePair<string, string>("Fill in the address of the main service first.", "Előbb add meg a fő szolgáltató címét."),
            new KeyValuePair<string, string>("Or switch NTRIP off in the profile list.", "Vagy kapcsold ki az NTRIP-et a profillistában."),
            new KeyValuePair<string, string>("Go to Settings -> COM Ports to Fix", "A javításhoz nyisd meg: Beállítások → COM-portok"),
            new KeyValuePair<string, string>("Current session is using defaults for missing/invalid values.", "A hiányzó vagy hibás értékek helyett az alapértelmezettet használja."),
            new KeyValuePair<string, string>("AgIO profile could not be fully loaded (file may be corrupt).", "Az AgIO profil nem töltődött be teljesen (a fájl sérült lehet)."),
            new KeyValuePair<string, string>("The NTRIP profile \"", "Az NTRIP profil \""),
            new KeyValuePair<string, string>("\" is not on this computer. Choose or make a profile in the NTRIP settings.", "\" nem található ezen a számítógépen. Válassz vagy hozz létre profilt az NTRIP beállításokban."),
            new KeyValuePair<string, string>("\" has no service address yet.", "\" még nem tartalmaz szolgáltatói címet."),
            new KeyValuePair<string, string>("The fallback does not work either: ", "A tartalék forrás sem működik: "),
            new KeyValuePair<string, string>("No corrections for ", "Nincs korrekciós jel már ennyi ideje: "),
            new KeyValuePair<string, string>(" AgIO keeps trying.", " Az AgIO folyamatosan újrapróbálkozik."),
            new KeyValuePair<string, string>(" seconds", " másodperc"),
            new KeyValuePair<string, string>(" minutes", " perc"),
            new KeyValuePair<string, string>("FALLBACK IN USE: the main service is not used now", "TARTALÉK FORRÁS AKTÍV: a fő szolgáltatót most nem használja"),
            new KeyValuePair<string, string>("Base station ID: ", "Bázisállomás azonosítója: "),
            new KeyValuePair<string, string>("Position: ", "Pozíció: "),
            new KeyValuePair<string, string>("Damaged data: ", "Sérült adatcsomagok: "),
            new KeyValuePair<string, string>("Service answered: ", "A szolgáltató válasza: "),
            new KeyValuePair<string, string>("Connecting in ", "Újracsatlakozás ennyi idő múlva: "),
            new KeyValuePair<string, string>("Cannot resolve caster URL: ", "A szerver címe nem oldható fel: "),
            new KeyValuePair<string, string>("]  Simulated Data", "]  Szimulált adatok"),
            new KeyValuePair<string, string>("]   NO DATA", "]   NINCS ADAT"),
            new KeyValuePair<string, string>("The updater must be started from AgOpenGPS.", "A frissítőt az AgOpenGPS-ből kell elindítani."),
            new KeyValuePair<string, string>("Please start AgOpenGPS first and use:", "Kérlek, előbb indítsd el az AgOpenGPS-t, és használd:"),
            new KeyValuePair<string, string>("Menu → Tools → Check for Updates", "Menü → Eszközök → Frissítések keresése"),
            new KeyValuePair<string, string>("This is required to detect your current version.", "Erre azért van szükség, hogy felismerje a jelenlegi verziót."),
            new KeyValuePair<string, string>("Install update from ", "Frissítés telepítése ebből a forrásból: "),
            new KeyValuePair<string, string>("Source: ", "Forrás: "),
            new KeyValuePair<string, string>("This will:", "A következők fognak történni:"),
            new KeyValuePair<string, string>("• Close AgOpenGPS and AgIO", "• Bezárja az AgOpenGPS-t és az AgIO-t"),
            new KeyValuePair<string, string>("• Create a backup of your current installation", "• Biztonsági mentés készül a jelenlegi telepítésről"),
            new KeyValuePair<string, string>("• Install the update", "• Telepíti a frissítést"),
            new KeyValuePair<string, string>("• Restart AgOpenGPS", "• Újraindítja az AgOpenGPS-t"),
            new KeyValuePair<string, string>("Do you want to continue?", "Folytatod?"),
            new KeyValuePair<string, string>("No update file found.", "Nem található frissítőfájl."),
            new KeyValuePair<string, string>("Place AgOpenGPS_X.Y.Z.zip in the root of your USB drive.", "Másold az AgOpenGPS_X.Y.Z.zip fájlt az USB-meghajtó gyökérmappájába."),
        };

        private static string LookupFragments(string text)
        {
            string result = text;
            foreach (var f in Fragments) result = result.Replace(f.Key, f.Value);

            // longer captions that are part of a composed text (a headline in front of an address ...)
            foreach (var f in TableFragments()) result = result.Replace(f.Key, f.Value);
            return result == text ? null : result;
        }

        private static List<KeyValuePair<string, string>> tableFragments;

        private static List<KeyValuePair<string, string>> TableFragments()
        {
            if (tableFragments == null)
            {
                var list = new List<KeyValuePair<string, string>>();
                foreach (var entry in Table)
                {
                    if (entry.Key.Length >= 14 && entry.Key.IndexOf(' ') > 0) list.Add(entry);
                }
                list.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
                tableFragments = list;
            }
            return tableFragments;
        }

        // texts with a changing part (numbers, names): the fixed words are translated, the rest is kept
        private static string LookupPattern(string text)
        {
            foreach (var rule in Patterns)
            {
                System.Text.RegularExpressions.Match m = rule.Key.Match(text);
                if (!m.Success) continue;
                string result = rule.Value(m);
                if (result != null) return result;
            }
            return null;
        }

        // the program prints numbers with a point; Hungarian text uses a comma
        private static string Comma(string number)
        {
            return number.Replace('.', ',');
        }

        private static string Part(System.Text.RegularExpressions.Match m, int group)
        {
            string value = m.Groups[group].Value;
            return Lookup(value) ?? value;
        }

        private static KeyValuePair<System.Text.RegularExpressions.Regex, Func<System.Text.RegularExpressions.Match, string>> Rule(
            string pattern, Func<System.Text.RegularExpressions.Match, string> translate)
        {
            return new KeyValuePair<System.Text.RegularExpressions.Regex, Func<System.Text.RegularExpressions.Match, string>>(
                new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant), translate);
        }

        private static readonly KeyValuePair<System.Text.RegularExpressions.Regex, Func<System.Text.RegularExpressions.Match, string>>[] Patterns =
        {
            Rule(@"^([\d.,]+) kb received$", m => m.Groups[1].Value + " kB adat érkezett"),
            Rule(@"^Last data ([\d.]+) seconds ago\.$", m => "Az utolsó adat " + Comma(m.Groups[1].Value) + " másodperce érkezett."),
            Rule(@"^Last data ([\d.]+) seconds ago\. Slow connection\?$", m => "Az utolsó adat " + Comma(m.Groups[1].Value) + " másodperce érkezett. Lassú lehet a kapcsolat."),
            Rule(@"^Corrections are ([\d.]+) seconds old\.$", m => "A korrekció " + Comma(m.Groups[1].Value) + " másodperces."),
            Rule(@"^Distance to base station: (.+)$", m => "Távolság a bázisállomástól: " + m.Groups[1].Value),
            Rule(@"^Your GPS: fix type (\d+)$", m => "A GPS fix típusa: " + m.Groups[1].Value),
            Rule(@"^(.+) Active \(old client\)$", m => Part(m, 1) + " működik (régi kliens)"),
            Rule(@"^(.+) Active$", m => Part(m, 1) + " működik"),
            Rule(@"^(.+) \(old client\)$", m => Part(m, 1) + " (régi kliens)"),
            Rule(@"^Opening (.+)\.\.\.$", m => m.Groups[1].Value + " megnyitása..."),
            Rule(@"^(.+) waiting for data$", m => m.Groups[1].Value + ": korrekciós jelre vár"),
            Rule(@"^(AgIO  v[\d.]+) Profile: (.*)$", m => m.Groups[1].Value + " Profil: " + m.Groups[2].Value),
            Rule(@"^To (.+)$", m => "Elmentve ide: " + m.Groups[1].Value),
            Rule(@"^Overwrite: (.+)$", m => "Felülírod ezt a profilt: " + m.Groups[1].Value + "?"),
            Rule(@"^Id '(.*)' is not a valid number$", m => "A(z) '" + m.Groups[1].Value + "' azonosító nem szám."),
            Rule(@"^An error occurred: (.*)$", m => "Hiba történt: " + m.Groups[1].Value),
            Rule(@"^UDP Error: (.*)$", m => "UDP hiba: " + m.Groups[1].Value),
            Rule(@"^Load Error: (.*)$", m => "Betöltési hiba: " + m.Groups[1].Value),
            // "Roll: 1.5", "Steer: -3.0°" ...: a known caption with a value behind it
            Rule(@"^([^:\r\n]{1,30}:)(\s*.*)$", m =>
            {
                string hu;
                return Table.TryGetValue(m.Groups[1].Value, out hu) ? hu + m.Groups[2].Value : null;
            }),
        };

        private static readonly Dictionary<string, string> Table = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ---- common ----
            { "OK", "OK" },
            { "Ok", "OK" },
            { "Yes", "Igen" },
            { "No", "Nem" },
            { "Cancel", "Mégsem" },
            { "Close", "Bezár" },
            { "Save", "Mentés" },
            { "Save As:", "Mentés:" },
            { "Delete", "Törlés" },
            { "Edit", "Szerkesztés" },
            { "Rename", "Átnevezés" },
            { "Refresh", "Frissítés" },
            { "Default", "Alapértelmezett" },
            { "Error", "Hiba" },
            { "Help", "Segítség" },
            { "Exit", "Kilépés" },
            { "Open:", "Megnyit:" },
            { "Choose", "Kiválasztás" },
            { "Choose from list...", "Választás a listából..." },
            { "Loading the list...", "Lista betöltése..." },
            { "Ready...", "Kész..." },
            { "Copy", "Másolás" },
            { "Copied", "Másolva" },
            { "Could not copy", "A másolás nem sikerült" },
            { "Copy for support", "Másolás hibajelentéshez" },
            { "Name", "Név" },
            { "Message", "Üzenet" },
            { "Status", "Állapot" },
            { "Options", "Beállítások" },
            { "Show", "Megjelenítés" },
            { "On", "Be" },
            { "Off", "Ki" },
            { "Use", "Használ" },
            { "Only", "Csak" },
            { "Or", "Vagy" },
            { "Time", "Idő" },
            { "Times", "Idők" },
            { "Current", "Jelenlegi" },
            { "Description", "Leírás" },
            { "Command", "Parancs" },
            { "Response", "Válasz" },
            { "Filter", "Szűrő" },
            { "Enter Value", "Érték megadása" },
            { "Enter a Value", "Érték megadása" },
            { "Enter Something", "Adj meg egy értéket" },

            // ---- connection / network ----
            { "Connect", "Csatlakozás" },
            { "Disconnect", "Bontás" },
            { "Not Connected", "Nincs kapcsolat" },
            { "Auto Connect", "Automatikus csatlakozás" },
            { "Connect GPS", "GPS csatlakoztatása" },
            { "Port", "Port" },
            { "Serial Port", "Soros port" },
            { "GPS Port", "GPS port" },
            { "GPS Port 2", "GPS port 2" },
            { "RTCM Port", "RTCM port" },
            { "Network port", "Hálózati port" },
            { "Radio on port", "Rádió a porton:" },
            { "Speed (baud)", "Sebesség (baud)" },
            { "Address", "Cím" },
            { "IP Address", "IP-cím" },
            { "IP Address : Port", "IP-cím : port" },
            { "Hostname", "Gépnév" },
            { "Hostname:", "Gépnév:" },
            { "Password", "Jelszó" },
            { "User name", "Felhasználónév" },
            { "Current Subnet", "Jelenlegi alhálózat" },
            { "Set Subnet", "Beállítás" },
            { "Enter New Subnet Address", "Add meg az új alhálózat címét" },
            { "Auto Fill New Subnet", "Automatikus kitöltés" },
            { "No Adapter For This Subnet", "Ehhez az alhálózathoz nincs hálózati kártya" },
            { "Network Prop", "Hálózat" },
            { "Ethernet Configuration", "Ethernet beállítások" },
            { "Turn UDP On And Hit OK", "Kapcsold be az UDP-t, majd nyomj OK-t" },
            { "UDP Is Off", "Az UDP ki van kapcsolva" },
            { "UDP Off", "UDP ki" },
            { "Transfer Rate", "Átviteli sebesség" },
            { "Last seen", "Utoljára jelentkezett" },
            { "Scanning", "Keresés folyamatban" },
            { "Scan Reply", "Modul válasza" },
            { "Module Scan", "Modulkeresés" },
            { "Module Sim", "Modul szimuláció" },
            { "Device Manager", "Eszközkezelő" },
            { "Check connection", "Kapcsolat ellenőrzése" },
            { "Position sent", "Elküldött pozíció" },
            { "What came in", "Beérkezett adatok" },
            { "failed tries", "sikertelen próbálkozás" },
            { "Send every (s)", "Küldés (másodpercenként)" },
            { "Send empty GSA", "Üres GSA-mondat küldése" },
            { "Send NTRIP To GPS Using:", "NTRIP-korrekció küldése a GPS-nek ezen keresztül:" },
            { "Serial RTCM", "RTCM soros porton" },
            { "Use USB", "Frissítés USB-ről" },
            { "Use Web", "Frissítés internetről" },

            // ---- NTRIP ----
            { "NTRIP settings", "NTRIP beállítások" },
            { "NTRIP details", "NTRIP részletek" },
            { "NTRIP profiles", "NTRIP profilok" },
            { "NTRIP version", "NTRIP verzió" },
            { "NTRIP Errors", "NTRIP hibaüzenetek" },
            { "Choose an NTRIP profile", "Válassz egy NTRIP profilt" },
            { "Use NTRIP client", "NTRIP használata" },
            { "Use the old NTRIP client", "Régi NTRIP kliens" },
            { "Raw TCP stream", "Nyers TCP-adatfolyam" },
            { "Mountpoint", "Mountpoint" },
            { "Mountpoints", "Elérhető mountpointok" },
            { "Choose the base station nearest to your field.", "Válaszd a földedhez legközelebbi bázisállomást." },
            { "Corrections go to", "A korrekciós jel ide kerül:" },
            { "Use a fallback when the main service fails", "Tartalék szolgáltató használata, ha a fő szolgáltató kiesik" },
            { "Switch after", "Váltás ennyi sikertelen próba után:" },
            { "Main", "Fő" },
            { "Radio", "Rádió" },
            { "Radio (Not XBee)", "Rádió (nem XBee)" },
            { "Radio Channel", "Rádiócsatorna" },
            { "Radio Settings", "Rádió beállítások" },
            { "Channel", "Csatorna" },
            { "Channels", "Csatornák" },
            { "A serial radio is not an NTRIP service. Its corrections are used as they arrive. A fallback service takes over when the radio goes quiet.",
              "A soros rádió nem NTRIP szolgáltató: a korrekciós jelet úgy használja fel, ahogy megérkezik. Ha a rádió elnémul, a tartalék szolgáltató veszi át." },

            // ---- position / GPS ----
            { "Latitude", "Szélesség" },
            { "Latitude:", "Szélesség:" },
            { "Longitude", "Hosszúság" },
            { "Longitude:", "Hosszúság:" },
            { "Lat", "Szél." },
            { "Lon", "Hossz." },
            { "Altitude", "Magasság" },
            { "Altitude (m)", "Magasság (m)" },
            { "Elevation:", "Magasság:" },
            { "Elev", "Magas." },
            { "Heading", "Irány" },
            { "Speed", "Sebesség" },
            { "Speed:", "Sebesség:" },
            { "Sats", "Műholdak" },
            { "Sats:", "Műholdak:" },
            { "# Sats", "Műhold" },
            { "Quality", "Fix minőség" },
            { "Quality:", "Fix minőség:" },
            { "FixQual", "Fix minőség" },
            { "Age", "Korrekció kora" },
            { "Age:", "Kor:" },
            { "Dual", "Két antenna" },
            { "Roll", "Oldaldőlés" },
            { "Roll Corrected", "Dőléskorrigált" },
            { "Roll:", "Oldaldőlés:" },
            { "Pitch", "Bólintás" },
            { "Yaw", "Elfordulás" },
            { "YawRate", "Elfordulási sebesség" },
            { "Offset", "Eltolás" },
            { "Simulate Data", "Adatok szimulálása" },
            { "Simulated", "Szimulált" },
            { "Decimal Precision", "Tizedesjegyek száma" },
            { "How often", "Milyen gyakran" },
            { "Frequency", "Gyakoriság" },
            { "System Data", "Rendszeradatok" },
            { "AOG Status:", "AgOpenGPS állapota:" },

            // ---- machine / steering ----
            { "AutoSteer", "Automata kormányzás" },
            { "Steer", "Kormány" },
            { "Steer:", "Kormány:" },
            { "Steer Module", "Kormánymodul" },
            { "Steer Button", "Kormánygomb" },
            { "Steer Switch", "Kormánykapcsoló" },
            { "Steer Switch:", "Kormánykapcsoló:" },
            { "Steer Ping:", "Korm. ping:" },
            { "Steer \r\nConfig", "Kormány \r\nbeállítás" },
            { "Steer\r\nSetting", "Kormány\r\nbeállítás" },
            { "From\r\nAutoSteer", "Kormány-\r\nzástól" },
            { "Set Angle", "Célszög" },
            { "Act Angle", "Aktuális szög" },
            { "Angle:", "Szög:" },
            { "Machine", "Munkagép" },
            { "Machine:", "Munkagép:" },
            { "Machine Module", "Munkagép modul" },
            { "Mach", "Gép" },
            { "Tool Ping:", "Gép ping:" },
            { "Work", "Munka" },
            { "Work Switch", "Munkakapcsoló" },
            { "Work:", "Munka:" },
            { "Switch:", "Kapcs.:" },
            { "Lift Enable", "Emelés eng." },
            { "Hyd Lift", "Hidr. emelő" },
            { "Raise Time", "Emelési idő" },
            { "LowerTime", "Leengedési idő" },
            { "Min Speed", "Min. sebesség" },
            { "Min PWM", "Min. PWM" },
            { "Hi PWM", "Magas PWM" },
            { "Lo PWM", "Alacsony PWM" },
            { "Pulse #", "Impulzus #" },
            { "PulseCounts", "Impulzusszám" },
            { "Counts:", "Impuls:" },
            { "Relay Act Hi", "Relé: magas" },
            { "Pressure", "Nyomás" },
            { "Ackerman", "Ackermann" },
            { "Uturn", "Fordulás" },
            { "Tram", "Techn." },
            { "Up", "Aktív" },
            { "Terms and Conditions", "Felhasználási feltételek" },
            { "YouTube Tutorials", "YouTube-oktatóvideók" },
            { "Discourse Forum", "Discourse fórum" },
            { "Disagree", "Nem fogadom el" },
            { "Agree", "Elfogadom" },
            { "Version:", "Verzió:" },
            { "Test Connection", "Kapcsolat tesztelése" },
            { "Enter Details Above and Tick Test Connection", "Add meg fent az adatokat, és jelöld be a kapcsolat tesztelését" },
            { "API Key:", "API-kulcs:" },
            { "Server:", "Szerver:" },
            { "Paste from Clipboard", "Beillesztés a vágólapról" },
            { "Register here to use AgShare", "Regisztrálj itt az AgShare használatához" },
            { "Activate", "Aktiválás" },
            { "Auto-Upload", "Automatikus feltöltés" },
            { "Local Only", "Csak helyben" },
            { "Title", "Cím" },
            { "✔ See you next time!", "✔ Viszlát legközelebb!" },
            { "Vehicle / Tool...", "Jármű / Munkagép..." },
            { "Simulator On", "Szimulátor bekapcsolva" },
            { "Enter Sim Coords", "Szimulátor koordinátái" },
            { "Kiosk Mode", "Kioszk mód" },
            { "Reset All", "Összes visszaállítása" },
            { "Reset", "Visszaállítás" },
            { "Wizards", "Varázslók" },
            { "Steer Wizard", "Kormányvarázsló" },
            { "Charts", "Diagramok" },
            { "Steer Chart", "Kormányzás diagram" },
            { "Heading Chart", "Irány diagram" },
            { "Roll Correction Chart", "Dőléskorrekció diagram" },
            { "Boundary Tool", "Határvonal-eszköz" },
            { "Smooth AB Curve", "AB-görbe simítása" },
            { "Hide Contour Paths", "Rétegvonalak elrejtése" },
            { "WebCam", "Webkamera" },
            { "Offset Fix", "Fix eltolása" },
            { "Fix       sec", "Fix       mp" },
            { "All", "Mind" },
            { "Configuration", "Beállítások" },
            { "Auto Steer", "Automata kormányzás" },
            { "View All Settings", "Összes beállítás megtekintése" },
            { "Directories", "Mappák" },
            { "GPS Data", "GPS-adatok" },
            { "Colors", "Színek" },
            { "Multi-Section Colors", "Többszakaszos színek" },
            { "HotKeys", "Gyorsbillentyűk" },
            { "Boundary", "Határvonal" },
            { "Headland", "Forgó" },
            { "Headland (Build)", "Forgó (készítés)" },
            { "TramLines", "Művelőutak" },
            { "Tramlines", "Művelőutak" },
            { "Delete Applied", "Megművelt terület törlése" },
            { "Flag By Lat Lon", "Jelölő koordináta alapján" },
            { "Recorded Path", "Rögzített útvonal" },
            { "Import Tracks", "Nyomvonalak importálása" },
            { "Current Field", "Jelenlegi tábla" },
            { "Ref", "Ref." },
            { "Simulator", "Szimulátor" },
            { "Auto Steer Toggle", "Auto kormány be/ki" },
            { "Touch Button to Reset\r\nThen Push New Key", "Koppints a gombra a törléshez,\r\nmajd nyomd meg az új billentyűt" },
            { "Cycle Lines", "Vonalváltás" },
            { "Close Field", "Tábla bezárása" },
            { "Updater", "Frissítő" },
            { "Pure Pursuit", "Tiszta követés" },
            { "Create Boundary From Mapping", "Határ létrehozása a térképből" },
            { "── Steer", "── Kormányzás" },
            { "Max Angular Vel.", "Max. szögsebesség" },
            { "Steer In Reverse", "Kormányzás tolatáskor" },
            { "Side Hill Comp", "Oldallejtő-kompenz." },
            { "── Arduino Steer", "── Arduino kormány" },
            { "Counts/Degree", "Impulzus/fok" },
            { "High Steer PWM", "Magas kormány PWM" },
            { "Low Steer PWM", "Alacsony kormány PWM" },
            { "Min Steer PWM", "Min. kormány PWM" },
            { "Kp (Proportional)", "Kp (arányos)" },
            { "Min Steer Speed", "Min. kormányzási seb." },
            { "Max Steer Speed", "Max. kormányzási seb." },
            { "Func Speed Limit", "Funkció sebességhatár" },
            { "── Geometry", "── Geometria" },
            { "Vehicle Type", "Jármű típusa" },
            { "Track Width", "Nyomtáv" },
            { "Antenna Pivot", "Antenna–forgáspont" },
            { "── GPS", "── GPS" },
            { "Heading Source", "Irányforrás" },
            { "Dual Hdg Offset", "Dual irány eltolás" },
            { "Dual Rev. Dist.", "Dual tolatási táv." },
            { "Min Step Limit", "Min. lépéshatár" },
            { "── IMU", "── IMU" },
            { "Fusion Weight", "Fúziós súly" },
            { "Dual As IMU", "Dual mint IMU" },
            { "── Brand", "── Márka" },
            { "Tractor Brand", "Traktor márka" },
            { "Harvester Brand", "Kombájn márka" },
            { "Articulated Brand", "Csuklós márka" },
            { "── Dimensions", "── Méretek" },
            { "Tool Width", "Munkaszélesség" },
            { "Tool Overlap", "Munkagép átfedés" },
            { "Tool Offset", "Munkagép eltolás" },
            { "Num Sections", "Szakaszok száma" },
            { "Trailing Hitch", "Vontatott kapcsolás" },
            { "Tank Trailing", "Tartálykocsi vontatás" },
            { "Trailing To Pivot", "Vontatás–forgáspont" },
            { "── Tool Type", "── Munkagép típusa" },
            { "Is Trailing", "Vontatott" },
            { "Is Rear Fixed", "Hátsó merev" },
            { "Is TBT", "TBT (traktor–gép–traktor)" },
            { "Is Front", "Elöl lévő" },
            { "── Sections", "── Szakaszok" },
            { "Sections Not Zones", "Szakaszok (nem zónák)" },
            { "Off When Out", "Kikapcs. kilépéskor" },
            { "Fast Section", "Gyors szakasz" },
            { "── Look Ahead / Timing", "── Előretekintés / időzítés" },
            { "Look Ahead On", "Előretekintés be" },
            { "Look Ahead Off", "Előretekintés ki" },
            { "Tool Off Delay", "Gép kikapcs. késleltetés" },
            { "Hydraulic Lift LA", "Hidr. emelés előretek." },
            { "Headland Sect. Ctrl", "Fordulósáv szakaszvez." },
            { "── Guidance (per Tool)", "── Vezetés (gépenként)" },
            { "Snap Distance", "Igazítási távolság" },
            { "Snap Distance Ref", "Igazítási táv. ref." },
            { "DeadZone Dist", "Holtzóna távolság" },
            { "DeadZone Hdg", "Holtzóna irány" },
            { "DeadZone Delay", "Holtzóna késleltetés" },
            { "Stanley Used", "Stanley használata" },
            { "Stanley Dist. Gain", "Stanley távolság-erős." },
            { "Stanley Hdg. Gain", "Stanley irány-erős." },
            { "Stanley Integral", "Stanley integrál" },
            { "PurePursuit Intgrl", "PurePursuit integrál" },
            { "Goal Pt Acq Factor", "Célpont felvétel tényező" },
            { "Goal Pt Hold", "Célpont tartás" },
            { "Goal Pt Mult", "Célpont szorzó" },
            { "Slow Speed Cutoff", "Lassú seb. határ" },
            { "── Work Switch", "── Munkakapcsoló" },
            { "Steer Work Switch", "Kormány munkakapcsoló" },
            { "Min Coverage", "Min. lefedettség" },
            { "── U-Turn (per Tool)", "── Visszafordulás (gépenként)" },
            { "Turn Radius", "Fordulási sugár" },
            { "Dist From Boundary", "Távolság a határtól" },
            { "Skip Width", "Kihagyás szélessége" },
            { "Extension Length", "Hosszabbítás" },
            { "U-Turn Style", "Visszafordulás stílusa" },
            { "U-Turn Smoothing", "Visszafordulás simítás" },
            { "U-Turn Compensation", "Visszafordulás kompenz." },
            { "── Display / Menu", "── Kijelző / menü" },
            { "Grid On", "Rács be" },
            { "Lightbar On", "Fénysáv be" },
            { "Side Guide Lines", "Oldalsó vezetővonalak" },
            { "Pure On", "Pure be" },
            { "Speedo On", "Sebességmérő be" },
            { "Lightbar Not SteerBar", "Fénysáv (nem kormánysáv)" },
            { "Day Mode", "Nappali mód" },
            { "Keyboard On", "Billentyűzet be" },
            { "Vehicle Image", "Jármű kép" },
            { "Texture On", "Textúra be" },
            { "Brightness On", "Fényerő be" },
            { "Log Elevation", "Magasság naplózása" },
            { "Shutdown Computer", "Számítógép leállítása" },
            { "Shutdown No Power", "Leállítás áramkimaradáskor" },
            { "Auto Start AgIO", "AgIO auto indítás" },
            { "Auto Off AgIO", "AgIO auto leállítás" },
            { "Lightbar cm/px", "Fénysáv cm/px" },
            { "Cam Zoom", "Kamera zoom" },
            { "Cam Pitch", "Kamera dőlés" },
            { "── AutoSteer", "── Automata kormány" },
            { "Guidance Lookahead", "Vezetés előretekintés" },
            { "AutoSteer Auto On", "Auto kormány auto be" },
            { "Constant Contour", "Állandó kontúr" },
            { "── Guide Lines", "── Vezetővonalak" },
            { "Num Guide Lines", "Vezetővonalak száma" },
            { "GPS Age Alarm", "GPS kor riasztás" },
            { "Is RTK", "RTK" },
            { "RTK Kill AutoSteer", "RTK vész: kormány le" },
            { "Jump Fix Alarm", "Ugrás riasztás" },
            { "Sim Latitude", "Szim. szélesség" },
            { "Sim Longitude", "Szim. hosszúság" },
            { "UDP Watch ms", "UDP figyelés ms" },
            { "── Sound", "── Hang" },
            { "U-Turn Sound", "Visszafordulás hang" },
            { "Hyd Lift Sound", "Hidr. emelés hang" },
            { "AutoSteer Sound", "Auto kormány hang" },
            { "Sections Sound", "Szakasz hang" },
            { "── Tram", "── Technológiai sáv" },
            { "Tram Passes", "Techn. sáv menetek" },
            { "Tram Alpha", "Techn. sáv átlátszóság" },
            { "── IMU / Global", "── IMU / általános" },
            { "Reverse On", "Tolatás be" },
            { "AutoSwitch Dual Fix", "Dual auto váltás: Fix" },
            { "AutoSwitch Speed", "Auto váltás sebesség" },
            { "Draw Pivot", "Forgáspont rajzolása" },
            { "Headland Dist On", "Fordulósáv táv. be" },
            { "Bnd Tool Spacing", "Határ gép térköz" },
            { "Bnd Tool Smooth", "Határ gép simítás" },
            { "Active Low", "Aktív alacsony" },
            { "Manual Sections", "Kézi szakaszok" },
            { "Remote Work Sys", "Távoli munkarendszer" },
            { "Steer WS Manual", "Kormány MK kézi" },
            { "Min Hdg Step Dist", "Min. irány lépéstáv." },
            { "── AgShare", "── AgShare" },
            { "AgShare Enabled", "AgShare engedélyezve" },
            { "Upload Active", "Feltöltés aktív" },
            { "Public Field", "Nyilvános tábla" },
            { "── Other", "── Egyéb" },
            { "Culture", "Nyelv" },
            { "── GPS / Fix", "── GPS / helymeghatározás" },
            { "Frame Time (ms)", "Képkocka idő (ms)" },
            { "Time Slice", "Időszelet" },
            { "GPS Hz", "GPS Hz" },
            { "Fix Quality", "Fix minőség" },
            { "Sats Tracked", "Követett műholdak" },
            { "HDOP", "HDOP" },
            { "Missed UDP Sentences", "Kimaradt UDP mondatok" },
            { "── Heading", "── Irány" },
            { "IMU Heading", "IMU irány" },
            { "Fix-to-Fix Heading", "Fix–fix irány" },
            { "Fused Heading (deg)", "Fúziós irány (fok)" },
            { "Angular Velocity", "Szögsebesség" },
            { "── Application", "── Alkalmazás" },
            { "Version", "Verzió" },
            { "Vehicle File", "Jármű fájl" },
            { "Tool File", "Munkagép fájl" },
            { "Setting", "Beállítás" },
            { "Value", "Érték" },
            { "All Settings", "Összes beállítás" },
            { "This will create and load the new profile.", "Ez létrehozza és betölti az új profilt." },
            { "Close Field first to run Updater", "A frissítő indítása előtt zárd be a táblát" },
            { "New Flag", "Új jelölő" },
            { "Manual Section", "Kézi szakasz" },
            { "Auto Section", "Auto szakasz" },
            { "Snap to Pivot", "Vonalhoz igazítás" },
            { "Move Line Left", "Vonal balra" },
            { "Move Line Right", "Vonal jobbra" },
            { "Vehicle Settings", "Jármű beállítás" },
            { "   Reset \r\nto Default", "   Alap-\r\nértékek" },
            { "Section or Zone", "Szakasz vagy zóna" },
            { "New Key", "Új billentyű" },
            { "F11 -  Full Screen\r\n\r\nNumPad1 - Manual Section\r\nNumPad0 - Auto Section", "F11 - Teljes képernyő\r\n\r\nNumPad1 - Kézi szakasz\r\nNumPad0 - Auto szakasz" },
            { "Prompt", "Kérdés" },
            { "Easting", "Keleti koordináta" },
            { "Northing", "Északi koordináta" },
            { "msec", "ms" },
            { "Raw Hz", "Nyers Hz" },
            { "Frame", "Képk." },
            { "Missed", "Kimar." },
            { "Fix2Fix", "Fix–fix" },
            { "North (+)", "Észak (+)" },
            { "West (-)", "Nyugat (-)" },
            { "East (+)", "Kelet (+)" },
            { "South (-)", "Dél (-)" },
            { "Keep offset even after field is closed?", "Megmaradjon az eltolás a tábla bezárása után is?" },
            { "Vehicle", "Jármű" },
            { "Tool", "Munkagép" },
            { "Vehicle Name", "Jármű neve" },
            { "Type:", "Típus:" },
            { "Tractor", "Traktor" },
            { "Wheelbase:", "Tengelytáv:" },
            { "Ant.Pivot:", "Antenna–forgáspont:" },
            { "Ant.Offset:", "Antenna eltolás:" },
            { "Hitch:", "Függesztés:" },
            { "Tool Name", "Munkagép neve" },
            { "Width:", "Szélesség:" },
            { "Sections:", "Szakaszok:" },
            { "Offset:", "Eltolás:" },
            { "Overlap:", "Átfedés:" },
            { "Lookahead:", "Előretekintés:" },
            { "1 sec", "1 mp" },
            { "Line Shift:", "Vonaleltolás:" },
            { "Tram Width:", "Művelőút szélessége:" },
            { "Units:", "Mértékegység:" },
            { "Metric", "Metrikus" },
            { "Choose Vehicle Type", "Válaszd ki a jármű típusát" },
            { "Opacity", "Átlátszóság" },
            { "No Image", "Nincs kép" },
            { "For Now", "Egyelőre" },
            { "To File", "Fájlba" },
            { "Clip Line", "Vonal vágása" },
            { "Zoom In", "Nagyítás" },
            { "Build", "Létrehozás" },
            { "Alpha", "Átlátszóság" },
            { "Filename", "Fájlnév" },
            { "Enter Record Name", "Add meg a felvétel nevét" },
            { "> OFF <", "> KI <" },
            { "Status: ", "Állapot: " },
            { "Add Name", "Név hozzáadása" },
            { "Curve", "Görbe" },
            { "Edit Name", "Név szerkesztése" },
            { "Pivot", "Forgáspont" },
            { "AB + Curve", "AB + görbe" },
            { "AB Line", "AB-vonal" },
            { "Lat/Lon +", "Szél./Hossz. +" },
            { "Lat/Lon", "Szél./Hossz." },
            { "Longitude ( +- 180)", "Hosszúság (±180)" },
            { "Lattitude (+- 90)", "Szélesség (±90)" },
            { "Spray", "Permetezés" },
            { "Passes", "Sávok" },
            { "Track", "Nyomvonal" },
            { "Seed", "Vetés" },
            { "Mode", "Mód" },
            { "Zoom", "Nagyítás" },
            { "Select Buttons", "Gombok kiválasztása" },
            { "AOG Menu", "AOG menü" },
            { "Preview", "Előnézet" },
            { "Multi Color", "Több szín" },
            { "Section Color", "Szakasz színe" },
            { "Edit Color", "Szín szerkesztése" },
            { "Type", "Típus" },
            { "Pivot Distance", "Forgáspont távolsága" },
            { "Antenna Height", "Antennamagasság" },
            { "Center", "Közép" },
            { "Antenna Offset", "Antenna-eltolás" },
            { "Left", "Bal" },
            { "Right", "Jobb" },
            { "** Dual Position Antenna on Right", "** Kettős antenna: a pozícióantenna jobb oldalon van" },
            { "Hitch Length", "Függesztés hossza" },
            { "Wheelbase", "Tengelytáv" },
            { "Attachment Style", "Csatlakozás típusa" },
            { "Overlap / Gap", "Átfedés / hézag" },
            { "Overlap", "Átfedés" },
            { "Gap", "Hézag" },
            { "Tool Offset Direction", "Munkagép-eltolás iránya" },
            { "Tool Right", "Munkagép jobbra" },
            { "Tool Left", "Munkagép balra" },
            { "Zone 8", "8. zóna" },
            { "Zone 7", "7. zóna" },
            { "Zone 1", "1. zóna" },
            { "Zone 2", "2. zóna" },
            { "Zone 6", "6. zóna" },
            { "Zone 3", "3. zóna" },
            { "Zone 5", "5. zóna" },
            { "Zone 4", "4. zóna" },
            { "Km/H", "km/h" },
            { "Inch", "Hüvelyk" },
            { "Section Width", "Szakaszszélesség" },
            { "% Coverage", "% lefedettség" },
            { "Zones", "Zónák" },
            { "Sections", "Szakaszok" },
            { "        Auto Sections", "        Automata szakaszok" },
            { "        Manual Sections", "        Kézi szakaszok" },
            { "Look Ahead Time Settings", "Előretekintési idő beállításai" },
            { "On (secs)", "Be (mp)" },
            { "Off (secs)", "Ki (mp)" },
            { "Turn Off Delay (secs)", "Kikapcsolási késleltetés (mp)" },
            { "Alarm Stops Autosteer", "A riasztás leállítja az automata kormányzást" },
            { "0 to Disable", "0 = kikapcsolva" },
            { "Fix Jump Distance (cm) ", "Fix-ugrás távolsága (cm) " },
            { "Dual Antenna Settings", "Kettős antenna beállításai" },
            { "Switch speed", "Váltási sebesség" },
            { "Auto Dual <-> Fix", "Automata kettős ↔ fix" },
            { "Reverse Distance (m)", "Tolatási távolság (m)" },
            { "Heading Offset (Degree)", "Irányeltolás (fok)" },
            { "Single Antenna Settings", "Egy antenna beállításai" },
            { "Default: 70%", "Alapérték: 70%" },
            { "Fusion", "Fúzió" },
            { "Reverse Detection", "Tolatás felismerése" },
            { "Fix To Fix Distance", "Fix–fix távolság" },
            { "Minimum GPS Step", "Minimális GPS-lépés" },
            { "Antenna Type", "Antenna típusa" },
            { "Invert Roll", "Dőlés megfordítása" },
            { "Zero Roll", "Dőlés nullázása" },
            { "Remove Offset", "Eltolás törlése" },
            { "Less", "Kevesebb" },
            { "More", "Több" },
            { "Roll Filter", "Dőlésszűrő" },
            { "Set Smoothing to 3 or 4x Radius", "A simítást állítsd a sugár 3–4-szeresére" },
            { "Set extension length to 2 or 3x Radius\r\n", "A hosszabbítás hossza legyen a sugár 2–3-szorosa\r\n" },
            { "Hydraulic Lift Config", "Hidraulikus emelő beállításai" },
            { "Plant Pop", "Tőszám" },
            { "Hydraulic Lift Look Ahead (secs)", "Hidraulikus emelő előretekintése (mp)" },
            { "Raise Time (secs)", "Emelési idő (mp)" },
            { "Enable", "Engedélyezés" },
            { "Lower Time (secs)", "Leengedési idő (mp)" },
            { "Invert Relays", "Relék megfordítása" },
            { "Send + Save", "Küldés és mentés" },
            { "Display?", "Megjelenítés?" },
            { "Overide", "Felülbírálás" },
            { "Tram Width", "Művelőút szélessége" },
            { "Hardware Messages", "Hardverüzenetek" },
            { "Power Loss Shutdown", "Leállítás áramkimaradáskor" },
            { "Auto Off AgIO?", "AgIO automatikus kikapcsolása?" },
            { "Bottom Menu", "Alsó menü" },
            { "Nudge Controls", "Eltolás gombok" },
            { "Right Menu", "Jobb oldali menü" },
            { "Auto Start AgIO?", "AgIO automatikus indítása?" },
            { "You Turn", "Fordulás" },
            { "Sound", "Hang" },
            { "U-Turn", "Automata forduló" },
            { "Lateral", "Oldalirányú" },
            { "Hide Contour", "Rétegvonal elrejtése" },
            { "AB Smooth", "AB simítás" },
            { "Rec Path", "Rögzített útvonal" },
            { "Tram Lines", "Művelőutak" },
            { "Screen Buttons", "Képernyőgombok" },
            { "Tools Menu", "Eszközmenü" },
            { "Field Menu", "Tábla menü" },
            { "OFF", "KI" },
            { "ON", "BE" },
            { "Debug Values", "Hibakeresési értékek" },
            { "Headland Dist.", "Forgó távolsága" },
            { "Line Smooth", "Vonalsimítás" },
            { "Section Lines", "Szakaszvonalak" },
            { "Direction Markers", "Irányjelzők" },
            { "Elevation", "Magasság" },
            { "Units", "Mértékegység" },
            { "Svenn Arrow", "Svenn-nyíl" },
            { "Brightness", "Fényerő" },
            { "Field Texture", "Tábla mintázata" },
            { "Polygons", "Sokszögek" },
            { "Start Fullscreen", "Indítás teljes képernyőn" },
            { "Speedo", "Sebességmérő" },
            { "Guidelines", "Segédvonalak" },
            { "Grid", "Rács" },
            { "Current Vehicle", "Jelenlegi jármű" },
            { "Current Tool", "Jelenlegi munkagép" },
            { "Inches", "Hüvelyk" },
            { "Tool Width:", "Munkaszélesség:" },
            { "Feet", "Láb" },
            { "Act:", "Mért:" },
            { "Set:", "Cél:" },
            { "Err:", "Hiba:" },
            { "Or +5", "Vagy +5" },
            { "Steer Angle:", "Kormányszög:" },
            { "Diameter:", "Átmérő:" },
            { "Acquire:", "Rátalálás:" },
            { "It will slowly increase steer angle to reduce cross track error", "Lassan növeli a kormányszöget az oldalirányú eltérés csökkentésére" },
            { "Slow", "Lassú" },
            { "Fast", "Gyors" },
            { "Steer Response", "Kormányzás reakciója" },
            { "Integral", "Integráló tag" },
            { "Look Ahead Min", "Min. előretekintés" },
            { "Look Ahead", "Előretekintés" },
            { "Stanley Gains", "Stanley-erősítések" },
            { "Max Steer Angle", "Max. kormányszög" },
            { "AV Act:", "AV aktuális:" },
            { "AV Set:", "AV cél:" },
            { "Counts per Degree", "Impulzus fokonként" },
            { "WAS Zero", "WAS nullázás" },
            { "Acquire Factor", "Rátalálási tényező" },
            { "Distance:", "Távolság:" },
            { "Speed Factor", "Sebességtényező" },
            { " ------ Dead Zone -----", " ------ Holtsáv -----" },
            { "Hold:", "Tartás:" },
            { "Acquire = Factor * Hold", "Rátalálás = tényező × tartás" },
            { "On Delay (sec)", "Bekapcsolási késleltetés (mp)" },
            { "Heading (Degree)", "Irány (fok)" },
            { "Gain", "Kormányérzékenység" },
            { "Minimum to Move", "Minimum a mozgáshoz" },
            { "Maximum Limit", "Felső határ" },
            { "Proportional Gain", "Kormányérzékenység (P)" },
            { "Sidehill Deg Turn per Deg of Roll", "Oldalirányú fordulat fokonként a dőlés 1 fokára" },
            { "Button - Push On. Push Off\r\nSwitch - Pushed On, Release Off", "Gomb – nyomásra be, újabb nyomásra ki\r\nKapcsoló – benyomva be, elengedve ki" },
            { "Pressure Sensor", "Nyomásérzékelő" },
            { "Current Sensor", "Áramérzékelő" },
            { "Count Sensor", "Impulzusérzékelő" },
            { "Invert Motor Dir", "Motoriránya megfordítása" },
            { "Steer Enable", "Kormányzás engedélyezése" },
            { "Motor Driver", "Motorvezérlő" },
            { "A2D Convertor", "A/D konverter" },
            { "Turn Sensor", "Kormányszög-érzékelő" },
            { "Invert WAS", "WAS megfordítása" },
            { "Reset All To Defaults", "Minden alapértékre állítása" },
            { "IMU X or Y Axis", "IMU X vagy Y tengely" },
            { "Stanley/ Pure", "Stanley / Pure" },
            { "Smart WAS", "Okos WAS" },
            { "Ready", "Kész" },
            { "Confidence: 0%", "Megbízhatóság: 0%" },
            { "Samples: 0", "Minták: 0" },
            { "Steer In Reverse?", "Kormányzás tolatáskor?" },
            { "In", "Be" },
            { "Out", "Ki" },
            { "UTurn Compensation", "Forduló kompenzáció" },
            { "kmh", "km/h" },
            { "Max Speed", "Max. sebesség" },
            { "Kmh", "km/h" },
            { "Manual Turns", "Kézi fordulók" },
            { "Line Width", "Vonalvastagság" },
            { "pixels", "képpont" },
            { "Nudge Distance", "Eltolás távolsága" },
            { "Guidance Bar", "Vezetősáv" },
            { "Steer Bar", "Kormányzássáv" },
            { "On/Off", "Be/Ki" },
            { "Lightbar", "Fénysáv" },
            { "cm Per Pixel", "cm / képpont" },
            { "Seconds", "Másodperc" },
            { "Next Guidance Line Search Time", "Következő vezetővonal keresési ideje" },
            { "Wizard", "Varázsló" },
            { "Error:", "Hiba:" },
            { "Low", "Alacsony" },
            { "Sidehill Deg per Deg of Roll", "Oldalirányú fok a dőlés fokonként" },
            { "Look Ahead (m)", "Előretekintés (m)" },
            { "Look Ahead Speed Gain", "Előretekintés sebességfüggése" },
            { "PWM Minimum Movement", "PWM minimum a mozgáshoz" },
            { "PWM Maximum", "Maximális kormányerő (PWM)" },
            { "Drive straight forward and Touch Zero", "Haladj egyenesen előre, és koppints a Nulla gombra" },
            { "Pressure Turn Sensor", "Kormány-nyomásérzékelő" },
            { "Current Turn Sensor", "Kormány-áramérzékelő" },
            { "Invert Hydraulic Enable Relay?", "Megfordítod a hidraulika-engedélyező relét?" },
            { "Steer Enable Mode", "Kormányzás-engedélyezés módja" },
            { "What Style of Motor Driver?", "Milyen típusú a motorvezérlő?" },
            { "A/D Convertor Connection?", "Hogyan csatlakozik az A/D konverter?" },
            { "Turn Counts", "Fordulatszám (impulzus)" },
            { "Do you have use a Danfoss Valve?", "Danfoss szelepet használsz?" },
            { "Free Drive", "Szabad vezetés" },
            { "  Start Wizard", "  Varázsló indítása" },
            { "Highly Recommended", "Erősen ajánlott" },
            { "Load Defaults", "Alapértékek betöltése" },
            { "Set Wheelbase", "Tengelytáv beállítása" },
            { "Set Track Width", "Nyomtáv beállítása" },
            { "Wheel Track", "Nyomtáv" },
            { "Antenna\r\nPosition", "Antenna\r\nhelyzete" },
            { "Set Distance Antenna Is From Pivot Axle", "Add meg az antenna távolságát a forgáspont tengelyétől" },
            { "Antenna\r\nHeight", "Antenna\r\nmagassága" },
            { "Set Antenna Height", "Antennamagasság beállítása" },
            { "0 = Off", "0 = ki" },
            { "Set Antenna Offset", "Antenna-eltolás beállítása" },
            { "A/D Convertor", "A/D konverter" },
            { "Motor Driver Board", "Motorvezérlő panel" },
            { "Driver Control Relay", "Vezérlőrelé" },
            { "Danfoss Valve", "Danfoss szelep" },
            { "If not Select Invert Roll", "Ha nem így van, válaszd a dőlés megfordítását" },
            { "Put vehicle leaning strongly to the right. Roll to the right must be POSITIVE.", "Döntsd erősen jobbra a járművet. A jobbra dőlésnek POZITÍVNAK kell lennie." },
            { "Roll Function", "Dőlés funkció" },
            { "Park on level surface and touch \"Zero Roll\"", "Állj sík felületre, és koppints a „Dőlés nullázása” gombra" },
            { "Roll Zero", "Dőlés nullázása" },
            { "Invert WAS Direction", "WAS irányának megfordítása" },
            { "Current Heading:", "Jelenlegi irány:" },
            { "WAS Zeroing", "WAS nullázása" },
            { "Invert Direction", "Irány megfordítása" },
            { "Steer Motor Direction", "Kormánymotor iránya" },
            { "* Begin * Calibration", "* Kalibrálás indítása *" },
            { "WAS Calibration", "WAS kalibrálása" },
            { "* SKIP *  Counts Per Degree and Ackermann Setting?", "* KIHAGYÁS * Fokonkénti impulzus és Ackermann-beállítás?" },
            { "Start Angle:", "Kezdőszög:" },
            { "Reset To 100%", "Vissza-\r\nállítás\r\n100%-ra" },
            { "Ackermann Value:", "Ackermann-érték:" },
            { "Set", "Beállít" },
            { "Turn steering to your desired limit for guidance and touch OK ->", "Fordítsd a kormányt a kívánt vezetési határig, majd koppints az OK-ra ->" },
            { "Guidance Steer Angle Limit", "Kormányszög-határ vezetéshez" },
            { "Or Manually Set Guidance Steer Angle Limit", "Vagy add meg kézzel a kormányszög-határt" },
            { "Automatic Canceling Feature", "Automatikus kikapcsolási funkció" },
            { "Panic Stop Speed", "Vészleállítási sebesség" },
            { "Zero", "Nulla" },
            { "Auto-Upload: upload field changes on close\r\nCloud Load: always download latest from AgShare on open", "Automatikus feltöltés: a tábla módosításainak feltöltése bezáráskor\r\nFelhőből betöltés: megnyitáskor mindig a legfrissebb letöltése az AgShare-ről" },
            { "R - Reset sim\r\nU - U-Turn (180 degrees)\r\n\r\nUp Arrow - Faster\r\nDn Arrow - Slower\r\nLeft Arrow - Left\r\nRight Arrow - Right\r\n\r\n( . ) Period - Stop\r\n( / ) Slash - Go Straight", "R – Szimulátor visszaállítás\r\nU – Megfordulás (180°)\r\n\r\nFel nyíl – Gyorsabb\r\nLe nyíl – Lassabb\r\nBal nyíl – Balra\r\nJobb nyíl – Jobbra\r\n\r\n( . ) Pont – Megállás\r\n( / ) Perjel – Egyenes" },
            { "This offset distance is added to Lat and Lon and saved in FIELD.KML file when closing the field.", "Ez az eltolás hozzáadódik a szélességhez és a hosszúsághoz, és a tábla bezárásakor a FIELD.KML fájlba mentődik." },
            { "The vehicle settings are now called \"profiles\" and\r\ncan be created/loaded in the main menu (top left).", "A járműbeállítások mostantól „profilok”, és a\r\nfőmenüben (bal felül) hozhatók létre és tölthetők be." },
            { "Touch the buttons on the left side you want to appear in the Menu (The right side). \r\n\r\nThe order you push them will be the order they appear on the menu from bottom up.", "Koppints a bal oldali gombokra, amelyeket meg szeretnél jeleníteni a menüben (jobb oldal). \r\n\r\nA megnyomásuk sorrendje lesz a menüben a megjelenésük sorrendje alulról felfelé." },
            { "Turn steering wheel to RIGHT about 20 degrees.\r\nWhile driving in a steady circle, Press Rec and wait.", "Fordítsd a kormányt JOBBRA kb. 20 fokkal.\r\nÁllandó sugarú körben haladva nyomd meg a REC gombot, és várj." },
            { "Button - Push Release, On.   \r\n               Push Release, Off\r\n\r\nSwitch - While Pushed is On. \r\n               Released is Off.", "Gomb – megnyomás és elengedés: be.   \r\n               megnyomás és elengedés: ki\r\n\r\nKapcsoló – nyomva tartva: be. \r\n               elengedve: ki." },
            { "Drive slowly and enable Auto Steer. Invert Direction if steer motor turns the wrong way.\r\nTest with Arrows.", "Haladj lassan, és kapcsold be az automata kormányzást. Ha a kormánymotor rossz irányba forog,\r\nhasználd az Irány megfordítása gombot. Próbáld ki a nyilakkal." },
            { "Steer Angle must be POSITIVE when turning to the RIGHT.\r\nIf not, select the Invert WAS.", "A kormányszögnek POZITÍVNAK kell lennie, ha jobbra fordulsz.\r\nHa nem így van, válaszd a WAS megfordítása gombot." },
            { "Turn steering wheel to LEFT roughly the same degrees as right.\r\nWhile driving in a steady circle, Press Rec and wait.", "Fordítsd a kormányt BALRA nagyjából ugyanannyi fokkal, mint jobbra.\r\nÁllandó sugarú körben haladva nyomd meg a REC gombot, és várj." },
            { "Change Setpoint with steer buttons. \r\n\r\nAdjust Minumum Movement as high as possible without steer motor or hydraulics moving steering", "Állítsd a célértéket a kormánygombokkal. \r\n\r\nA minimális mozgást állítsd a lehető legmagasabbra úgy, hogy a kormánymotor vagy a hidraulika még ne mozgassa a kormányt" },
            { "Now go drive an AB Line and fine tune settings based on your speed and soil conditions.\r\n\r\nRemember: CPD, Min PWM, and Look Ahead are critical settings. ", "Most készíts egy AB-vonalat, és finomhangold a beállításokat a sebességed és a talajviszonyok alapján.\r\n\r\nFigyelj: a CPD, a minimális PWM és az előretekintés kritikus beállítások. " },
            { "Old profiles contained all settings in one file. New format separates Vehicle, Tool, and Environment. Select what you want to extract.", "A régi profilok minden beállítást egy fájlban tartalmaztak. Az új formátum külön kezeli a járművet, az eszközt és a környezetet. Válaszd ki, mit szeretnél kinyerni." },
            { "Firmware Update Feature\r\n\r\nThis feature is under development.\r\n\r\nFuture capabilities will include:\r\n\r\n• Select firmware .hex file\r\n• Select communication port (from AgIO settings)\r\n• Start TeensyLoaderCLI with parameters\r\n• Upload firmware to Teensy device\r\n• Progress indicator\r\n\r\nTeensyLoader integration:\r\nteensy_loader_cli --mcu=TEENSY40 --port=COM3 firmware.hex", "Firmware-frissítés\r\n\r\nEz a funkció fejlesztés alatt áll.\r\n\r\nA tervezett képességek:\r\n\r\n• Firmware .hex fájl kiválasztása\r\n• Kommunikációs port kiválasztása (az AgIO beállításaiból)\r\n• A TeensyLoaderCLI indítása paraméterekkel\r\n• Firmware feltöltése a Teensy eszközre\r\n• Folyamatjelző\r\n\r\nTeensyLoader-integráció:\r\nteensy_loader_cli --mcu=TEENSY40 --port=COM3 firmware.hex" },
            { "None", "Nincs" },
            { "Switch", "Kapcsoló" },
            { "Button", "Gomb" },
            { "Select an old profile file", "Válassz régi profilt" },
            { "Select to convert (Vehicle and/or Tool)", "Mit alakítsunk át? (jármű / eszköz)" },
            { "App:", "Kezelt:" },
            { "Actual:", "Tényleges:" },
            { "ha/hr", "ha/ó" },
            { "\u221E Hrs", "\u221E ó" },
            { "ac/hr", "ac/ó" },
            { "Drive slowly straight forward, Allow system to steer", "Haladj lassan egyenesen előre, és hagyd, hogy a rendszer kormányozzon" },
            { "Minimum Movement Setting", "Minimális mozgás beállítása" },
            { "Zero or +5", "Nulla vagy +5" },
            { "Set Proportional for good response. \r\nSet PWM Maximum to limit turning speed.", "Állítsd be az érzékenységet úgy, hogy a kormány jól reagáljon.\r\nA PWM-maximummal korlátozhatod, milyen gyorsan mozoghat a kormány." },
            { "Drive forward and enable Auto Steer", "Indulj el előre, és kapcsold be az automata kormányzást" },
            { "Gain Setting", "Kormányérzékenység beállítása" },
            { "End", "Vége" },
            { "Restart Wizard", "Varázsló újraindítása" },
            { "Finished !", "Kész!" },
            { "Progress:", "Haladás:" },
            { "Counts P Deg", "Impulzus / fok" },
            { "WAS Offset", "WAS eltolás" },
            { "Heading:", "Irány:" },
            { "Auto", "Automata" },
            { "Corrected Easting", "Korrigált keleti koordináta" },
            { "Correction Distance", "Korrekció távolsága" },
            { "Roll Degrees", "Dőlés (fok)" },
            { "Scroll On Off", "Görgetés be/ki" },
            { "Pole", "Rúd" },
            { "Smooth", "Simított" },
            { "Direct", "Közvetlen" },
            { "Camera Behaviour", "Kamera viselkedése" },
            { "Reset to Default", "Alapértékre állítás" },
            { "( +90 to -90)", "(+90 – −90)" },
            { "( +180 to -180 )", "(+180 – −180)" },
            { "Vehicle: -", "Jármű: -" },
            { "Tool: -", "Munkagép: -" },
            { "Environment: -", "Környezet: -" },
            { "Environment", "Környezet" },
            { "System / GPS", "Rendszer / GPS" },
            { "SetPoint", "Célérték" },
            { "Actual", "Tényleges" },
            { "Enter Field Name", "Add meg a tábla nevét" },
            { "Area:", "Terület:" },
            { "Points:", "Pontok:" },
            { "Available Tracks", "Elérhető nyomvonalak" },
            { "Available Fields", "Elérhető táblák" },
            { "Import", "Importálás" },
            { "Deselect All", "Kijelölés megszüntetése" },
            { "Select All", "Mindet kijelöl" },
            { "Select a field from the left, then select tracks to copy", "Válassz egy táblát balra, majd jelöld ki a másolandó nyomvonalakat" },
            { "Field Name", "Tábla neve" },
            { "Exit To Windows In:", "Kilépés:" },
            { "Shutdown In:", "Leállítás ennyi idő múlva:" },
            { "Shutdown", "Leállítás" },
            { "(De)Select Tracks to use", "Jelöld ki a használni kívánt nyomvonalakat" },
            { "Shrink B", "B pont rövidítése" },
            { "Shrink A", "A pont rövidítése" },
            { "Extend B", "B pont hosszabbítása" },
            { "Extend A", "A pont hosszabbítása" },
            { "Next", "Következő" },
            { "Previous", "Előző" },
            { "Auto Find", "Automatikus keresés" },
            { "Field", "Tábla" },
            { "Overwrite", "Felülírás" },
            { "Get Selected", "Kijelöltek letöltése" },
            { "Get All", "Összes letöltése" },
            { "Downloading...Please Wait", "Letöltés... Kérlek, várj" },
            { "Lon:", "Hossz.:" },
            { "Lat:", "Szél.:" },
            { "Distance To Flag:", "Távolság a jelölőig:" },
            { "Point A", "A pont" },
            { "Import Flags", "Jelölők importálása" },
            { "Export Flags", "Jelölők exportálása" },
            { "Bounds", "Határok" },
            { "Thru?", "Átmenő?" },
            { "Area", "Terület" },
            { "Spacing (m)", "Távolság (m)" },
            { "Add Points", "Pontok hozzáadása" },
            { "Points to Process: ", "Feldolgozandó pontok: " },
            { "Points", "Pontok" },
            { "AgShare Upload", "AgShare-feltöltés" },
            { "AgShare Download", "AgShare-letöltés" },
            { "From KML", "KML-fájlból" },
            { "Open", "Megnyitás" },
            { "Drive In", "Behajtás a táblába" },
            { "Resume", "Folytatás" },
            { "Existing", "Meglévő" },
            { "New Field", "Új tábla" },
            { "Previous Field", "Előző tábla" },
            { "Working Width", "Munkaszélesség" },
            { "New Bnd", "Új határvonal" },
            { "Background ", "Háttér " },
            { "Fieldname", "Tábla neve" },
            { "Upload Selected", "Kijelöltek feltöltése" },
            { "Edit Field Name", "Tábla nevének szerkesztése" },
            { "Lines", "Vonalak" },
            { "Mapping", "Térképezés" },
            { "Flags", "Jelölők" },
            { "Sort", "Rendezés" },
            { "Remain:", "Hátralévő:" },
            { "Rate:", "Mennyiség:" },
            { "Total:", "Összesen:" },
            { "Applied:", "Megművelt:" },
            { "Worked", "Megművelt" },
            { "Dist:", "Táv.:" },
            { "Trip", "Út" },
            { "Distance (km)", "Távolság (km)" },
            { " Use Selected", " Kijelölt használata" },
            { "Use Selected", "Kijelölt használata" },
            { "Delete Field", "Tábla törlése" },
            { "Record Name", "Felvétel neve" },
            { "Delete Record", "Felvétel törlése" },
            { "Path Off", "Útvonal ki" },
            { "Day", "Nappal" },
            { "Night", "Éjszaka" },
            { "Current: -", "Jelenlegi: -" },
            { "Selected: -", "Kijelölt: -" },
            { "Ant. Pivot:", "Antenna–forgáspont:" },
            { "Ant. Offset:", "Antenna eltolás:" },
            { "Track Width:", "Nyomtáv:" },
            { "Attach:", "Csatlakozás:" },
            { "Trail Hitch:", "Vontatott csatlakozás:" },
            { "Load", "Betöltés" },
            { "Convert Old profiles", "Régi profilok átalakítása" },
            { "Environments", "Környezetek" },
            { "File Name", "Fájlnév" },
            { "Convert Old Profile Files to New Format", "Régi profilfájlok átalakítása az új formátumra" },
            { "Convert", "Átalakítás" },
            { "Step 2: Select to convert (Vehicle and/or Tool):", "2. lépés: válaszd ki, mit alakítsunk át (jármű és/vagy munkagép):" },
            { "Convert Tool", "Munkagép átalakítása" },
            { "Tool profile name:", "Munkagép-profil neve:" },
            { "Vehicle profile name:", "Jármű-profil neve:" },
            { "Convert Vehicle", "Jármű átalakítása" },
            { "Step 1: Select an old profile file:", "1. lépés: válassz egy régi profilfájlt:" },
            { "Name:", "Név:" },
            { "Create", "Létrehozás" },
            { "Copy from:", "Másolás innen:" },
            { "Invalid: ", "Érvénytelen: " },
            { "GPS single: ", "GPS (autonóm): " },
            { "RTK Float: ", "RTK lebegés: " },
            { "Estimate: ", "Becsült: " },
            { "Man IP: ", "Kézi: " },
            { "Sim: ", "Szim.: " },
            { "Unknown: ", "Ismeretlen: " },
            { "Age: ", "Kor: " },
            { "No network connection.", "Nincs hálózati kapcsolat." },
            { "Connected. Waiting for corrections.", "Csatlakozva. Várakozás a korrekciós jelre." },
            { "Corrections stopped. Reconnecting.", "A korrekciós jel megszűnt. Újracsatlakozás..." },
            { "Connected, but nothing is coming in. Some services need your position: check that your GPS position is switched on.", "Csatlakozva, de nem érkezik adat. Egyes szolgáltatók ismerni akarják a pozíciódat: ellenőrizd, hogy a GPS-pozíció küldése be van-e kapcsolva." },
            { "The service closed the connection. Reconnecting.", "A szolgáltató megszakította a kapcsolatot. Újracsatlakozás..." },
            { "The service closed the connection without sending corrections.", "A szolgáltató korrekciós jel küldése nélkül megszakította a kapcsolatot." },
            { "Receiving corrections", "Korrekciós jel érkezik" },
            { "The service sent an answer AgIO cannot read.", "A szolgáltató olyan választ küldött, amit az AgIO nem tud értelmezni." },
            { "The service did not send a list of mountpoints.", "A szolgáltató nem küldött mountpoint-listát." },
            { "The service sent an empty answer.", "A szolgáltató üres választ küldött." },
            { "This mountpoint does not exist on the service. Pick one from the list.", "Ez a mountpoint nem létezik a szolgáltatónál. Válassz egyet a listából." },
            { "This address answered with a web page, not an NTRIP service. Check the address and port.", "Ez a cím weboldallal válaszolt, nem NTRIP szolgáltatóval. Ellenőrizd a címet és a portot." },
            { "User name, password or mountpoint is not correct.", "A felhasználónév, a jelszó vagy a mountpoint hibás." },
            { "This mountpoint does not exist on the service. Check the spelling, capitals matter.", "Ez a mountpoint nem létezik a szolgáltatónál. Ellenőrizd a helyesírást, a kis- és nagybetű számít." },
            { "The service cannot take another connection right now. It is busy, or this login is already in use.", "A szolgáltató most nem fogad újabb kapcsolatot. Túlterhelt, vagy ez a bejelentkezés már használatban van." },
            { "The service is starting up. AgIO tries again in about a minute.", "A szolgáltató éppen indul. Az AgIO körülbelül egy perc múlva újra próbálkozik." },
            { "This service needs your e-mail address as user name.", "Ez a szolgáltató az e-mail címedet kéri felhasználónévként." },
            { "User name or password is not correct.", "A felhasználónév vagy a jelszó hibás." },
            { "The service denied access.", "A szolgáltató megtagadta a hozzáférést." },
            { "The service answered something AgIO does not understand.", "A szolgáltató olyan választ adott, amit az AgIO nem ért." },
            { "The radio is open. Waiting for corrections.", "A rádió megnyílt. Várakozás a korrekciós jelre." },
            { "The radio is open, but nothing arrives.", "A rádió megnyílt, de nem érkezik adat." },
            { "Receiving corrections from the radio.", "Korrekciós jel érkezik a rádióból." },
            { "Back on the main service", "Újra a fő szolgáltató van használatban" },
            { "Corrections now come from the main service again.", "A korrekciós jel újra a fő szolgáltatótól érkezik." },
            { "The main service is back", "A fő szolgáltató újra működik" },
            { "Switch back to it? Your GPS may lose RTK for a moment. AgIO never switches back by itself.", "Visszaváltasz rá? A GPS egy pillanatra elveszítheti az RTK-t. Az AgIO soha nem vált vissza magától." },
            { "The main service has a problem", "A fő szolgáltatóval gond van" },
            { "Switched to the fallback", "Átváltva a tartalék szolgáltatóra" },
            { "You switched to the fallback.", "Te váltottál a tartalék szolgáltatóra." },
            { "no corrections", "nincs korrekciós jel" },
            { "All good: corrections are coming in.", "Minden rendben: a korrekciós jel megérkezik." },
            { "The profile has no name.", "A profilnak nincs neve." },
            { "GPS observations (legacy)", "GPS-mérések (régi formátum)" },
            { "GLONASS observations (legacy)", "GLONASS-mérések (régi formátum)" },
            { "proprietary", "gyártóspecifikus" },
            { "base position", "bázispozíció" },
            { "base position + antenna height", "bázispozíció + antennamagasság" },
            { "antenna descriptor", "antennaleírás" },
            { "system parameters", "rendszerparaméterek" },
            { "GPS ephemeris", "GPS-pályaadatok" },
            { "GLONASS ephemeris", "GLONASS-pályaadatok" },
            { "receiver + antenna descriptor", "vevő- és antennaleírás" },
            { "BeiDou ephemeris", "BeiDou-pályaadatok" },
            { "QZSS ephemeris", "QZSS-pályaadatok" },
            { "Galileo ephemeris", "Galileo-pályaadatok" },
            { "GLONASS code-phase biases", "GLONASS kódfázis-eltérések" },
            { "text", "szöveg" },
            { "empty", "üres" },
            { "Press New Shortcut Key", "Nyomd meg az új gyorsbillentyűt" },
            { "Press Button to Edit Shortcut", "Nyomd meg a gombot a gyorsbillentyű szerkesztéséhez" },
            { " ???? \r\n Not Connected", " ???? \r\n Nincs kapcsolat" },
            { "1: Set distance, 2: Tap Build, 3: Create Clip Lines", "1: Add meg a távolságot, 2: Koppints az Építés gombra, 3: Hozd létre a vágóvonalakat" },
            { "XTE Chart", "XTE-diagram" },
            { "Drive Steady", "Haladj egyenletesen" },
            { "Ready to Apply", "Készen áll az alkalmazásra" },
            { "Collecting...", "Adatgyűjtés..." },
            { "Error, CPD set to 100", "Hiba, a CPD 100-ra állt" },
            { "Ackermann Set to: ", "Az Ackermann értéke: " },
            { "Error, Ackermann set to 100", "Hiba, az Ackermann 100-ra állt" },
            { "Select Square Below And Pick New Color", "Válassz egy négyzetet lent, és add meg az új színt" },
            { "Select Preset Color", "Válassz egy előre beállított színt" },
            { "Pick New Color and Select Square Below to Save Preset", "Válaszd ki az új színt, majd egy négyzetet lent a mentéshez" },
            { "Fields directory not found", "A táblák mappája nem található" },
            { "Error loading fields", "Hiba a táblák betöltésekor" },
            { "No tracks found in this field", "Ebben a táblában nincs nyomvonal" },
            { "Error loading tracks", "Hiba a nyomvonalak betöltésekor" },
            { "Saving current tracks...", "A jelenlegi nyomvonalak mentése..." },
            { "Converting tracks...", "Nyomvonalak átalakítása..." },
            { "Saving tracks...", "Nyomvonalak mentése..." },
            { "Selected Field: ", "Kiválasztott tábla: " },
            { "Error loading fields: ", "Hiba a táblák betöltésekor: " },
            { "Easy Drive Active", "Egyszerű vezetés aktív" },
            { "Load Environment:", "Környezet betöltése:" },
            { "Vehicle: ", "Jármű: " },
            { "Tool: ", "Munkagép: " },
            { "Environment: ", "Környezet: " },
            { "Critical Save Error", "Súlyos mentési hiba" },
            { "Shutdown Error", "Leállítási hiba" },
            { "Direction Reset", "Irány visszaállítva" },
            { "Forward is Set", "Az előre irány beállítva" },
            { "AutoSteer Disabled", "Az automata kormányzás kikapcsolt" },
            { "Nudge Window Open", "Az eltolás ablak nyitva van" },
            { "Close Nudge Window", "Zárd be az eltolás ablakot" },
            { "High Field Start Distance Warning", "Nagy távolság a tábla kezdőpontjától" },
            { "AgShare upload failed", "Az AgShare-feltöltés sikertelen" },
            { "An error occurred while starting upload to AgShare.", "Hiba történt az AgShare-feltöltés indításakor." },
            { "An error occurred during upload to AgShare.", "Hiba történt az AgShare-feltöltés közben." },
            { "Field NOT saved", "A tábla NEM lett elmentve" },
            { "Saving the field failed, so it was left open. Check disk space and try again.", "A mentés nem sikerült, ezért a tábla nyitva maradt. Ellenőrizd a szabad lemezterületet, és próbáld újra." },
            { "Resume Style", "Folytatás módja" },
            { "Last Stopped Position", "Az utolsó megállás helyétől" },
            { "Closest Point", "A legközelebbi pontról" },
            { "Start At Beginning", "Az elejétől" },
            { "Cannot use with zones", "Zónákkal nem használható" },
            { "Only for Sections", "Csak szakaszokhoz" },
            { "Easy Drive", "Egyszerű vezetés" },
            { "Profiles not available in Easy Drive mode", "Egyszerű vezetés módban a profilok nem érhetők el" },
            { "Sections are on", "A szakaszok be vannak kapcsolva" },
            { "Turn Auto or Manual Off First", "Előbb kapcsold ki az automata vagy a kézi módot" },
            { "Reset Direction", "Irány visszaállítása" },
            { "Drive Forward > 1.5 kmh", "Haladj előre 1,5 km/h felett" },
            { "Autosteer Turned Off", "Az automata kormányzás kikapcsolt" },
            { "RTK Fix Alarm", "RTK fix riasztás" },
            { "Track Changed", "A nyomvonal megváltozott" },
            { "Paths Enabled", "Az útvonalak engedélyezve" },
            { "Sim Reverse Touched", "Szimulátor: tolatás aktiválva" },
            { "Hotkey Triggered", "Gyorsbillentyű aktiválva" },
            { "Settings Sent To Steer Module", "A beállítások elküldve a kormánymodulnak" },
            { "Return From Editing", "Visszatérés a szerkesztésből" },
            { "Updater not found. Please ensure AgOpenGPS.Updater.exe is in the application directory.", "A frissítő nem található. Ellenőrizd, hogy az AgOpenGPS.Updater.exe a program mappájában van-e." },
            { "Updater Not Found", "A frissítő nem található" },
            { "Not enough points to make a boundary", "Nincs elég pont a határvonal elkészítéséhez" },
            { "Can't calculate center of field. Missing Outer Boundary or AB line.", "A tábla közepe nem számolható ki: hiányzik a külső határvonal vagy az AB-vonal." },
            { "You can't have more zones then sections", "Nem lehet több zóna, mint szakasz" },
            { "Simulator is off", "A szimulátor ki van kapcsolva" },
            { "Simulator can't work while using real Antenna", "A szimulátor nem működik, amíg valódi antennát használsz" },
            { "Reset To Default", "Alapértékek visszaállítva" },
            { "Values Set to Inital Default", "Az értékek visszaálltak az alapértelmezettre" },
            { "Exceeded Range", "Tartományon kívül" },
            { "Offset too large - cannot apply", "Az eltolás túl nagy, nem alkalmazható" },
            { "Smart WAS Zero", "Okos WAS-nullázás" },
            { "Defaults Set", "Alapértékek beállítva" },
            { "CPD and Ackermann set to defaults", "A CPD és az Ackermann alapértékre állt" },
            { "P Gain Change", "P-erősítés módosítva" },
            { "Proportional Gain set to 1", "Az arányos erősítés 1-re állt" },
            { "Profiles Created", "Profilok létrehozva" },
            { "Default profiles created", "Az alapértelmezett profilok létrejöttek" },
            { "Environment settings reset!", "A környezeti beállítások visszaálltak!" },
            { "Environment settings loaded!", "A környezeti beállítások betöltődtek!" },
            { "Profile files not found!\n\nThe profile(s) in the registry cannot be found.\nPlease select or create new profiles.\n\nEnvironment will use defaults if not selected.", "Nem találhatók a profilfájlok!\n\nA beállításjegyzékben (registry) megadott profilok nem találhatók.\nVálassz ki vagy hozz létre új profilokat.\n\nHa nem választasz, a program az alapértékeket használja." },
            { "No profiles found.\n\nUse 'New' to create Vehicle and Tool profiles.\n\nEnvironment will use defaults if not selected.", "Nincs profil.\n\nAz 'Új' gombbal hozhatsz létre jármű- és munkagép-profilt.\n\nHa nem választasz, a program az alapértékeket használja." },
            { "Serious Field Origin Error\r\n\r\nField Origin is More Then 20 km from your current GPS Position Delete this field and create a new one as Accuracy will be poor\r\n\r\nOr you may have a field open and drove far away", "Súlyos hiba a tábla kiindulópontjánál\r\n\r\nA tábla kiindulópontja több mint 20 km-re van a jelenlegi GPS-pozíciótól. Töröld ezt a táblát, és hozz létre újat, mert a pontosság rossz lesz.\r\n\r\nVagy lehet, hogy egy tábla nyitva van, és messzire elhajtottál." },
            { "Warning: Closing AgIO will stop communication with hardware.\r\nAre you sure you want to close?", "Figyelem: Ha bezárod az AgIO-t, megszakad a kapcsolat a hardverrel.\r\nBiztosan be akarod zárni?" },
            { "Up + Down", "Mind" },
            { "Down", "Inaktív" },
            { "Auto Hide", "Automatikus elrejtés" },
            { "Auto\r\nRun", "Auto\r\nfutás" },

            // ---- profiles / settings ----
            { "Profile", "Profil" },
            { "Profiles", "Profilok" },
            { "Current Profile", "Jelenlegi profil" },
            { "Curent Profile", "Jelenlegi profil" },
            { "New profile", "Új profil" },
            { "New:", "Új:" },
            { "Using Profile: ", "Használt profil: " },
            { "AgIO: Manage Profiles", "AgIO: profilok kezelése" },
            { "Overwrite This Profile:", "Felülírandó profil:" },
            { "Advanced Settings", "Speciális beállítások" },
            { "ISOBUS Settings", "ISOBUS beállítások" },
            { "Serial Pass Thru Configuration", "Soros átvezetés beállítása" },
            { "Serial Pass On", "Soros átvezetés be" },
            { "Different Port Then GPS ?", "Az RTCM a GPS-től eltérő porton megy?" },
            { "Start Minimized", "Indítás minimalizálva" },
            { "PopUp AgIO on Warn", "AgIO előugrik figyelmeztetéskor" },
            { "Auto Start GPS-Out", "GPS-Out automatikus indítása" },
            { "Show\r\nOn\r\nWarn", "Jelzés\r\nesetén" },

            // ---- viewers / tools ----
            { "Event Log Viewer", "Eseménynapló" },
            { "Log Viewer", "Napló" },
            { "Serial Monitor", "Soros monitor" },
            { "UDP Monitor", "UDP monitor" },
            { "PGN Guide", "PGN útmutató" },
            { "Guide ->", "Útmutató ->" },
            { "Keyboard", "Billentyűzet" },
            { "Message text goes here", "Az üzenet szövege itt jelenik meg" },
            { "AgIO Message", "AgIO üzenet" },
            { "AgTwo Message", "AgTwo üzenet" },

            // ---- NTRIP panel (AgIO) ----
            { "Stop", "Leállítás" },
            { "Start", "Indítás" },
            { "NTRIP Details", "NTRIP részletek" },
            { "NTRIP is off", "Az NTRIP ki van kapcsolva" },
            { "FALLBACK IN USE", "TARTALÉK FORRÁS AKTÍV" },
            { "Are the corrections good?", "A korrekció állapota:" },
            { "Switch to fallback", "Váltás a tartalék forrásra" },
            { "Try the main service again", "Fő szolgáltató újrapróbálása" },
            { "Switch back to main", "Vissza a fő szolgáltatóra" },
            { "Connected, waiting for data", "Csatlakozva, korrekciós jelre vár" },
            { "Connecting...", "Csatlakozás..." },
            { "Mountpoint not found", "A mountpoint nem található" },
            { "Cannot log in", "Sikertelen bejelentkezés" },
            { "Stopped", "Leállítva" },
            { "Service is starting up", "A szolgáltató éppen indul" },
            { "Service is busy", "A szolgáltató túlterhelt" },
            { "Connected, nothing coming in", "Csatlakozva, de nem jön korrekciós jel" },
            { "No network", "Nincs internetkapcsolat" },
            { "Reconnecting...", "Újracsatlakozás..." },
            { "Service not understood", "A szolgáltató válasza értelmezhetetlen" },
            { "Cannot reach the service", "A szolgáltató nem érhető el" },
            { "Corrections coming in", "Korrekciós jel érkezik" },
            { "This stream cannot be checked in detail.", "Ez az adatfolyam részletesen nem ellenőrizhető." },
            { "Base station position received", "A bázisállomás pozíciója megvan" },
            { "Base station position", "Bázisállomás pozíciója" },
            { "Waiting for it. It comes every 10 seconds.", "Még várjuk, 10 másodpercenként érkezik." },
            { "Base station position is missing", "Hiányzik a bázisállomás pozíciója" },
            { "RTK cannot start without it.", "Enélkül nem indulhat el az RTK." },
            { "Not received yet.", "Még nem érkezett meg." },
            { "Distance to base station", "Távolság a bázisállomástól" },
            { "Waiting for your GPS position.", "A távolság a GPS-pozíció megérkezése után számolható." },
            { "Too far for good RTK. Choose a base station closer by.", "Túl messze van a pontos RTK-hoz. Válassz közelebbi bázisállomást." },
            { "Far away. RTK is less accurate at this distance.", "Messze van, ezen a távolságon az RTK kevésbé pontos." },
            { "From the fixed position in the NTRIP settings.", "A beállított rögzített pozícióból számolva." },
            { "Satellites in the corrections", "A korrekció műholdrendszerei" },
            { "No satellite data in the corrections", "A korrekció nem tartalmaz műholdadatot" },
            { "The base station sends no observations.", "A bázisállomás nem küld méréseket." },
            { "Nothing received yet.", "Még nem érkezett semmi." },
            { "Fresh data", "A korrekció friss" },
            { "Waiting for the first data.", "Várakozás az első adatra." },
            { "Data is late", "A korrekció késik" },
            { "No new data", "Nem jön új korrekció" },
            { "Your GPS: RTK fixed", "A GPS RTK fix állapotban van" },
            { "Your GPS: RTK float", "A GPS RTK lebegés módban van" },
            { "Still working it out. Wait a moment.", "Még pontosítja a helyzetét, várj egy kicsit." },
            { "Your GPS: no RTK yet", "A GPS még nem használ RTK-t" },
            { "Normal GPS accuracy only.", "Egyelőre csak normál GPS-pontosság." },
            { "Your GPS has no fix", "A GPS-vevőnek még nincs helyzete (nincs fix)" },
            { "Your GPS is in manual or simulator mode", "A GPS kézi vagy szimulátor módban van" },

            // ---- messages and tool tips ----
            { "No address", "Hiányzik a cím" },
            { "Fill in the address first.", "Előbb add meg a szolgáltató címét." },
            { "No port", "Nincs port megadva" },
            { "Could not save", "A mentés nem sikerült" },
            { "Error opening port", "A port nem nyitható meg" },
            { "Cannot use NTRIP yet", "Az NTRIP még nem használható" },
            { "Choose a profile and tap Use first.", "Előbb válassz egy profilt, és koppints a Használ gombra." },
            { "The profile in use has no service address or radio port yet. Edit it first.", "A használatban lévő profilban még nincs szolgáltatói cím vagy rádióport. Előbb egészítsd ki." },
            { "Cannot read it", "A profil nem olvasható" },
            { "This profile file could not be read.", "Ezt a profilfájlt nem lehetett beolvasni." },
            { "Cannot rename", "Az átnevezés nem lehetséges" },
            { "That name is already used.", "Ilyen nevű profil már van." },
            { "Profile Load Warning", "Figyelmeztetés a profil betöltésekor" },
            { "Profile Recovery", "Profil helyreállítva" },
            { "AgIO profile was recovered from .last backup because the main file was unreadable.", "A fő profilfájl nem volt olvasható, ezért az AgIO a .last biztonsági mentésből állította helyre a profilt." },
            { "File Saved", "Fájl elmentve" },
            { "Invalid Id", "Hibás azonosító" },
            { "Invalid Name", "Hibás név" },
            { "Name is not filled in", "A név mező üres" },
            { "Invalid Frequency", "Hibás frekvencia" },
            { "Frequency is not filled in", "A frekvencia mező üres" },
            { "No channel", "Nincs csatorna kiválasztva" },
            { "Radio is set to on. But no channel is selected", "A rádió be van kapcsolva, de nincs csatorna kiválasztva." },
            { "NTRIP also enabled", "Az NTRIP is be volt kapcsolva" },
            { "NTRIP is also enabled, diabling it", "Az NTRIP is be volt kapcsolva, ezért kikapcsoltuk." },
            { "AgIO Default Profile Used", "Az AgIO az alapértelmezett profillal fut" },
            { "Using Default Profile", "Alapértelmezett profil van használatban" },
            { "Radio NTRIP ON", "A rádiós NTRIP be van kapcsolva" },
            { "Serial NTRIP ON", "A soros NTRIP be van kapcsolva" },
            { "Serial Pass NTRIP ON", "A soros átvezetés be van kapcsolva" },
            { "Turn on NTRIP", "Kapcsold be az NTRIP-et" },
            { "Error connecting to Serial Pass", "Nem sikerült csatlakozni a soros átvezetéshez" },
            { "Error connecting to radio", "Nem sikerült csatlakozni a rádióhoz" },
            { "IP Set", "IP-cím beállítva" },
            { "No File Found", "A fájl nem található" },
            { "Enter a File Name To Save...", "Add meg a fájl nevét a mentéshez." },
            { "Save And Return", "Mentés és vissza" },
            { "Unable to connect to Port", "Nem sikerült csatlakozni a porthoz" },
            { "Connection already terminated?", "A kapcsolat már megszakadt." },
            { "Connection already terminated??", "A kapcsolat már megszakadt." },
            { "No Arduino Port Active", "Nincs aktív Arduino-port" },
            { "Serious Network Connection Error", "Súlyos hálózati hiba" },
            { "Critical Registry Error", "Súlyos hiba a Windows-beállításokban (registry)" },
            { "Can't delete the Registery SubKeyTree", "A registry alkulcs nem törölhető" },
            { "AOG-TaskController is not installed??", "Az AOG-TaskController nincs telepítve." },
            { "Radio is on. Switch it off in the radio settings first.", "A rádió be van kapcsolva. Előbb a rádió beállításaiban kapcsold ki." },
            { "Serial pass-through is on. Switch it off first.", "A soros átvezetés be van kapcsolva. Előbb kapcsold ki." },
            { "NTRIP is on. Switch it off in the NTRIP profile list first.", "Az NTRIP be van kapcsolva. Előbb az NTRIP profillistában kapcsold ki." },

            // ---- small captions ----
            { "Distance", "Távolság" },
            { "Clr", "Töröl" },
            { "Error\r\nMsg", "Hiba\r\nüzenet" },
            { "Message 2", "Üzenet 2" },
            { "Module 3", "Modul 3" },
            { "Serial Monitor - Baud: Teensy - 115200   Arduino - 38400", "Soros monitor – baud: Teensy – 115200   Arduino – 38400" },
            { "Send $GN---", "$GN--- küldése" },
            { "Send $GP---", "$GP--- küldése" },
            { "Roll: 0°", "Oldaldőlés: 0°" },
            { "Steer: 0.0°", "Kormány: 0.0°" },
            { "Kmh:", "km/h:" },
            { "Kmh: 0.0", "km/h: 0.0" },
            { "M/Sec:", "m/s:" },
            { "M/Sec: 0.0", "m/s: 0.0" },
            { "NTRIP output", "NTRIP kimenet" },

            // ---- NTRIP settings, profiles, status texts ----
            { "A fixed position", "Rögzített pozíció" },
            { "Another NTRIP service", "Másik NTRIP szolgáltató" },
            { "Automatic", "Automatikus" },
            { "Both", "Mindkettő" },
            { "Fallback", "Tartalék" },
            { "I switch myself", "Magam váltok" },
            { "Main service", "Fő szolgáltató" },
            { "My AIO Board (Network)", "AIO kártyám (hálózaton)" },
            { "My GPS Receiver (Serial)", "GPS vevőm (soros porton)" },
            { "My GPS position", "A GPS-em pozíciója" },
            { "NTRIP service", "NTRIP szolgáltató" },
            { "Output and position", "Kimenet és pozíció" },
            { "Serial radio", "Soros rádió" },
            { "Switch by itself", "Automatikus váltás" },
            { "IN USE NOW", "AKTÍV" },
            { "IN USE", "AKTÍV" },
            { "MAIN SERVICE", "FŐ SZOLGÁLTATÓ" },
            { "FALLBACK", "TARTALÉK" },
            { "Off. No second service is used.", "Ki van kapcsolva, második szolgáltató nincs használatban." },
            { "(no port yet)", "(még nincs port)" },
            { "You switch by hand", "A váltást te végzed kézzel" },
            { "Raw TCP (no mountpoint or login)", "Nyers TCP (mountpoint és bejelentkezés nélkül)" },
            { "No mountpoint chosen", "Nincs mountpoint kiválasztva" },
            { "Not filled in yet", "Még nincs kitöltve" },
            { "Needs your position", "Kell hozzá a pozíciód" },
            { "Place", "Hely" },
            { "(not saved yet)", "(még nincs mentve)" },
            { "Choose the port of the radio first.", "Előbb válaszd ki a rádió portját." },
            { "Choose the radio port to use Fallback", "A tartalék használatához válaszd ki a rádió portját." },
            { "Fill in Caster information to use Fallback", "A tartalék használatához add meg a szerver adatait." },
            { "No profiles yet.\r\n\r\nTap New profile to make one.", "Még nincs profil.\r\n\r\nKoppints az Új profil gombra, és hozz létre egyet." },
            { "This profile is not filled in yet.\r\nTap Edit to fill in the main service.", "Ez a profil még üres.\r\nKoppints a Szerkesztés gombra, és add meg a fő szolgáltatót." },
            { "No NTRIP profile is chosen yet. Choose or make one in the NTRIP settings.", "Még nincs NTRIP profil kiválasztva. Válassz vagy hozz létre egyet az NTRIP beállításokban." },
            { "Choose the port of the main radio in the NTRIP profile.", "Az NTRIP profilban válaszd ki a fő rádió portját." },
            { "NTRIP is back online", "Az NTRIP újra működik" },
            { "Corrections are coming in again.", "A korrekciós jel újra érkezik." },
            { "NTRIP login rejected", "NTRIP: a bejelentkezést elutasították" },
            { "NTRIP mountpoint not found", "NTRIP: a mountpoint nem található" },
            { "NTRIP caster busy", "NTRIP: a szerver túlterhelt" },
            { "NTRIP no corrections", "NTRIP: nem jön korrekciós jel" },
            { "NTRIP stream lost", "NTRIP: megszakadt az adatfolyam" },
            { "NTRIP not connected", "NTRIP: nincs kapcsolat" },
            { "NTRIP Off", "NTRIP kikapcsolva" },
            { "Listening NTRIP", "NTRIP: figyelés" },
            { "Connection Problem", "Kapcsolódási probléma" },
            { "Not Connecting To Caster", "Nem sikerül csatlakozni a szerverhez" },
            { "no IPv4 address found", "nem található IPv4-cím" },
            { "NTRIP Connection Failed", "Az NTRIP-kapcsolat sikertelen" },
            { "NTRIP Connection Rejected", "Az NTRIP-kapcsolatot elutasították" },
            { "Caster closed the connection without sending any data (check mountpoint/credentials)", "A szerver adat küldése nélkül bontotta a kapcsolatot. Ellenőrizd a mountpointot és a bejelentkezési adatokat." },
            { "NTRIP Client Not Set Up", "Az NTRIP kliens még nincs beállítva" },
            { "Choose Existing or Create New Profile", "Válassz meglévő profilt, vagy hozz létre újat." },
            { "Create or Choose a Profile", "Hozz létre vagy válassz egy profilt." },
            { "Turn it off before using Serial Pass Thru", "A soros átvezetés használata előtt kapcsold ki." },
            { "Turn it off before using NTRIP", "Az NTRIP használata előtt kapcsold ki." },
            { "Turn it off before using Radio NTRIP", "A rádiós NTRIP használata előtt kapcsold ki." },
            { "Air NTRIP ON", "A rádiós NTRIP be van kapcsolva" },
            { "New Values Changed", "Az új értékek érvénybe léptek." },
            { "UDP On", "UDP be" },
            { "UDP Is On", "Az UDP be van kapcsolva" },
            { "AgIO will Restart to Enable UDP Networking Features", "Az UDP-hálózat bekapcsolásához az AgIO újraindul." },
            { "AgIO will Restart to Disable UDP Networking Features", "Az UDP-hálózat kikapcsolásához az AgIO újraindul." },
            { "ModSim will Restart to Enable UDP Networking Changes", "A hálózati változások érvényesítéséhez a ModSim újraindul." },
            { "Man IP", "Kézi IP" },
            { "NTRIP UDP", "NTRIP UDP" },

            // ---- updater ----
            { "Updater Error", "Hiba a frissítőben" },
            { "AgOpenGPS Update", "AgOpenGPS frissítés" },
            { "Please wait...", "Kérlek, várj..." },
            { "Please Wait", "Kérlek, várj" },
            { "Please wait for the current operation to complete.", "Várd meg, amíg a folyamatban lévő művelet befejeződik." },
            { "Checking GitHub releases...", "Új kiadások keresése a GitHubon..." },
            { "Checking USB drive...", "USB-meghajtó ellenőrzése..." },
            { "No update found on USB drive.", "Nincs frissítés az USB-meghajtón." },
            { "Closing AgOpenGPS and AgIO...", "Az AgOpenGPS és az AgIO bezárása..." },
            { "Applications closed", "A programok bezárultak" },
            { "Downloading from GitHub...", "Letöltés a GitHubról..." },
            { "Download Failed", "Sikertelen letöltés" },
            { "Download failed", "Sikertelen letöltés" },
            { "Copying from USB...", "Másolás az USB-ről..." },
            { "Copy Failed", "Sikertelen másolás" },
            { "Copy failed", "Sikertelen másolás" },
            { "Installation Failed", "Sikertelen telepítés" },
            { "Installation failed", "Sikertelen telepítés" },
            { "Complete! Restarting...", "Kész! Újraindítás..." },
            { "Complete!", "Kész!" },
            { "Update was cancelled.", "A frissítést megszakítottad." },
            { "No release notes available.", "Ehhez a kiadáshoz nincs leírás." },
            { "Release Candidate", "Előzetes kiadás" },
            { "Download failed: the file size does not match the release.", "A letöltés nem sikerült: a fájl mérete nem egyezik a kiadáséval." },
            { "Download failed: the checksum does not match the release, the file was not installed.", "A letöltés nem sikerült: az ellenőrző összeg nem egyezik, ezért a fájlt nem telepítettük." },
            { "No suitable download asset found in this release.", "Ebben a kiadásban nincs letölthető telepítőfájl." },
            { "Another updater instance is already running.", "A frissítő már fut egy másik ablakban." },
            { "No applications were running.", "Egyik program sem futott." },
            { "Applications closed successfully.", "A programok sikeresen bezárultak." },
            { "Update file not found.", "A frissítőfájl nem található." },
            { "Installation directory does not exist.", "A telepítési mappa nem létezik." },
            { "Creating backup...", "Biztonsági mentés készül..." },
            { "Extracting files...", "Fájlok kicsomagolása..." },
            { "Installing files...", "Fájlok telepítése..." },
            { "AgOpenGPS.exe not found in installation directory.", "Az AgOpenGPS.exe nem található a telepítési mappában." },
            { "AgOpenGPS restarted successfully.", "Az AgOpenGPS újraindult." },
            // ---- updater (older) ----
            { "Check for Updates", "Frissítések keresése" },
            { "Install Update", "Frissítés telepítése" },
            { "Include pre-release", "Előzetes kiadások is" },
            { "Firmware Update", "Firmware frissítés" },
            { "AgOpenGPS Firmware Update", "AgOpenGPS firmware frissítés" },
            { "AgOpenGPS Updater", "AgOpenGPS frissítő" },
            { "Release Notes", "Újdonságok" },
            { "View Release Notes", "Újdonságok megtekintése" },
            { "Release notes will appear here.", "Az újdonságok itt jelennek meg." },
            { "Current Version: -", "Jelenlegi verzió: -" },
            { "Latest Version: -", "Legújabb verzió: -" },
            { "Latest Version: Up to date", "Legújabb verzió: a program naprakész" },
            { "Web update - Click Check for Updates", "Internetes frissítés – kattints a Frissítések keresése gombra" },
            { "Web update (GitHub Releases)", "Internetes frissítés (GitHub)" },
            { "Web update (Local update available)", "Internetes frissítés (helyi frissítés is elérhető)" },
            { "Local: No AgOpenGPS_*.zip found on USB", "USB: nincs AgOpenGPS_*.zip fájl" },
            { "Local: Place AgOpenGPS.zip in root of USB drive", "USB: másold az AgOpenGPS.zip fájlt az USB-meghajtó gyökerébe" },
            { "AOG-TaskController not installed, please download from:", "Az AOG-TaskController nincs telepítve. Innen töltheted le:" },
            { "Linux Users", "Linux-felhasználóknak" },
            { "Linux Only !", "Csak Linuxon!" },
        };
    }
}
