namespace GX100Plugin
{
    /// <summary>
    /// The one definition of what a valid GX100 configuration looks like. The
    /// plugin settings and every profile seed from these values and validate
    /// against these ranges, so the two can never drift apart and no profile can
    /// hold a value the device would reject.
    /// </summary>
    public static class GX100Defaults
    {
        public const int HSensitivity = 80;
        public const int SeqSensitivity = 80;

        // Flags reach the device as a single 0/1 byte.
        public const int ComboSwitch1 = 1;
        public const int ComboSwitch2 = 0;
        public const int SequentialMode = 0;
        public const int PullButton = 0;

        // Sensitivities occupy one byte of the config report.
        public const int MinSensitivity = 0;
        public const int MaxSensitivity = 255;

        public static bool IsSensitivity(int value) =>
            value >= MinSensitivity && value <= MaxSensitivity;

        public static bool IsFlag(int value) => value == 0 || value == 1;
    }
}
