using System.Threading;

namespace GX100Plugin.Telemetry
{
    public class GX100Data
    {
        // Connection
        public volatile bool IsConnected;

        // Configuration state
        public volatile int HSensitivity = 80;
        public volatile int SeqSensitivity = 80;
        public volatile bool ComboSwitch1 = true;
        public volatile bool ComboSwitch2;
        public volatile bool SequentialMode;
        public volatile bool PullButton;

        // Input state
        public volatile int CurrentGear;  // -1=R, 0=N, 1-8
        public volatile bool SeqUp;
        public volatile bool SeqDown;
        public volatile bool Button1;
        public volatile bool Button2;
        public volatile bool Button3;
        public volatile bool Button4;
        public volatile bool Button5;
        public volatile ushort RawButtons;

        // Hall-sensor readings, index 0-5 for sensors 1-6. Written by the HID
        // reader thread, read by the UI and SimHub properties.
        private readonly int[] _sensors = new int[Protocol.GX100Protocol.SensorCount];

        public int GetSensor(int index) => Volatile.Read(ref _sensors[index]);

        public void UpdateSensors(int[] values)
        {
            int n = System.Math.Min(values.Length, _sensors.Length);
            for (int i = 0; i < n; i++)
                Volatile.Write(ref _sensors[i], values[i]);
        }

        public bool IsPressed(int bit) => (RawButtons & (1 << bit)) != 0;

        // Calibration
        public volatile bool IsHCalibrating;
        public volatile bool IsSeqCalibrating;

        public void UpdateFromButtons(ushort raw)
        {
            RawButtons = raw;

            bool gear1 = (raw & (1 << Protocol.GX100Protocol.ButtonGear1)) != 0;
            bool gear2 = (raw & (1 << Protocol.GX100Protocol.ButtonGear2)) != 0;
            bool gear3 = (raw & (1 << Protocol.GX100Protocol.ButtonGear3)) != 0;
            bool gear4 = (raw & (1 << Protocol.GX100Protocol.ButtonGear4)) != 0;
            bool gear5 = (raw & (1 << Protocol.GX100Protocol.ButtonGear5)) != 0;
            bool gear6 = (raw & (1 << Protocol.GX100Protocol.ButtonGear6)) != 0;
            bool gear7 = (raw & (1 << Protocol.GX100Protocol.ButtonGear7)) != 0;
            bool gear8 = (raw & (1 << Protocol.GX100Protocol.ButtonGear8)) != 0;
            bool reverse = (raw & (1 << Protocol.GX100Protocol.ButtonReverse)) != 0;

            SeqUp = (raw & (1 << Protocol.GX100Protocol.ButtonSeqUp)) != 0;
            SeqDown = (raw & (1 << Protocol.GX100Protocol.ButtonSeqDown)) != 0;
            Button1 = (raw & (1 << Protocol.GX100Protocol.ButtonExtra1)) != 0;
            Button2 = (raw & (1 << Protocol.GX100Protocol.ButtonExtra2)) != 0;
            Button3 = (raw & (1 << Protocol.GX100Protocol.ButtonExtra3)) != 0;
            Button4 = (raw & (1 << Protocol.GX100Protocol.ButtonExtra4)) != 0;
            Button5 = (raw & (1 << Protocol.GX100Protocol.ButtonExtra5)) != 0;

            if (reverse) CurrentGear = -1;
            else if (gear1) CurrentGear = 1;
            else if (gear2) CurrentGear = 2;
            else if (gear3) CurrentGear = 3;
            else if (gear4) CurrentGear = 4;
            else if (gear5) CurrentGear = 5;
            else if (gear6) CurrentGear = 6;
            else if (gear7) CurrentGear = 7;
            else if (gear8) CurrentGear = 8;
            else CurrentGear = 0;
        }

        public string GearName => CurrentGear switch
        {
            -1 => "R",
            0 => "N",
            _ => CurrentGear.ToString()
        };
    }
}
