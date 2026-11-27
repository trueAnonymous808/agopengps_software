using System;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;

namespace AgIO
{
    // The configurator's way to the GNSS receivers through the AiO module (its firmware with the GNSS bridge):
    // bytes for the position receiver go to the module's NTRIP port 2233 (as corrections do), bytes for the heading
    // receiver to port 2234; the receivers' answers come back in PGN 0xB0 [port][bytes]. A receiver on a serial port
    // of this computer gets the bytes there too.
    public partial class FormLoop
    {
        public const byte PgnGnssReply = 0xB0, PgnFirmwareUpdate = 0xB1;
        public const int PortGps1Commands = 2233, PortGps2Commands = 2234, PortFirmwareUpdate = 2235;

        /// <summary>An answer of a receiver: port (1 position, 2 heading) and its bytes. Raised on the UI thread.</summary>
        public event Action<int, byte[]> GnssReply;

        /// <summary>An answer of the module's firmware update (command letter, status, extra) and the module that sent
        /// it. Raised on the UI thread.</summary>
        public event Action<byte[], IPAddress> FirmwareUpdateReply;

        // who sent the datagram being handled now (set on the UI thread just before it is handled)
        private IPAddress udpFrom;

        private bool HandleGnssReply(byte[] data)
        {
            if (data.Length >= 8 && data[3] == PgnFirmwareUpdate)
            {
                int n = data[4];
                if (data.Length >= 5 + n)
                {
                    var update = new byte[n];
                    Array.Copy(data, 5, update, 0, n);
                    FirmwareUpdateReply?.Invoke(update, udpFrom);
                }
                return true;
            }
            if (data.Length < 7 || data[3] != PgnGnssReply) return false;
            int len = data[4];
            if (len < 1 || data.Length < 5 + len) return true;
            int port = data[5];
            var body = new byte[len - 1];
            Array.Copy(data, 6, body, 0, len - 1);
            GnssReply?.Invoke(port, body);
            return true;
        }

        /// <summary>Sends bytes to a receiver; false when there is no way to it.</summary>
        public bool SendToReceiver(int port, byte[] data)
        {
            bool sent = false;
            if (isUDPNetworkConnected)
            {
                var ep = new IPEndPoint(IPAddress.Parse(
                    Properties.Settings.Default.etIP_SubnetOne + "." +
                    Properties.Settings.Default.etIP_SubnetTwo + "." +
                    Properties.Settings.Default.etIP_SubnetThree + ".255"), port == 2 ? PortGps2Commands : PortGps1Commands);
                sent = SendQuiet(data, ep);
            }
            if (port == 1 && WriteGpsPortQuiet(data)) sent = true;
            return sent;
        }

        /// <summary>A datagram for the firmware update (port 2235) of one module, or of the whole subnet when null.</summary>
        public bool SendToFirmwareUpdate(byte[] data, IPAddress module)
        {
            if (!isUDPNetworkConnected) return false;
            IPAddress to = module ?? IPAddress.Parse(
                Properties.Settings.Default.etIP_SubnetOne + "." +
                Properties.Settings.Default.etIP_SubnetTwo + "." +
                Properties.Settings.Default.etIP_SubnetThree + ".255");
            return SendQuiet(data, new IPEndPoint(to, PortFirmwareUpdate));
        }

        // from the configurator's worker threads: no monitor log (that belongs to the UI thread)
        private bool SendQuiet(byte[] data, IPEndPoint ep)
        {
            Socket socket = UDPSocket;
            if (socket == null || data.Length == 0) return false;
            try
            {
                socket.BeginSendTo(data, 0, data.Length, SocketFlags.None, ep, new AsyncCallback(SendDataUDPAsync), null);
                return true;
            }
            catch (Exception)
            {
                return false;   // the socket is closing or the network is gone
            }
        }

        private void AddConfiguratorMenu()
        {
            var configurator = new ToolStripMenuItem
            {
                Text = AgLibrary.Localization.UiTranslator.TrText("Module configurator"),
                Image = AgIO.Configurator.ConfiguratorIcon.Draw(deviceManagerToolStripMenuItem.Image?.Width ?? 64),
                ImageScaling = deviceManagerToolStripMenuItem.ImageScaling,
                Font = deviceManagerToolStripMenuItem.Font,
                Size = deviceManagerToolStripMenuItem.Size
            };
            configurator.Click += (s, e) =>
            {
                foreach (Form f in Application.OpenForms)
                    if (f is FormConfigurator open)
                    {
                        open.Activate();
                        return;
                    }
                new FormConfigurator(this).Show(this);
            };
            toolStripDropDownButton1.DropDownItems.Add(configurator);
        }
    }
}
