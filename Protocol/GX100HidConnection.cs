using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace GX100Plugin.Protocol
{
    /// <summary>
    /// I/O for the GX-100 shifter.
    ///
    /// Under Wine the device is driven through /dev/hidrawN directly rather than
    /// through HidSharp. Wine's HID stack misreports this device: it advertises a
    /// 9-byte output report where the descriptor declares 64, so a config report
    /// cannot be written through HidSharp there at all. HidSharp is kept only as a
    /// write fallback for native Windows, which has no hidraw; button input
    /// requires hidraw.
    /// </summary>
    public class GX100HidConnection : IDisposable
    {
        /// <summary>
        /// One connection generation. Each reader thread owns the session it was
        /// started for and never touches <see cref="_session"/> directly, so a
        /// reader left over from a previous generation cannot close the handles or
        /// raise the disconnect event of the live one.
        /// </summary>
        private sealed class Session
        {
            public string? HidrawPath;          // path form CreateFile accepted, e.g. @"Z:\dev\hidraw6"
            public SafeFileHandle? ReadHandle;  // owned by the reader thread
            public HidStream? Stream;           // native-Windows write fallback
            public Thread? Reader;
            public volatile bool Stop;
        }

        private readonly object _sync = new object();
        private Session? _session;
        private volatile bool _disposed;

        public bool IsConnected
        {
            get { var s = _session; return s != null && !s.Stop; }
        }

        public event Action<ushort>? ButtonStateChanged;

        /// <summary>
        /// Raised for every input report with the six Hall-sensor readings. Unlike
        /// <see cref="ButtonStateChanged"/> this is not filtered — the readings are
        /// the point of it, and they change on every report.
        /// </summary>
        public event Action<int[]>? SensorsUpdated;
        public event Action? DeviceDisconnected;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(
            SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(
            SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        // --- Connect / disconnect ---

        public bool Connect()
        {
            lock (_sync)
            {
                if (_disposed) return false;

                var current = _session;
                if (current != null && !current.Stop) return true;

                var s = new Session();
                try
                {
                    string? unixPath = FindHidrawPath();
                    if (unixPath != null)
                    {
                        s.ReadHandle = TryOpenHidraw(unixPath, GENERIC_READ, out string? opened);
                        if (s.ReadHandle == null) return false;

                        s.HidrawPath = opened;
                        SimHub.Logging.Current.Info($"[GX100] Connected via hidraw: {opened}");

                        _session = s;
                        StartReader(s);
                        return true;
                    }

                    var device = FindDevice();
                    if (device == null) return false;

                    s.Stream = device.Open();
                    SimHub.Logging.Current.Warn(
                        "[GX100] No hidraw device — config writes via HidSharp, button input unavailable");

                    _session = s;
                    return true;
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Error($"[GX100] Connect failed: {ex.Message}");
                    CloseSession(s);
                    return false;
                }
            }
        }

        public void Disconnect()
        {
            Session? s;
            lock (_sync)
            {
                s = _session;
                _session = null;
            }
            if (s != null) CloseSession(s);
        }

        /// <summary>
        /// Retires a session. Never joins the reader: a read() blocked on a Linux
        /// character device does not reliably wake when its handle closes, so the
        /// thread may sit in ReadFile until the device sends its next report.
        /// Waiting for that would stall whoever called Disconnect — the UI thread
        /// on a toggle, or SimHub's shutdown path. The thread is a background
        /// thread and, once superseded, touches no live state, so it is left to
        /// drain and exit on its own.
        /// </summary>
        private static void CloseSession(Session s)
        {
            s.Stop = true;
            try { s.ReadHandle?.Dispose(); } catch { }
            try { s.Stream?.Close(); } catch { }
        }

        // --- Reading ---

        private void StartReader(Session s)
        {
            s.Reader = new Thread(() => ReadLoop(s))
            {
                IsBackground = true,
                Name = "GX100-HidrawReader",
            };
            s.Reader.Start();
        }

        private void ReadLoop(Session s)
        {
            var buffer = new byte[GX100Protocol.InputReportLength];
            int lastButtons = -1;

            SimHub.Logging.Current.Info("[GX100] hidraw reader started");

            while (!s.Stop)
            {
                bool ok;
                uint bytesRead;
                try
                {
                    ok = ReadFile(s.ReadHandle!, buffer, (uint)buffer.Length, out bytesRead, IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    if (!s.Stop)
                        SimHub.Logging.Current.Error($"[GX100] Read exception: {ex.Message}");
                    break;
                }

                if (s.Stop) break;

                if (!ok)
                {
                    SimHub.Logging.Current.Error(
                        $"[GX100] hidraw ReadFile failed: error {Marshal.GetLastWin32Error()}");
                    break;
                }

                // A runt report is not a disconnect — skip it and keep reading.
                if (bytesRead < 2) continue;

                if (bytesRead >= GX100Protocol.SensorOffset + GX100Protocol.SensorCount * 2)
                {
                    var sensors = new int[GX100Protocol.SensorCount];
                    for (int i = 0; i < sensors.Length; i++)
                    {
                        int o = GX100Protocol.SensorOffset + i * 2;
                        sensors[i] = (buffer[o] << 8) | buffer[o + 1];
                    }

                    try
                    {
                        SensorsUpdated?.Invoke(sensors);
                    }
                    catch (Exception ex)
                    {
                        SimHub.Logging.Current.Error($"[GX100] Sensor handler error: {ex.Message}");
                    }
                }

                int buttons = buffer[0] | (buffer[1] << 8);

                // The device streams continuously because the Hall-sensor bytes
                // jitter on every report; button consumers only care about changes,
                // so suppress reports that leave the button field as it was.
                if (buttons == lastButtons) continue;
                lastButtons = buttons;

                try
                {
                    ButtonStateChanged?.Invoke((ushort)buttons);
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Error($"[GX100] Button handler error: {ex.Message}");
                }
            }

            HandleReaderExit(s);
        }

        private void HandleReaderExit(Session s)
        {
            bool deliberate = s.Stop;

            bool wasCurrent;
            lock (_sync)
            {
                wasCurrent = ReferenceEquals(_session, s);
                if (wasCurrent) _session = null;
            }

            CloseSession(s);

            // Only a live session that stopped on its own is a real disconnect. A
            // superseded reader waking up late must stay silent, or it would drop
            // the connection that replaced it and start a reconnect loop.
            if (deliberate || !wasCurrent || _disposed) return;

            SimHub.Logging.Current.Info("[GX100] Device disconnected");
            try { DeviceDisconnected?.Invoke(); } catch { }
        }

        // --- Writing ---

        public bool SendCalibration(byte command)
        {
            var report = new byte[GX100Protocol.OutputReportLength];
            report[0] = GX100Protocol.ReportCalibration;
            report[1] = command;
            return WriteReport(report);
        }

        public bool SendConfiguration(
            bool comboSwitch1, bool comboSwitch2, bool sequentialMode,
            int hSensitivity, int seqSensitivity, bool pullButton)
        {
            var report = new byte[GX100Protocol.OutputReportLength];
            report[0] = GX100Protocol.ReportConfig;
            report[GX100Protocol.ConfigComboSwitch1] = comboSwitch1 ? (byte)0x01 : (byte)0x00;
            report[GX100Protocol.ConfigComboSwitch2] = comboSwitch2 ? (byte)0x01 : (byte)0x00;
            report[GX100Protocol.ConfigSeqMode] = sequentialMode ? (byte)0x01 : (byte)0x00;
            report[GX100Protocol.ConfigHSensitivity] = (byte)(hSensitivity & 0xFF);
            report[GX100Protocol.ConfigSeqSensitivity] = (byte)(seqSensitivity & 0xFF);
            report[GX100Protocol.ConfigPullButton] = pullButton ? (byte)0x01 : (byte)0x00;
            return WriteReport(report);
        }

        private bool WriteReport(byte[] report)
        {
            var s = _session;
            if (s == null || s.Stop) return false;

            if (s.HidrawPath != null)
                return WriteHidraw(s.HidrawPath, report);

            var stream = s.Stream;
            if (stream == null) return false;
            try
            {
                stream.Write(report);
                return true;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[GX100] HidSharp write failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Opens a handle per report, mirroring the known-good Linux tool. This
        /// keeps writes off the read handle, where a synchronous write would
        /// otherwise contend with a read that blocks until the device's next
        /// report.
        /// </summary>
        private static bool WriteHidraw(string path, byte[] report)
        {
            SafeFileHandle? h = null;
            try
            {
                h = CreateFile(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid)
                {
                    SimHub.Logging.Current.Error(
                        $"[GX100] Open for write failed ({path}): error {Marshal.GetLastWin32Error()}");
                    return false;
                }

                if (!WriteFile(h, report, (uint)report.Length, out uint written, IntPtr.Zero))
                {
                    SimHub.Logging.Current.Error(
                        $"[GX100] WriteFile failed: error {Marshal.GetLastWin32Error()}");
                    return false;
                }

                if (written != report.Length)
                {
                    SimHub.Logging.Current.Error($"[GX100] Short write: {written}/{report.Length} bytes");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[GX100] Write exception: {ex.Message}");
                return false;
            }
            finally
            {
                h?.Dispose();
            }
        }

        // --- Device discovery ---

        private static SafeFileHandle? TryOpenHidraw(string unixPath, uint access, out string? openedPath)
        {
            // Wine maps the unix root onto Z:; fall back to the bare unix path in
            // case the plugin is running somewhere that resolves it directly.
            string winePath = @"Z:" + unixPath.Replace('/', '\\');

            foreach (string candidate in new[] { winePath, unixPath })
            {
                SafeFileHandle handle;
                try
                {
                    handle = CreateFile(candidate, access, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                        IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn($"[GX100] CreateFile({candidate}) threw: {ex.Message}");
                    continue;
                }

                if (!handle.IsInvalid)
                {
                    openedPath = candidate;
                    return handle;
                }

                SimHub.Logging.Current.Warn(
                    $"[GX100] CreateFile({candidate}) failed: error {Marshal.GetLastWin32Error()}");
                handle.Dispose();
            }

            openedPath = null;
            return null;
        }

        /// <summary>Native-Windows fallback only; picks the interface with the largest output report.</summary>
        private static HidDevice? FindDevice()
        {
            HidDevice? best = null;
            int bestOutputLen = 0;

            foreach (var device in DeviceList.Local.GetHidDevices())
            {
                if (device.VendorID != GX100Protocol.VendorId ||
                    device.ProductID != GX100Protocol.ProductId)
                    continue;

                int outLen = device.GetMaxOutputReportLength();
                if (outLen > bestOutputLen)
                {
                    best = device;
                    bestOutputLen = outLen;
                }
            }

            return best;
        }

        private static string? FindHidrawPath()
        {
            const string sysClass = "/sys/class/hidraw";
            if (Directory.Exists(sysClass))
                return FindHidrawInDir(sysClass);

            const string wineSysClass = @"Z:\sys\class\hidraw";
            if (Directory.Exists(wineSysClass))
                return FindHidrawInDir(wineSysClass);

            return null;
        }

        private static string? FindHidrawInDir(string sysClass)
        {
            // HID uevents spell the ids as 8 hex digits: HID_ID=0003:000004B0:00005750
            string vidPid = $"{GX100Protocol.VendorId:X8}:{GX100Protocol.ProductId:X8}";

            try
            {
                foreach (var entry in Directory.GetDirectories(sysClass))
                {
                    string ueventPath = Path.Combine(entry, "device", "uevent");
                    if (!File.Exists(ueventPath)) continue;

                    if (File.ReadAllText(ueventPath).Contains(vidPid))
                        return $"/dev/{Path.GetFileName(entry)}";
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[GX100] Error scanning hidraw: {ex.Message}");
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }
    }
}
