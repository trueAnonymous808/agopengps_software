using AgLibrary.Firmware;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Threading;

namespace AgIO.Configurator
{
    /// <summary>
    /// Puts a firmware on a Teensy 4.1 over USB, as the Teensy Loader does: the Teensy's bootloader (HalfKay) is a
    /// USB HID device (16C0:0478) taking 1024-byte blocks with a 64-byte header holding the address; the first block
    /// erases the chip (it may take a while), empty blocks are skipped, and a last report with address FFFFFF boots it.
    /// </summary>
    public static class TeensyLoader
    {
        public const uint FlashBase = 0x60000000;
        public const int BlockSize = 1024;
        public const uint CodeSize = 8126464;   // Teensy 4.1
        private const int HeaderSize = 64;

        /// <summary>A Teensy running a sketch with USB serial reboots into its bootloader when its port is opened at 134 baud.</summary>
        public static bool RebootToBootloader(string comPort)
        {
            try
            {
                using (var p = new SerialPort(comPort, 134))
                {
                    p.Open();
                    Thread.Sleep(100);
                    p.Close();
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool BootloaderPresent()
        {
            string path = FindBootloader();
            return path != null;
        }

        /// <summary>Writes the image; progress 0..1. Throws with a readable message when it cannot.</summary>
        public static void Program(IntelHex hex, Action<double, string> progress, CancellationToken cancel, double waitForBootloaderS = 30)
        {
            List<byte[]> blocks = hex.Blocks(FlashBase, BlockSize, CodeSize);

            progress?.Invoke(0, "Waiting for the Teensy bootloader (press the button on the Teensy if it does not come)");
            string path = null;
            DateTime giveUp = DateTime.Now.AddSeconds(waitForBootloaderS);
            while ((path = FindBootloader()) == null)
            {
                if (cancel.IsCancellationRequested) throw new OperationCanceledException();
                if (DateTime.Now > giveUp) throw new IOException("No Teensy in bootloader mode found on USB");
                Thread.Sleep(250);
            }

            using (SafeFileHandle handle = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new IOException("The Teensy bootloader could not be opened (" + Marshal.GetLastWin32Error() + ")");

                var report = new byte[1 + HeaderSize + BlockSize];   // report id 0 first
                int written = 0, total = 0;
                foreach (byte[] b in blocks) if (b != null) total++;
                for (int i = 0; i < blocks.Count; i++)
                {
                    if (blocks[i] == null) continue;
                    if (cancel.IsCancellationRequested)
                    {
                        if (written == 0) throw new OperationCanceledException("Stopped: nothing was written, the Teensy keeps its firmware");
                        throw new IOException("Stopped halfway: the Teensy holds no complete firmware now - program it again");
                    }
                    uint addr = (uint)(i * BlockSize);
                    Array.Clear(report, 0, report.Length);
                    report[1] = (byte)(addr & 0xFF);
                    report[2] = (byte)((addr >> 8) & 0xFF);
                    report[3] = (byte)((addr >> 16) & 0xFF);
                    Array.Copy(blocks[i], 0, report, 1 + HeaderSize, BlockSize);
                    // the first block erases the whole flash first
                    Write(handle, report, written == 0 ? 45 : 2);
                    written++;
                    progress?.Invoke((double)written / total, "Writing " + written + " / " + total);
                }

                // boot the new firmware
                Array.Clear(report, 0, report.Length);
                report[1] = 0xFF;
                report[2] = 0xFF;
                report[3] = 0xFF;
                try { Write(handle, report, 2); }
                catch (IOException) { }   // it reboots at once and may not answer
            }
            progress?.Invoke(1, "Done: the Teensy starts the new firmware");
        }

        // one report, retried until the timeout (the bootloader holds off while it erases or writes the flash)
        private static void Write(SafeFileHandle handle, byte[] report, double timeoutS)
        {
            DateTime giveUp = DateTime.Now.AddSeconds(timeoutS);
            GCHandle pin = GCHandle.Alloc(report, GCHandleType.Pinned);
            try
            {
                using (var done = new ManualResetEvent(false))
                {
                    while (true)
                    {
                        var ov = new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() };
                        done.Reset();
                        bool ok = Native.WriteFile(handle, pin.AddrOfPinnedObject(), report.Length, IntPtr.Zero, ref ov);
                        if (!ok && Marshal.GetLastWin32Error() == Native.ERROR_IO_PENDING)
                        {
                            int waitMs = (int)Math.Max(50, (giveUp - DateTime.Now).TotalMilliseconds);
                            if (done.WaitOne(Math.Min(waitMs, 1000)))
                                ok = Native.GetOverlappedResult(handle, ref ov, out int n, false) && n == report.Length;
                            else
                            {
                                Native.CancelIo(handle);
                                Native.GetOverlappedResult(handle, ref ov, out _, true);   // let the cancel finish before the buffer moves on
                            }
                        }
                        if (ok) return;
                        if (DateTime.Now > giveUp) throw new IOException("The Teensy did not take a block (" + Marshal.GetLastWin32Error() + ")");
                        Thread.Sleep(10);
                    }
                }
            }
            finally
            {
                pin.Free();
            }
        }

        // the HID device path of a Teensy bootloader (vid 16C0, pid 0478)
        private static string FindBootloader()
        {
            Native.HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return null;
            try
            {
                var iface = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA)) };
                for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref iface); i++)
                {
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out int needed, IntPtr.Zero);
                    if (needed <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(needed);
                    try
                    {
                        // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 in a 64-bit process, 6 in a 32-bit one
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, needed, out _, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(detail + 4);
                        if (path != null && path.IndexOf("vid_16c0&pid_0478", StringComparison.OrdinalIgnoreCase) >= 0) return path;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(set);
            }
            return null;
        }

        private static class Native
        {
            public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
            public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
            public const int ERROR_IO_PENDING = 997;
            public const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;

            [StructLayout(LayoutKind.Sequential)]
            public struct SP_DEVICE_INTERFACE_DATA
            {
                public int cbSize;
                public Guid InterfaceClassGuid;
                public int Flags;
                public IntPtr Reserved;
            }

            [DllImport("hid.dll")]
            public static extern void HidD_GetHidGuid(out Guid hidGuid);

            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

            [DllImport("setupapi.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

            [DllImport("setupapi.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, int numberOfBytesToWrite, IntPtr numberOfBytesWritten, ref NativeOverlapped overlapped);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetOverlappedResult(SafeFileHandle file, ref NativeOverlapped overlapped, out int numberOfBytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CancelIo(SafeFileHandle file);
        }
    }
}
