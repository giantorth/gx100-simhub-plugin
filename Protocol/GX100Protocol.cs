namespace GX100Plugin.Protocol
{
    public static class GX100Protocol
    {
        public const int VendorId = 0x04B0;   // Nidec Copal Electronics
        public const int ProductId = 0x5750;
        public const int ReportLength = 64;

        // Report type bytes (first byte of output report)
        public const byte ReportCalibration = 0x01;
        public const byte ReportConfig = 0x02;

        // Calibration sub-commands
        public const byte CalHPatternStart = 0x01;
        public const byte CalHPatternFinish = 0x02;
        public const byte CalSequentialStart = 0x03;
        public const byte CalSequentialFinish = 0x04;

        // Config report field offsets (0-based within the 64-byte report)
        public const int ConfigComboSwitch1 = 1;
        public const int ConfigComboSwitch2 = 2;
        public const int ConfigSeqMode = 3;
        public const int ConfigHSensitivity = 4;
        // byte 5 = 0x00 padding
        public const int ConfigSeqSensitivity = 6;
        // byte 7 = 0x00 padding
        public const int ConfigPullButton = 8;

        // Input report button bit positions (2-byte button field)
        // Buttons 1-8: gears, 9-10: seq up/down, 11: reverse, 12-16: extra
        public const int ButtonGear1 = 0;
        public const int ButtonGear2 = 1;
        public const int ButtonGear3 = 2;
        public const int ButtonGear4 = 3;
        public const int ButtonGear5 = 4;
        public const int ButtonGear6 = 5;
        public const int ButtonGear7 = 6;
        public const int ButtonGear8 = 7;
        public const int ButtonSeqUp = 8;
        public const int ButtonSeqDown = 9;
        public const int ButtonReverse = 10;
        public const int ButtonExtra1 = 11;
        public const int ButtonExtra2 = 12;
        public const int ButtonExtra3 = 13;
        public const int ButtonExtra4 = 14;
        public const int ButtonExtra5 = 15;
    }
}
