using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CwcConfigurator.Services.Simulation.Process;

namespace CwcConfigurator.App.Simulation;

public partial class ScadaView
{
    /// <summary>
    /// An electrical object on a single-line-diagram screen: the IEC-like symbol with the incoming supply on top and
    /// the outgoing voltage at the bottom. Closed / live = red, open = green, tripped = orange.
    /// </summary>
    private sealed class SldSymbol : SheetItem
    {
        private const double Center = 30;
        private const double BusWidth = 260;
        private static readonly Brush TripBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
        private static readonly string[] Types = ["Busbar", "AuxSupply", "Mcb", "Contactor", "Disconnector", "EarthSwitch", "Motor"];

        private readonly Canvas _canvas = new();
        private readonly Canvas _body = new();
        private readonly TextBlock _name;
        private readonly TextBlock _state;
        private readonly Border _border;
        private readonly bool _bus;

        public static bool Supports(string type) => Types.Contains(type, StringComparer.OrdinalIgnoreCase);

        public SldSymbol(ObjectConfig config, ObjectTypeInfo info) : base(config, info)
        {
            _bus = info.Type is "Busbar" or "AuxSupply";
            _name = new TextBlock { Text = config.Name, FontWeight = FontWeights.SemiBold, FontSize = 12 };
            _state = new TextBlock { FontSize = 10.5, Foreground = Brushes.DimGray };
            _canvas.Width = _bus ? BusWidth : 150;
            _canvas.Height = _bus ? 44 : 92;
            _canvas.Children.Add(_body);
            var label = new StackPanel { Children = { _name, _state } };
            Canvas.SetLeft(label, _bus ? 0 : 52);
            Canvas.SetTop(label, _bus ? 0 : 28);
            _canvas.Children.Add(label);

            var active = info.Ports.Where(port => port.Power && info.IsActive(port.ShownWhen, config)).ToList();
            var inputs = active.Where(port => port.Direction == PortDirection.Input).ToList();
            for (var i = 0; i < inputs.Count; i++)
            {
                // Incoming supplies on top (a busbar with several supplies spreads them along the bar).
                var x = _bus ? BusWidth * (i + 1) / (inputs.Count + 1) : Center;
                AddPin(inputs[i], x, _bus ? 18 : 2);
            }

            foreach (var port in active.Where(port => port.Direction == PortDirection.Output))
            {
                AddPin(port, _bus ? BusWidth / 2 : Center, _bus ? 34 : 90);
            }

            _border = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(2),
                Cursor = Cursors.Hand,
                ToolTip = $"{info.Title} — {(string.IsNullOrWhiteSpace(config.Description) ? info.Description : config.Description)}",
                Child = _canvas
            };
            Draw(null);
        }

        public override FrameworkElement Element => _border;

        public override void SetSelected(bool selected) => _border.BorderBrush = selected ? HighlightBrush : Brushes.Transparent;

        public override void Refresh(SimObject? instance) => Draw(instance);

        /// <summary>A busbar takes its feeders where they are: the link leaves the bar straight above the feeder.</summary>
        public override Point Anchor(string port, Point? towards)
        {
            var point = base.Anchor(port, towards);
            if (_bus && towards is { } other && Info.Port(port) is { Direction: PortDirection.Output })
            {
                var left = Canvas.GetLeft(Element) + 4;
                return new Point(Math.Clamp(other.X, left + 6, left + BusWidth - 6), point.Y);
            }

            return point;
        }

        private void AddPin(PortDef port, double x, double y)
        {
            var dot = new Border { Width = 10, Height = 10, Background = Brushes.White, BorderBrush = LiveBrush, BorderThickness = new Thickness(2) };
            Canvas.SetLeft(dot, x - 5);
            Canvas.SetTop(dot, y - 5);
            Panel.SetZIndex(dot, 5);
            _canvas.Children.Add(Pin(port, dot, $"{port.Name} — {port.Description}\nDrag to another pin to link; right-click to disconnect."));
        }

        private void Draw(SimObject? instance)
        {
            bool On(string port) => instance?.Value(port) >= 0.5;
            Brush Wire(string port) => instance is not null && On(port) ? LiveBrush : DeadBrush;
            _body.Children.Clear();

            switch (Info.Type)
            {
                case "Busbar":
                case "AuxSupply":
                {
                    var live = On("Live");
                    _body.Children.Add(Shape(new Rectangle { Width = BusWidth, Height = 8, Fill = instance is null ? DeadBrush : live ? LiveBrush : DeadBrush }, 0, 26));
                    _state.Text = "";
                    _name.Text = $"{Config.Name}   {(instance is null ? "" : live ? $"{Value(instance, Info.Type == "Busbar" ? "Voltage" : "U")} V" : "dead")}";
                    return;
                }

                case "Mcb":
                case "Contactor":
                {
                    var closed = On(Info.Type == "Mcb" ? "On" : "State");
                    var tripped = Info.Type == "Mcb" && On("Tripped");
                    _body.Children.Add(Segment(Center, 2, Center, 32, Wire("Supply")));
                    _body.Children.Add(Segment(Center, 60, Center, 90, Wire("Live")));
                    var box = new Rectangle
                    {
                        Width = 28,
                        Height = 28,
                        StrokeThickness = 2.5,
                        Stroke = tripped ? TripBrush : closed ? ClosedBrush : OpenBrush,
                        Fill = tripped ? TripBrush : closed ? ClosedBrush : Brushes.White
                    };
                    _body.Children.Add(Shape(box, Center - 14, 32));
                    if (Info.Type == "Contactor")
                    {
                        _body.Children.Add(Shape(new TextBlock { Text = "K", FontWeight = FontWeights.Bold, Foreground = closed ? Brushes.White : OpenBrush }, Center - 4, 37));
                    }

                    _state.Text = tripped ? "TRIPPED" : closed ? (Info.Type == "Mcb" ? "ON" : "CLOSED") : (Info.Type == "Mcb" ? "OFF" : "OPEN");
                    return;
                }

                case "Disconnector":
                {
                    var closed = On("Closed");
                    var open = On("Open") || instance is null;
                    _body.Children.Add(Segment(Center, 2, Center, 30, Wire("Supply")));
                    _body.Children.Add(Segment(Center, 62, Center, 90, Wire("Live")));
                    _body.Children.Add(Shape(new Ellipse { Width = 6, Height = 6, Fill = Brushes.Black }, Center - 3, 27));
                    _body.Children.Add(closed
                        ? Segment(Center, 30, Center, 62, ClosedBrush, 3)
                        : Segment(Center, 62, Center + 16, 34, open ? OpenBrush : TripBrush, 3));
                    _state.Text = closed ? "CLOSED" : open ? "OPEN" : "MOVING";
                    return;
                }

                case "EarthSwitch":
                {
                    var earthed = On("Earthed");
                    var free = On("NotEarthed") || instance is null;
                    _body.Children.Add(Segment(Center, 2, Center, 90, Wire("Supply")));
                    _body.Children.Add(Segment(Center, 46, Center + 20, 46, earthed ? ClosedBrush : DeadBrush));
                    _body.Children.Add(earthed
                        ? Segment(Center + 20, 46, Center + 20, 64, ClosedBrush, 3)
                        : Segment(Center + 20, 64, Center + 32, 50, free ? OpenBrush : TripBrush, 3));
                    for (var i = 0; i < 3; i++)
                    {
                        _body.Children.Add(Segment(Center + 20 - (10 - i * 3), 66 + i * 4, Center + 20 + (10 - i * 3), 66 + i * 4, Brushes.Black, 1.5));
                    }

                    _state.Text = earthed ? (On("Fault") ? "EARTHED – FAULT" : "EARTHED") : free ? "NOT EARTHED" : "MOVING";
                    return;
                }

                case "Motor":
                {
                    var running = On("Running");
                    _body.Children.Add(Segment(Center, 2, Center, 40, Wire("Supply")));
                    _body.Children.Add(Shape(new Ellipse
                    {
                        Width = 36,
                        Height = 36,
                        StrokeThickness = 2.5,
                        Stroke = running ? ClosedBrush : OpenBrush,
                        Fill = running ? new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2)) : Brushes.White
                    }, Center - 18, 40));
                    _body.Children.Add(Shape(new TextBlock { Text = "M", FontWeight = FontWeights.Bold, FontSize = 14 }, Center - 6, 48));
                    _state.Text = instance is null ? "" : running ? $"RUN  {Value(instance, "Current")} A" : $"STOP  {Value(instance, "Speed")} %";
                    return;
                }
            }
        }

        private static string Value(SimObject instance, string port) =>
            instance.Value(port).ToString(Math.Abs(instance.Value(port)) >= 100 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);

        private static UIElement Shape(UIElement element, double x, double y)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            return element;
        }

        private static Line Segment(double x1, double y1, double x2, double y2, Brush brush, double thickness = 2.5) =>
            new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    }
}
