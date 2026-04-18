using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace GX100Plugin.Protocol
{
    public class GX100HidConnection : IDisposable
    {
        private HidDevice? _device;
        private HidStream? _stream;
        private volatile bool _disposed;

        private string? _hidrawPath;
        private SafeFileHandle? _hidrawHandle;
        private Thread? _readThread;
        private volatile bool _stopReading;

        public bool IsConnected => _stream != null;

        public event Action<ushort>? ButtonStateChanged;
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        public GX100HidConnection()
        {
            DeviceList.Local.Changed += OnDeviceListChanged;
        }

        public bool Connect()
        {
            if (_stream != null) return true;

            try
            {
                _device = FindDevice();
                if (_device == null) return false;

                int maxIn = _device.GetMaxInputReportLength();
                int maxOut = _device.GetMaxOutputReportLength();
                _stream = _device.Open();
                _stream.ReadTimeout = Timeout.Infinite;
                SimHub.Logging.Current.Info(
                    $"[GX100] HidSharp connected (maxOut={maxOut} maxIn={maxIn})");

                OpenHidraw();
                StartDirectReader();
                return true;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[GX100] Connect error: {ex.Message}");
                CleanupStreams();
                return false;
            }
        }

        private void OpenHidraw()
        {
            _hidrawPath = FindHidrawPath();
            if (_hidrawPath == null)
            {
                SimHub.Logging.Current.Warn("[GX100] No hidraw found — output via HidSharp (may fail under Wine)");
                return;
            }

            string winePath = @"Z:" + _hidrawPath.Replace('/', '\\');
            _hidrawHandle = CreateFile(winePath, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (_hidrawHandle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                SimHub.Logging.Current.Error($"[GX100] CreateFile({winePath}) failed: error {err}");
                _hidrawHandle = null;

                try
                {
                    _hidrawHandle = CreateFile(_hidrawPath, GENERIC_READ | GENERIC_WRITE,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

                    if (_hidrawHandle.IsInvalid)
                    {
                        _hidrawHandle = null;
                        SimHub.Logging.Current.Error("[GX100] Direct open also failed");
                    }
                    else
                    {
                        SimHub.Logging.Current.Info($"[GX100] Hidraw opened directly: {_hidrawPath}");
                    }
                }
                catch (Exception ex2)
                {
                    SimHub.Logging.Current.Error($"[GX100] Direct open exception: {ex2.Message}");
                    _hidrawHandle = null;
                }
            }
            else
            {
                SimHub.Logging.Current.Info($"[GX100] Hidraw opened via Wine Z: drive: {winePath}");
            }
        }

        private void StartDirectReader()
        {
            if (_hidrawHandle == null || _hidrawHandle.IsInvalid)
            {
                SimHub.Logging.Current.Warn("[GX100] No hidraw handle — cannot start direct reader");
                return;
            }

            _stopReading = false;
            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = "GX100-HidrawReader"
            };
            _readThread.Start();
        }

        private void ReadLoop()
        {
            // hidraw input: 16 buttons = 2 bytes + 14 padding = 16 bytes total, no report ID
            var buffer = new byte[64];
            SimHub.Logging.Current.Info("[GX100] Direct hidraw reader started");

            while (!_stopReading)
            {
                try
                {
                    var handle = _hidrawHandle;
                    if (handle == null || handle.IsInvalid) break;

                    bool ok = ReadFile(handle, buffer, (uint)buffer.Length, out uint bytesRead, IntPtr.Zero);
                    if (!ok || bytesRead < 2)
                    {
                        if (_stopReading) break;
                        int err = Marshal.GetLastWin32Error();
                        SimHub.Logging.Current.Error($"[GX100] Hidraw ReadFile failed: error {err}, read={bytesRead}");
                        break;
                    }

                    ushort buttons = (ushort)(buffer[0] | (buffer[1] << 8));
                    ButtonStateChanged?.Invoke(buttons);
                }
                catch (Exception ex)
                {
                    if (_stopReading) break;
                    SimHub.Logging.Current.Error($"[GX100] Read exception: {ex.Message}");
                    break;
                }
            }

            if (!_stopReading)
            {
                SimHub.Logging.Current.Info("[GX100] Hidraw reader stopped — device disconnected");
                HandleDisconnect();
            }
        }

        public void Disconnect()
        {
            StopReader();
            CleanupStreams();
        }

        private void StopReader()
        {
            _stopReading = true;
            try
            {
                var handle = _hidrawHandle;
                if (handle != null && !handle.IsInvalid)
                    CancelIoEx(handle, IntPtr.Zero);
            }
            catch { }
            _readThread?.Join(2000);
            _readThread = null;
        }

        private void CleanupStreams()
        {
            if (_hidrawHandle != null && !_hidrawHandle.IsInvalid)
                _hidrawHandle.Close();
            _hidrawHandle = null;
            try { _stream?.Close(); } catch { }
            _stream = null;
            _device = null;
        }

        public void SendCalibration(byte command)
        {
            var report = new byte[GX100Protocol.ReportLength];
            report[0] = GX100Protocol.ReportCalibration;
            report[1] = command;
            WriteReport(report);
        }

        public void SendConfiguration(
            bool comboSwitch1, bool comboSwitch2, bool sequentialMode,
            int hSensitivity, int seqSensitivity, bool pullButton)
        {
            var report = new byte[GX100Protocol.ReportLength];
            report[0] = GX100Protocol.ReportConfig;
            report[GX100Protocol.ConfigComboSwitch1] = comboSwitch1 ? (byte)0x01 : (byte)0x00;
            report[GX100Protocol.ConfigComboSwitch2] = comboSwitch2 ? (byte)0x01 : (byte)0x00;
            report[GX100Protocol.ConfigSeqMode] = sequentialMode ? (byte)0x01 : (byte)0x00;
            report[GX100Protocol.ConfigHSensitivity] = (byte)(hSensitivity & 0xFF);
            report[GX100Protocol.ConfigSeqSensitivity] = (byte)(seqSensitivity & 0xFF);
            report[GX100Protocol.ConfigPullButton] = pullButton ? (byte)0x01 : (byte)0x00;
            WriteReport(report);
        }

        private void WriteReport(byte[] report)
        {
            if (_hidrawHandle != null && !_hidrawHandle.IsInvalid)
            {
                try
                {
                    bool ok = WriteFile(_hidrawHandle, report, (uint)report.Length, out uint written, IntPtr.Zero);
                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        SimHub.Logging.Current.Error($"[GX100] Hidraw WriteFile failed: error {err}");
                    }
                    return;
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Error($"[GX100] Hidraw write exception: {ex.Message}");
                }
            }

            var stream = _stream;
            if (stream == null) return;
            try
            {
                stream.Write(report);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[GX100] HidSharp write error: {ex.Message}");
            }
        }

        private void HandleDisconnect()
        {
            CleanupStreams();
            SimHub.Logging.Current.Info("[GX100] Device disconnected");
            DeviceDisconnected?.Invoke();
        }

        private void OnDeviceListChanged(object? sender, DeviceListChangedEventArgs e)
        {
            if (_disposed) return;
            if (_stream == null && FindDevice() != null)
                SimHub.Logging.Current.Info("[GX100] Device detected via hotplug");
        }

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
                int inLen = device.GetMaxInputReportLength();
                SimHub.Logging.Current.Info(
                    $"[GX100] Found HID interface: maxOut={outLen} maxIn={inLen} path={device.DevicePath}");

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
            if (!Directory.Exists(sysClass))
            {
                string wineSysClass = @"Z:\sys\class\hidraw";
                if (Directory.Exists(wineSysClass))
                    return FindHidrawInDir(wineSysClass);
                return null;
            }
            return FindHidrawInDir(sysClass);
        }

        private static string? FindHidrawInDir(string sysClass)
        {
            string vidPid = $"{GX100Protocol.VendorId:X8}:{GX100Protocol.ProductId:X8}";

            try
            {
                foreach (var entry in Directory.GetDirectories(sysClass))
                {
                    string name = Path.GetFileName(entry);
                    string ueventPath = Path.Combine(entry, "device", "uevent");
                    if (!File.Exists(ueventPath)) continue;

                    string uevent = File.ReadAllText(ueventPath);
                    if (uevent.Contains(vidPid))
                    {
                        SimHub.Logging.Current.Info($"[GX100] Found hidraw match: {name} (uevent contains {vidPid})");
                        return $"/dev/{name}";
                    }
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
            DeviceList.Local.Changed -= OnDeviceListChanged;
            Disconnect();
        }
    }
}
