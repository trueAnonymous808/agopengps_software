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
        private static Timer timer;
        private static bool active;

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
            if (active || !IsHungarian()) return;
            active = true;

            // New forms are picked up within a fraction of a second, and texts that the program sets later
            // (for example a status label) are translated as well.
            timer = new Timer { Interval = 150 };
            timer.Tick += (s, e) =>
            {
                try
                {
                    foreach (Form form in Application.OpenForms) Apply(form);
                }
                catch (Exception)
                {
                    // never let the translation disturb the program
                }
            };
            timer.Start();
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
        public static string Tr(string english)
        {
            if (!active || english == null) return english;
            return Lookup(english) ?? english;
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

            c.Text = hu;
            Fit(c, current, hu, state);
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
            int availWidth = c.ClientSize.Width - (isButton ? 8 : 2);
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
            { "Open:", "Megnyitás:" },
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
            { "Elev", "Magasság" },
            { "Heading", "Irány" },
            { "Speed", "Sebesség" },
            { "Speed:", "Sebesség:" },
            { "Sats", "Műholdak" },
            { "Sats:", "Műholdak:" },
            { "# Sats", "Műholdak" },
            { "Quality", "Fix minőség" },
            { "Quality:", "Fix minőség:" },
            { "FixQual", "Fix minőség" },
            { "Age", "Korrekció kora" },
            { "Age:", "Korrekció kora:" },
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
            { "Steer Ping:", "Kormány ping:" },
            { "Steer \r\nConfig", "Kormány \r\nbeállítás" },
            { "Steer\r\nSetting", "Kormány\r\nbeállítás" },
            { "From\r\nAutoSteer", "Kormányzástól" },
            { "Set Angle", "Célszög" },
            { "Act Angle", "Aktuális szög" },
            { "Angle:", "Szög:" },
            { "Machine", "Munkagép" },
            { "Machine:", "Munkagép:" },
            { "Machine Module", "Munkagép modul" },
            { "Mach", "Gép" },
            { "Tool Ping:", "Munkagép ping:" },
            { "Work", "Munka" },
            { "Work Switch", "Munkakapcsoló" },
            { "Work:", "Munka:" },
            { "Switch:", "Kapcsoló:" },
            { "Lift Enable", "Emelés engedélyezve" },
            { "Hyd Lift", "Hidraulikus emelő" },
            { "Raise Time", "Emelési idő" },
            { "LowerTime", "Leengedési idő" },
            { "Min Speed", "Min. sebesség" },
            { "Min PWM", "Min. PWM" },
            { "Hi PWM", "Magas PWM" },
            { "Lo PWM", "Alacsony PWM" },
            { "Pulse #", "Impulzus #" },
            { "PulseCounts", "Impulzusszám" },
            { "Counts:", "Számláló:" },
            { "Relay Act Hi", "Relé: magas aktív" },
            { "Pressure", "Nyomás" },
            { "Ackerman", "Ackermann" },
            { "Uturn", "Fordulás" },
            { "Tram", "Techn. nyom" },
            { "Up", "Aktív" },
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
            { "PopUp AgIO on Warn", "AgIO felugrik figyelmeztetésnél" },
            { "Auto Start GPS-Out", "GPS-Out automatikus indítása" },
            { "Show\r\nOn\r\nWarn", "Megjelen\r\nfigyelm.\r\nesetén" },

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
            { "Your GPS: RTK float", "A GPS RTK float állapotban van" },
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
