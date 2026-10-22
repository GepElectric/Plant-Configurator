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
        private const double MinBusWidth = 260;
        private static readonly Brush TripBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
        private static readonly string[] Types = ["Busbar", "AuxSupply", "Mcb", "Contactor", "Disconnector", "EarthSwitch", "Motor"];

        private readonly Canvas _canvas = new();
        private readonly Canvas _body = new();
        private readonly TextBlock _name;
        private readonly TextBlock _state;
        private readonly Border _border;
        private readonly bool _bus;
        private readonly double _busWidth;

        public static bool Supports(string type) => Types.Contains(type, StringComparer.OrdinalIgnoreCase);

        public SldSymbol(ObjectConfig config, ObjectTypeInfo info) : base(config, info)
        {
            _bus = info.Type is "Busbar" or "AuxSupply";
            var feeders = info.Ports.Count(port => port.Power && port.Direction == PortDirection.Output && info.IsActive(port.ShownWhen, config));
            _busWidth = Math.Max(MinBusWidth, feeders * 90);

            _name = new TextBlock { Text = config.Name, FontWeight = FontWeights.SemiBold, FontSize = 12 };
            _state = new TextBlock { FontSize = 10.5, Foreground = Brushes.DimGray };
            _canvas.Width = _bus ? _busWidth : 150;
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
                var x = _bus ? _busWidth * (i + 1) / (inputs.Count + 1) : Center;
                AddPin(inputs[i], x, _bus ? 18 : 2);
            }

            // Outgoing feeders spread along the bar.
            var outputs = active.Where(port => port.Direction == PortDirection.Output).ToList();
            for (var i = 0; i < outputs.Count; i++)
            {
                AddPin(outputs[i], _bus ? _busWidth * (i + 1) / (outputs.Count + 1) : Center, _bus ? 34 : 90);
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
            // A single output (AuxSupply.Live, or an older link to Busbar.Live) leaves the bar straight above the feeder.
            var pinned = PinElements.ContainsKey(port) && PinElements.Count(pair => Info.Port(pair.Key)?.Direction == PortDirection.Output) > 1;
            if (_bus && !pinned && towards is { } other && Info.Port(port) is { Direction: PortDirection.Output })
            {
                var left = Canvas.GetLeft(Element) + 4;
                var top = Canvas.GetTop(Element) + 4;
                return new Point(Math.Clamp(other.X, left + 6, left + _busWidth - 6), top + 34);
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
                    _body.Children.Add(Shape(new Rectangle { Width = _busWidth, Height = 8, Fill = instance is null ? DeadBrush : live ? LiveBrush : DeadBrush }, 0, 26));
                    _state.Text = "";
                    _name.Text = $"{Config.Name}   {(instance is null ? "" : !live ? "dead" : Info.Type == "AuxSupply" ? $"{Value(instance, "U")} V" : "live")}";
                    return;
                }

                case "Mcb":
                case "Contactor":
                case "Disconnector":
                {
                    // IEC 60617: switch blade from the lower contact; at the fixed (upper) contact a cross for a
                    // circuit breaker, an arc for a contactor, a bar for a disconnector.
                    var closed = On(Info.Type switch { "Mcb" => "On", "Contactor" => "State", _ => "Closed" });
                    var tripped = Info.Type == "Mcb" && On("Tripped");
                    _body.Children.Add(Segment(Center, 2, Center, 30, Wire("Supply")));
                    _body.Children.Add(Segment(Center, 62, Center, 90, Wire("Live")));
                    _body.Children.Add(Blade(closed, tripped ? TripBrush : closed ? ClosedBrush : OpenBrush));
                    var marker = closed ? ClosedBrush : tripped ? TripBrush : OpenBrush;
                    switch (Info.Type)
                    {
                        case "Mcb":
                            _body.Children.Add(Segment(Center - 5, 25, Center + 5, 35, marker, 2));
                            _body.Children.Add(Segment(Center - 5, 35, Center + 5, 25, marker, 2));
                            break;
                        case "Contactor":
                            _body.Children.Add(Arc(Center, 30, marker));
                            break;
                        default:
                            _body.Children.Add(Segment(Center - 6, 30, Center + 6, 30, marker, 2));
                            break;
                    }

                    _state.Text = Info.Type switch
                    {
                        "Mcb" => tripped ? "TRIPPED" : closed ? "ON" : "OFF",
                        _ => closed ? "CLOSED" : "OPEN"
                    };
                    return;
                }

                case "EarthSwitch":
                {
                    // Line passes straight through; the earthing disconnector branches off to the earth symbol.
                    var earthed = On("Earthed");
                    var brush = earthed ? ClosedBrush : OpenBrush;
                    _body.Children.Add(Segment(Center, 2, Center, 90, Wire("Supply")));
                    _body.Children.Add(Segment(Center, 40, Center + 22, 40, Wire("Supply"), 2));
                    _body.Children.Add(Segment(Center + 16, 40, Center + 28, 40, brush, 2));
                    _body.Children.Add(earthed
                        ? Segment(Center + 22, 40, Center + 22, 66, brush, 3)
                        : Segment(Center + 22, 66, Center + 36, 46, brush, 3));
                    for (var k = 0; k < 3; k++)
                    {
                        _body.Children.Add(Segment(Center + 22 - (10 - k * 3), 68 + k * 4, Center + 22 + (10 - k * 3), 68 + k * 4, Brushes.Black, 1.5));
                    }

                    _state.Text = earthed ? "EARTHED" : "NOT EARTHED";
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

        /// <summary>Switch blade: closed = straight to the fixed contact, open = turned aside.</summary>
        private static Line Blade(bool closed, Brush brush) =>
            closed ? Segment(Center, 62, Center, 30, brush, 3) : Segment(Center, 62, Center + 17, 34, brush, 3);

        /// <summary>Contactor contact: half circle at the fixed contact.</summary>
        private static Path Arc(double x, double y, Brush brush) => new()
        {
            Stroke = brush,
            StrokeThickness = 2,
            Data = new PathGeometry([new PathFigure(new Point(x - 6, y), [new ArcSegment(new Point(x + 6, y), new Size(6, 6), 0, false, SweepDirection.Clockwise, true)], false)])
        };

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
