using AgIO.Configurator;
using AgLibrary.Firmware;
using AgLibrary.Gnss;
using AgLibrary.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AgIO
{
    // AgIO menu > Module configurator: puts a firmware on the AiO board's Teensy over USB (the AgOpenGPS ones
    // modified for TDCP and receiver commands, or any Config-O-Matic offers, downloaded and cached), and sets up the
    // GNSS receivers (UM982, u-blox F9P) through the module over Ethernet - no taking the box apart for that.
    public sealed class FormConfigurator : Form
    {
        private readonly FormLoop mf;
        private readonly FirmwareCatalog catalog = new FirmwareCatalog();
        private CancellationTokenSource work;

        private readonly Bitmap icon;
        private readonly Panel pageFirmware, pageReceiver;
        private readonly Button btnPageFirmware, btnPageReceiver;

        // firmware page
        private readonly ListBox lstFirmware = new ListBox();
        private readonly Button btnRefresh, btnBootloader, btnFlash, btnNetFlash;
        private readonly NetFirmwareUpdater netUpdater;
        private readonly ComboBox cboCom = new ComboBox();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Label lblFirmware = new Label();

        // receiver page
        private readonly ComboBox cboPreset = new ComboBox();
        private readonly Button btnPortMain, btnPortHeading, btnDetect, btnSendPreset, btnSendCommand, btnSave;
        private int receiverPort = 1;
        private readonly TextBox txtCommand = new TextBox();
        private readonly Label lblReceiver = new Label();
        private ReceiverKind kind = ReceiverKind.Unknown;

        private readonly TextBox log = new TextBox();

        // the answer the sender waits for
        private readonly object answerLock = new object();
        private Func<int, byte[], bool> awaited;
        private readonly AutoResetEvent answered = new AutoResetEvent(false);

        private static string Tr(string s) => AgLibrary.Localization.UiTranslator.TrText(s);

        public FormConfigurator(FormLoop mf)
        {
            this.mf = mf;
            Text = Tr("Module configurator");
            Font = new Font("Segoe UI", 10f);
            BackColor = Color.FromArgb(236, 239, 243);
            ClientSize = new Size(940, 700);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            ShowInTaskbar = false;
            icon = ConfiguratorIcon.Draw(64);
            Icon = Icon.FromHandle(new Bitmap(icon, 32, 32).GetHicon());

            // ---- header: picture, name, what it is for ----
            var pic = new PictureBox { Image = icon, Location = new Point(16, 12), Size = new Size(56, 56), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            var title = new Label { Text = Tr("Module configurator"), Location = new Point(84, 12), AutoSize = true, Font = new Font("Segoe UI Semibold", 16f) };
            var subtitle = Muted(Tr("Firmware for the AiO board's Teensy and settings for its GNSS receivers - over the network, without opening the box."), 86, 46, 820, 22);

            // ---- the two pages, chosen with big buttons ----
            pageFirmware = new Panel { Location = new Point(16, 132), Size = new Size(908, 380) };
            pageReceiver = new Panel { Location = new Point(16, 132), Size = new Size(908, 380), Visible = false };
            btnPageFirmware = PageButton(Tr("Teensy firmware"), 16);
            btnPageReceiver = PageButton(Tr("GNSS receiver"), 236);
            btnPageFirmware.Click += (s, e) => ShowPage(true);
            btnPageReceiver.Click += (s, e) => ShowPage(false);

            // firmware: what to put on (left), how (right)
            Panel choose = Card(pageFirmware, 0, 0, 520, 380, Tr("Firmware"));
            Muted(Tr("Green ★ = from the AgOpenGPS fork, modified: raw GNSS for TDCP, receiver settings and firmware update over the network, UM982 dual heading. Red = the original Config-O-Matic firmwares, unchanged."), 14, 34, 492, 52, choose);
            lstFirmware.SetBounds(14, 90, 492, 234);
            lstFirmware.DrawMode = DrawMode.OwnerDrawFixed;
            lstFirmware.ItemHeight = 40;
            lstFirmware.IntegralHeight = false;
            lstFirmware.BorderStyle = BorderStyle.FixedSingle;
            lstFirmware.DrawItem += DrawFirmware;
            btnRefresh = Btn(Tr("Download / update list"), 14, 332, 240, 36);
            btnRefresh.Click += async (s, e) => await RefreshCatalog();
            choose.Controls.AddRange(new Control[] { lstFirmware, btnRefresh });

            Panel put = Card(pageFirmware, 530, 0, 378, 380, Tr("Put it on the module"));
            btnNetFlash = Btn(Tr("Over the network (no USB)"), 14, 40, 350, 50, true);
            btnNetFlash.Click += async (s, e) => await NetFlash();
            Muted(Tr("When a modified firmware is on the module already."), 14, 92, 350, 22, put);
            btnFlash = Btn(Tr("Over USB"), 14, 122, 350, 44);
            btnFlash.Click += async (s, e) => await Flash();
            Muted(Tr("The first time, or when the network does not work. USB port of the Teensy:"), 14, 168, 350, 40, put);
            cboCom.SetBounds(14, 210, 150, 28);
            cboCom.DropDownStyle = ComboBoxStyle.DropDownList;
            btnBootloader = Btn(Tr("Start bootloader"), 174, 206, 190, 34);
            btnBootloader.Click += (s, e) => StartBootloader();
            progress.SetBounds(14, 262, 350, 18);
            lblFirmware.SetBounds(14, 284, 350, 86);
            put.Controls.AddRange(new Control[] { btnNetFlash, btnFlash, cboCom, btnBootloader, progress, lblFirmware });

            // receiver: which one (1), what to send (2), by hand (3)
            Panel which = Card(pageReceiver, 0, 0, 908, 128, "1. " + Tr("Which receiver"));
            btnPortMain = Btn(Tr("Main receiver (position)"), 14, 40, 300, 40, true);
            btnPortHeading = Btn(Tr("Second F9P (heading)"), 324, 40, 260, 40);
            btnPortMain.Click += (s, e) => SetPort(1);
            btnPortHeading.Click += (s, e) => SetPort(2);
            Muted(Tr("The UM982 (dual antenna in one) and a single F9P are always the main receiver. The second one is only there with two F9Ps for dual antenna: it measures the heading."), 14, 84, 570, 40, which);
            btnDetect = Btn(Tr("Detect"), 600, 40, 150, 40);
            btnDetect.Click += async (s, e) => await Detect();
            lblReceiver.SetBounds(760, 40, 136, 80);
            lblReceiver.Font = new Font("Segoe UI Semibold", 10.5f);
            lblReceiver.Text = "?";
            which.Controls.AddRange(new Control[] { btnPortMain, btnPortHeading, btnDetect, lblReceiver });

            Panel send = Card(pageReceiver, 0, 136, 908, 112, "2. " + Tr("Setting to send"));
            cboPreset.SetBounds(14, 44, 520, 28);
            cboPreset.DropDownStyle = ComboBoxStyle.DropDownList;
            btnSendPreset = Btn(Tr("Send"), 544, 38, 150, 40, true);
            btnSendPreset.Click += async (s, e) => await SendPreset();
            btnSave = Btn(Tr("Save in the receiver"), 704, 38, 190, 40);
            btnSave.Click += async (s, e) => await SaveReceiver();
            Muted(Tr("Without saving, the receiver forgets it when switched off."), 14, 82, 870, 22, send);
            send.Controls.AddRange(new Control[] { cboPreset, btnSendPreset, btnSave });

            Panel byHand = Card(pageReceiver, 0, 256, 908, 124, Tr("Command by hand"));
            txtCommand.SetBounds(14, 42, 520, 28);
            btnSendCommand = Btn(Tr("Send command"), 544, 36, 150, 40);
            btnSendCommand.Click += async (s, e) => await SendCommand();
            Muted(Tr("UM982: text commands (e.g. GPGGA COM1 0.1). u-blox: hex bytes of a UBX frame. The baud rate of the receiver's port to the module is never changed from here: the module's firmware expects it as it is."), 14, 76, 880, 42, byHand);
            byHand.Controls.AddRange(new Control[] { txtCommand, btnSendCommand });

            // ---- what happened ----
            var logTitle = new Label { Text = Tr("Log"), Location = new Point(16, 520), AutoSize = true, Font = new Font("Segoe UI Semibold", 11f) };
            log.Multiline = true;
            log.ReadOnly = true;
            log.ScrollBars = ScrollBars.Vertical;
            log.Font = new Font("Consolas", 9f);
            log.BorderStyle = BorderStyle.FixedSingle;
            log.SetBounds(16, 546, 908, 140);

            Controls.AddRange(new Control[] { pic, title, subtitle, btnPageFirmware, btnPageReceiver, pageFirmware, pageReceiver, logTitle, log });
            foreach (Control page in new Control[] { pageFirmware, pageReceiver })
                foreach (Control card in page.Controls)
                    foreach (Control c in card.Controls)
                        if (c is ButtonBase) c.BringToFront();
            ShowPage(true);
            SetPort(1);

            netUpdater = new NetFirmwareUpdater(mf.SendToFirmwareUpdate);
            mf.GnssReply += OnReply;
            mf.FirmwareUpdateReply += netUpdater.Answer;
            FormClosed += (s, e) =>
            {
                mf.GnssReply -= OnReply;
                mf.FirmwareUpdateReply -= netUpdater.Answer;
                work?.Cancel();
            };
            Load += (s, e) =>
            {
                catalog.LoadLocal();
                ShowCatalog();
                cboCom.Items.AddRange(SerialPort.GetPortNames().OrderBy(p => p).Cast<object>().ToArray());
                if (cboCom.Items.Count > 0) cboCom.SelectedIndex = 0;
                FillPresets();
                AddLog(Tr("Firmware cache") + ": " + FirmwareCatalog.CacheDir);
            };
        }

        // ---- looks ----

        private static readonly Color Green = Color.FromArgb(67, 140, 45), Slate = Color.FromArgb(84, 110, 122);
        private static readonly Color Original = Color.FromArgb(198, 40, 40);   // the original Config-O-Matic ones

        // strong colours with their own text colour: readable by day and at night alike
        private static Button Btn(string text, int x, int y, int w, int h, bool strong = false)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 10f),
                BackColor = strong ? Green : Slate,
                ForeColor = Color.White,
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderColor = strong ? Color.FromArgb(46, 100, 30) : Color.FromArgb(55, 71, 79);
            return b;
        }

        private static void Strong(Button b, bool strong)
        {
            Color back = strong ? Green : Slate;
            if (b.BackColor.ToArgb() != back.ToArgb()) b.BackColor = back;
        }

        private Button PageButton(string text, int x)
        {
            var b = Btn(text, x, 82, 210, 42);
            b.Font = new Font("Segoe UI Semibold", 11f);
            return b;
        }

        private void ShowPage(bool firmware)
        {
            pageFirmware.Visible = firmware;
            pageReceiver.Visible = !firmware;
            Strong(btnPageFirmware, firmware);
            Strong(btnPageReceiver, !firmware);
        }

        private void SetPort(int port)
        {
            receiverPort = port;
            Strong(btnPortMain, port == 1);
            Strong(btnPortHeading, port == 2);
            kind = ReceiverKind.Unknown;
            lblReceiver.Text = "?";
        }

        private static Panel Card(Control parent, int x, int y, int w, int h, string title)
        {
            var card = new Panel { Location = new Point(x, y), Size = new Size(w, h), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            card.Controls.Add(new Label { Text = title, Location = new Point(12, 8), AutoSize = true, Font = new Font("Segoe UI Semibold", 11.5f) });
            parent.Controls.Add(card);
            return card;
        }

        private static Label Muted(string text, int x, int y, int w, int h, Control parent = null)
        {
            var l = new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), ForeColor = Color.Gray, Font = new Font("Segoe UI", 9f) };
            parent?.Controls.Add(l);
            return l;
        }

        // a firmware: its name in bold - green from the AgOpenGPS fork (modified, with a star), red from the original
        // Config-O-Matic - and where it comes from below in grey
        private void DrawFirmware(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            e.DrawBackground();
            var f = (FirmwareEntry)lstFirmware.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color main = selected ? SystemColors.HighlightText : lstFirmware.ForeColor;
            Color sub = selected ? SystemColors.HighlightText : Color.Gray;
            using (var bold = new Font("Segoe UI Semibold", 10f))
            using (var small = new Font("Segoe UI", 8.5f))
            {
                string name = (f.Modified ? "★ " : "") + f.Name + (f.Available ? "" : "   (" + Tr("not downloaded") + ")");
                TextRenderer.DrawText(e.Graphics, name, bold, new Point(e.Bounds.X + 6, e.Bounds.Y + 3), selected ? main : f.Modified ? Green : Original);
                TextRenderer.DrawText(e.Graphics, f.Group, small, new Point(e.Bounds.X + 22, e.Bounds.Y + 21), sub);
            }
            e.DrawFocusRectangle();
        }

        private void AddLog(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => AddLog(line)));
                return;
            }
            log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine);
        }

        private void Busy(bool busy)
        {
            foreach (Button b in new[] { btnRefresh, btnBootloader, btnFlash, btnNetFlash, btnDetect, btnSendPreset, btnSendCommand, btnSave, btnPortMain, btnPortHeading })
                b.Enabled = !busy;
        }

        // ---- firmware ----

        private void ShowCatalog()
        {
            lstFirmware.Items.Clear();
            foreach (FirmwareEntry f in catalog.Entries)
                lstFirmware.Items.Add(f);
            if (lstFirmware.Items.Count > 0) lstFirmware.SelectedIndex = 0;
        }

        private async Task RefreshCatalog()
        {
            Busy(true);
            work = new CancellationTokenSource();
            try
            {
                AddLog(Tr("Downloading the Config-O-Matic list..."));
                int n = await Task.Run(() => catalog.Refresh((done, total, name) => BeginInvoke((Action)(() =>
                {
                    progress.Maximum = Math.Max(1, total);
                    progress.Value = Math.Min(done, progress.Maximum);
                })), work.Token, problem => AddLog(Tr("Download failed - the cached list is used") + ": " + problem)));
                AddLog(Tr("Downloaded") + ": " + n + ", " + Tr("in the cache") + ": " + catalog.Entries.Count(e => e.Available));
                ShowCatalog();
                FillPresets();
            }
            catch (Exception ex)
            {
                AddLog(Tr("Download failed - the cached list is used") + ": " + ex.Message);
            }
            finally
            {
                Busy(false);
            }
        }

        private void StartBootloader()
        {
            if (cboCom.SelectedItem == null) return;
            string port = cboCom.SelectedItem.ToString();
            AddLog(TeensyLoader.RebootToBootloader(port) ? Tr("Bootloader requested on") + " " + port : Tr("Could not open") + " " + port);
        }

        private async Task Flash()
        {
            if (!(lstFirmware.SelectedItem is FirmwareEntry fw)) return;
            if (!fw.Available)
            {
                AddLog(Tr("Not in the cache yet: download the list first"));
                return;
            }
            if (MessageBox.Show(this, Tr("Write this firmware to the Teensy?") + "\n\n" + fw + "\n\n" + Tr("Do not unplug it or switch it off while writing."),
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            Busy(true);
            work = new CancellationTokenSource();
            try
            {
                IntelHex hex = IntelHex.Load(fw.LocalPath);
                if (!TeensyLoader.BootloaderPresent() && cboCom.SelectedItem != null)
                    TeensyLoader.RebootToBootloader(cboCom.SelectedItem.ToString());
                await Task.Run(() => TeensyLoader.Program(hex, (p, msg) => BeginInvoke((Action)(() =>
                {
                    progress.Maximum = 1000;
                    progress.Value = (int)(Math.Max(0, Math.Min(1, p)) * 1000);
                    lblFirmware.Text = msg.StartsWith("Writing ", StringComparison.Ordinal) ? Tr("Writing") + msg.Substring(7) : Tr(msg);
                })), work.Token));
                AddLog(Tr("Firmware written") + ": " + fw);
                Log.EventWriter("Configurator: firmware written " + fw);
            }
            catch (Exception ex)
            {
                AddLog(Tr("Writing failed") + ": " + ex.Message);
            }
            finally
            {
                Busy(false);
            }
        }

        // over Ethernet, to a module whose firmware has the network update (one of the modified ones)
        private async Task NetFlash()
        {
            if (!(lstFirmware.SelectedItem is FirmwareEntry fw)) return;
            if (!fw.Available)
            {
                AddLog(Tr("Not in the cache yet: download the list first"));
                return;
            }
            Busy(true);
            work = new CancellationTokenSource();
            try
            {
                string running = await Task.Run(() => netUpdater.Query());
                if (running == null)
                {
                    AddLog(Tr("No module answered over the network: the first time the firmware goes on over USB"));
                    return;
                }
                AddLog(Tr("On the module now") + " (" + netUpdater.Module + "): " + running);
                if (!fw.Modified)
                    AddLog(Tr("Note: this firmware has no network update - later ones will need USB again"));
                // another board's firmware drives the pins differently (the steering motor on another pin): asked apart
                if (!NetFirmwareUpdater.SameBoard(running, fw.Name))
                {
                    AddLog(Tr("This firmware seems to be for another board") + ": " + fw.Name);
                    if (MessageBox.Show(this, Tr("This firmware seems to be for another board than the one on the module.") + "\n\n"
                        + Tr("On the module now") + ": " + running + "\n" + Tr("Selected") + ": " + fw.Name + "\n\n"
                        + Tr("A firmware for another board uses other pins: the steering motor may not work or may move unexpectedly. Write it anyway?"),
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                }
                if (MessageBox.Show(this, Tr("Write this firmware to the module over the network?") + "\n\n" + fw + "\n\n"
                    + Tr("The tractor must stand, autosteer off. While it installs (about 30 seconds at the end) do not switch the module off: then it can only be programmed over USB again."),
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                IntelHex hex = IntelHex.Load(fw.LocalPath);
                await Task.Run(() => netUpdater.Update(hex, (p, msg) => BeginInvoke((Action)(() =>
                {
                    progress.Maximum = 1000;
                    progress.Value = (int)(Math.Max(0, Math.Min(1, p)) * 1000);
                    lblFirmware.Text = msg.StartsWith("Sending ", StringComparison.Ordinal) ? Tr("Sending") + msg.Substring(7) : Tr(msg);
                })), work.Token));
                AddLog(Tr("Sent and checked; the module installs it and restarts") + ": " + fw);
                Log.EventWriter("Configurator: firmware sent over the network " + fw);
                // back with the new one?
                string after = await Task.Run(() =>
                {
                    Thread.Sleep(15000);
                    for (int i = 0; i < 30; i++)
                    {
                        string n = netUpdater.Query(1000);
                        if (n != null) return n;
                    }
                    return null;
                });
                AddLog(after != null ? Tr("The module is back") + ": " + after
                    : Tr("The module has not answered yet - if it stays silent, program it over USB"));
            }
            catch (Exception ex)
            {
                AddLog(Tr("Writing failed") + ": " + ex.Message);
            }
            finally
            {
                Busy(false);
            }
        }

        // ---- receiver ----

        private int Port => receiverPort;

        private sealed class Preset
        {
            public string Name;
            public ReceiverKind For;
            public Func<List<byte[]>> Build;
            public override string ToString() => Name;
        }

        private void FillPresets()
        {
            cboPreset.Items.Clear();
            cboPreset.Items.Add(new Preset { Name = Tr("UM982: TDCP raw data on"), For = ReceiverKind.Um982, Build = () => Um982(ReceiverConfig.Um982TdcpOn) });
            cboPreset.Items.Add(new Preset { Name = Tr("UM982: TDCP raw data off"), For = ReceiverKind.Um982, Build = () => Um982(ReceiverConfig.Um982TdcpOff) });
            cboPreset.Items.Add(new Preset
            {
                Name = Tr("u-blox F9P: TDCP raw data on"),
                For = ReceiverKind.UbloxF9P,
                Build = () => ReceiverConfig.ValSetFrames(new[]
                {
                    new KeyValuePair<uint, ulong>(ReceiverConfig.KeyRawxUart1, 1),
                    new KeyValuePair<uint, ulong>(ReceiverConfig.KeySfrbxUart1, 1)
                }, ReceiverConfig.LayerRam | ReceiverConfig.LayerBbr | ReceiverConfig.LayerFlash)
            });
            cboPreset.Items.Add(new Preset
            {
                Name = Tr("u-blox F9P: TDCP raw data off"),
                For = ReceiverKind.UbloxF9P,
                Build = () => ReceiverConfig.ValSetFrames(new[]
                {
                    new KeyValuePair<uint, ulong>(ReceiverConfig.KeyRawxUart1, 0),
                    new KeyValuePair<uint, ulong>(ReceiverConfig.KeySfrbxUart1, 0)
                }, ReceiverConfig.LayerRam | ReceiverConfig.LayerBbr | ReceiverConfig.LayerFlash)
            });
            // Config-O-Matic's own receiver files, from the cache
            foreach (string name in FirmwareCatalog.ReceiverFiles())
            {
                string file = name;
                bool unicore = file.StartsWith("ConfigUM982", StringComparison.OrdinalIgnoreCase);
                cboPreset.Items.Add(new Preset
                {
                    Name = "Config-O-Matic: " + file,
                    For = unicore ? ReceiverKind.Um982 : ReceiverKind.UbloxF9P,
                    Build = () => unicore
                        ? Um982(ReceiverConfig.ParseUnicoreCommands(FirmwareCatalog.ReceiverFile(file)).ToArray())
                        : ReceiverConfig.ValSetFrames(KeepBaud(ReceiverConfig.ParseUcenterDump(FirmwareCatalog.ReceiverFile(file))),
                            ReceiverConfig.LayerRam | ReceiverConfig.LayerBbr | ReceiverConfig.LayerFlash)
                });
            }
            if (cboPreset.Items.Count > 0) cboPreset.SelectedIndex = 0;
        }

        // the port to the module keeps its baud rate: the firmware reads it at a fixed one
        private List<byte[]> Um982(IEnumerable<string> commands)
        {
            var list = new List<byte[]>();
            foreach (string c in commands)
            {
                if (c.StartsWith("CONFIG COM1 ", StringComparison.OrdinalIgnoreCase) || c.Equals(ReceiverConfig.Um982Save, StringComparison.OrdinalIgnoreCase))
                {
                    AddLog(Tr("Skipped") + ": " + c);
                    continue;
                }
                list.Add(ReceiverConfig.UnicoreCommand(c));
            }
            return list;
        }

        private IEnumerable<KeyValuePair<uint, ulong>> KeepBaud(List<KeyValuePair<uint, ulong>> items)
        {
            const uint uart1Baud = 0x40520001;
            foreach (KeyValuePair<uint, ulong> kv in items)
            {
                if (kv.Key == uart1Baud)
                {
                    AddLog(Tr("Skipped") + ": CFG-UART1-BAUDRATE " + kv.Value);
                    continue;
                }
                yield return kv;
            }
        }

        private void OnReply(int port, byte[] data)
        {
            AddLog("< GPS" + port + ": " + Describe(data));
            lock (answerLock)
            {
                if (awaited != null && awaited(port, data))
                {
                    awaited = null;
                    answered.Set();
                }
            }
        }

        private static string Describe(byte[] d)
        {
            if (ReceiverConfig.TryParseAck(d, out bool ack, out byte cls, out byte id))
                return (ack ? "ACK " : "NAK ") + cls.ToString("X2") + " " + id.ToString("X2");
            if (ReceiverConfig.TryParseMonVer(d, out string ver)) return "MON-VER " + ver;
            if (d.Length > 0 && (d[0] == '$' || d[0] == '#')) return Encoding.ASCII.GetString(d).Trim();
            return BitConverter.ToString(d.Take(32).ToArray()).Replace('-', ' ') + (d.Length > 32 ? " ..." : "");
        }

        // sends one message and waits for the answer that matches
        private bool SendAndWait(byte[] msg, Func<int, byte[], bool> match, int timeoutMs)
        {
            lock (answerLock) awaited = match;
            answered.Reset();
            int port = Port;   // a plain field: no Invoke from this worker thread (the window may be closing)
            if (!mf.SendToReceiver(port, msg))
            {
                AddLog(Tr("No way to the receiver: no module network and no GPS serial port"));
                return false;
            }
            bool got = answered.WaitOne(timeoutMs);
            lock (answerLock) awaited = null;
            return got;
        }

        private async Task Detect()
        {
            Busy(true);
            try
            {
                kind = await Task.Run(() =>
                {
                    ReceiverKind found = ReceiverKind.Unknown;
                    Func<int, byte[], bool> any = (p, d) =>
                    {
                        ReceiverKind k = ReceiverConfig.KindFromAnswer(d);
                        if (k == ReceiverKind.Unknown) return false;
                        found = k;
                        return true;
                    };
                    if (SendAndWait(ReceiverConfig.UnicoreCommand(ReceiverConfig.Um982Version), any, 1500)) return found;
                    if (SendAndWait(ReceiverConfig.PollMonVer(), any, 1500)) return found;
                    return ReceiverKind.Unknown;
                });
                lblReceiver.Text = kind == ReceiverKind.Um982 ? "UM982" : kind == ReceiverKind.UbloxF9P ? "u-blox F9P"
                    : Tr("No answer: is the modified firmware on the Teensy?");
            }
            finally
            {
                Busy(false);
            }
        }

        private async Task SendPreset()
        {
            if (!(cboPreset.SelectedItem is Preset preset)) return;
            if (kind != ReceiverKind.Unknown && kind != preset.For)
            {
                AddLog(Tr("This setting is for another receiver"));
                return;
            }
            List<byte[]> msgs;
            try
            {
                msgs = preset.Build();
            }
            catch (Exception ex)
            {
                AddLog(Tr("The setting could not be read") + ": " + ex.Message);
                return;
            }
            Busy(true);
            try
            {
                int ok = 0, failed = 0;
                await Task.Run(() =>
                {
                    foreach (byte[] m in msgs)
                    {
                        if (SendOne(m)) ok++;
                        else failed++;
                    }
                });
                AddLog(preset.Name + ": " + ok + " " + Tr("taken") + (failed > 0 ? ", " + failed + " " + Tr("not confirmed") : ""));
                if (failed == 0) AddLog(Tr("Press Save in the receiver to keep it after switching off"));
            }
            finally
            {
                Busy(false);
            }
        }

        // a UBX frame waits for its ACK/NAK, a Unicore line for its "$command,...,response"
        private bool SendOne(byte[] m)
        {
            if (m.Length >= 8 && m[0] == 0xB5 && m[1] == 0x62)
            {
                byte cls = m[2], id = m[3];
                bool ack = false;
                bool got = SendAndWait(m, (p, d) =>
                {
                    if (!ReceiverConfig.TryParseAck(d, out bool a, out byte c, out byte i) || c != cls || i != id) return false;
                    ack = a;
                    return true;
                }, 1500);
                return got && ack;
            }
            string cmd = Encoding.ASCII.GetString(m).Trim();
            bool okAnswer = false;
            bool answeredLine = SendAndWait(m, (p, d) =>
            {
                if (!ReceiverConfig.TryParseUnicoreAnswer(Encoding.ASCII.GetString(d).Trim(), out string c, out bool ok)) return false;
                if (!c.Equals(cmd, StringComparison.OrdinalIgnoreCase)) return false;
                okAnswer = ok;
                return true;
            }, 1500);
            return answeredLine && okAnswer;
        }

        private async Task SaveReceiver()
        {
            Busy(true);
            try
            {
                // u-blox: the presets write RAM + BBR + flash already; nothing more to save
                bool ublox = kind == ReceiverKind.UbloxF9P;
                bool ok = ublox || await Task.Run(() => SendOne(ReceiverConfig.UnicoreCommand(ReceiverConfig.Um982Save)));
                AddLog(ok ? Tr("Saved in the receiver") : Tr("Saving not confirmed"));
            }
            finally
            {
                Busy(false);
            }
        }

        private async Task SendCommand()
        {
            string text = txtCommand.Text.Trim();
            if (text.Length == 0) return;
            byte[] msg;
            if (System.Text.RegularExpressions.Regex.IsMatch(text, "^(?:[0-9A-Fa-f]{2}\\s*)+$"))
                msg = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();
            else if (text.StartsWith("CONFIG COM1 ", StringComparison.OrdinalIgnoreCase))
            {
                AddLog(Tr("Not sent: the baud rate to the module must stay as it is"));
                return;
            }
            else msg = ReceiverConfig.UnicoreCommand(text);
            Busy(true);
            try
            {
                bool ok = await Task.Run(() => SendOne(msg));
                AddLog("> " + text + (ok ? "  OK" : "  " + Tr("no confirmation")));
            }
            finally
            {
                Busy(false);
            }
        }
    }
}
