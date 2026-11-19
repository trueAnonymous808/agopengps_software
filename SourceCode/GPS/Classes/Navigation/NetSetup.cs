using System.Net;

namespace AgOpenGPS.Navigation
{
    // The map, road and address servers only accept TLS 1.2 or newer.
    public static class NetSetup
    {
        private const SecurityProtocolType Tls13 = (SecurityProtocolType)12288;

        /// <summary>
        /// Leaves the system's own choice alone (on .NET 4.8 it already offers TLS 1.2 and 1.3; adding a protocol to it
        /// would narrow it to that protocol), and only adds TLS 1.2 / 1.3 to an older, explicit list of protocols.
        /// </summary>
        public static void EnsureModernTls()
        {
            SecurityProtocolType current = ServicePointManager.SecurityProtocol;
            if (current == SecurityProtocolType.SystemDefault) return;
            try
            {
                ServicePointManager.SecurityProtocol = current | SecurityProtocolType.Tls12 | Tls13;
            }
            catch (System.NotSupportedException)
            {
                ServicePointManager.SecurityProtocol = current | SecurityProtocolType.Tls12;
            }
        }
    }
}
