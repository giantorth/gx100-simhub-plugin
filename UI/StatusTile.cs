using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GX100Plugin.Protocol;

namespace GX100Plugin
{
    /// <summary>
    /// One input on the status panel — a gate slot, sequential direction or extra
    /// button. Lights up while the input is active and, for inputs with a Hall
    /// sensor behind them, shows the live reading as a number and a bar.
    /// </summary>
    internal sealed class StatusTile
    {
        private static readonly Brush ActiveAccent = Freeze(new SolidColorBrush(Color.FromRgb(0x3C, 0xB3, 0x71)));
        private static readonly Brush ActiveFill = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x2D, 0x1A)));
        private static readonly Brush IdleBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)));
        private static readonly Brush IdleFill = Freeze(new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x00, 0x00)));
        private static readonly Brush IdleText = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
        private static readonly Brush BarTrack = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));

        public Border Root { get; }

        private readonly TextBlock _label;
        private readonly TextBlock? _value;
        private readonly ProgressBar? _bar;
        private bool? _active;
        private int? _sensor = -1;

        public StatusTile(string label, bool showSensor, double width, double height)
        {
            _label = new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.Bold,
                FontSize = showSensor ? 20 : 13,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(_label);

            if (showSensor)
            {
                _value = new TextBlock
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 3),
                };
                _bar = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = GX100Protocol.SensorMax,
                    Height = 4,
                    MinHeight = 4,
                    Background = BarTrack,
                    BorderThickness = new Thickness(0),
                };
                content.Children.Add(_value);
                content.Children.Add(_bar);
            }

            Root = new Border
            {
                Width = width,
                Height = height,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Child = content,
            };

            SetActive(false);
            SetSensor(null);
        }

        /// <summary>
        /// Shows or hides the reading and bar. The tile keeps its size either way,
        /// with the label re-centred, so the panel does not jump around.
        /// </summary>
        public void SetSensorVisible(bool visible)
        {
            if (_value == null || _bar == null) return;

            var v = visible ? Visibility.Visible : Visibility.Collapsed;
            _value.Visibility = v;
            _bar.Visibility = v;
        }

        public void SetLabel(string text)
        {
            if (_label.Text != text) _label.Text = text;
        }

        public void SetActive(bool active)
        {
            if (_active == active) return;
            _active = active;

            Root.BorderBrush = active ? ActiveAccent : IdleBorder;
            Root.Background = active ? ActiveFill : IdleFill;
            _label.Foreground = active ? ActiveAccent : IdleText;
            if (_value != null) _value.Foreground = active ? ActiveAccent : IdleText;
            if (_bar != null) _bar.Foreground = active ? ActiveAccent : IdleText;
        }

        /// <summary>Shows a raw sensor reading, or a dash when there is none to show.</summary>
        public void SetSensor(int? reading)
        {
            if (_value == null || _bar == null || _sensor == reading) return;
            _sensor = reading;

            _value.Text = reading?.ToString() ?? "—";
            _bar.Value = reading ?? 0;
        }

        private static Brush Freeze(Brush b)
        {
            b.Freeze();
            return b;
        }
    }
}
