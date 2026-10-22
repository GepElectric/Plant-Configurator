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
        private const double MinBusLength = 80;
        private static readonly Brush TripBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)));
        private static readonly string[] Types = ["Busbar", "AuxSupply", "Mcb", "Contactor", "Disconnector", "EarthSwitch", "Motor", "Transformer", "Breaker", "Generator", "Grid"];

        private readonly Canvas _canvas = new();
        private readonly Canvas _body = new();
        private readonly TextBlock _name;
        private readonly TextBlock _state;
        private readonly Border _border;
        private readonly bool _bus;
        private readonly bool _vertical;
        private double _busLength;
        private readonly Action? _changed;
        private Rectangle? _handle;
        private Point _resizeStart;
        private double _resizeLength;

        public static bool Supports(string type) => Types.Contains(type, StringComparer.OrdinalIgnoreCase);

        /// <param name="changed">Called after the busbar was resized on the sheet.</param>
        public SldSymbol(ObjectConfig config, ObjectTypeInfo info, Action? changed = null) : base(config, info)
        {
            _bus = info.Type == "Busbar";
            _changed = changed;
            _vertical = _bus && info.OptionValue(config, "Orientation") == "Vertical";
            _busLength = Math.Max(MinBusLength, config.Params.TryGetValue("Length", out var length) ? length : info.Params.FirstOrDefault(param => param.Name == "Length")?.Default ?? 300);

            _name = new TextBlock { Text = config.Name, FontWeight = FontWeights.SemiBold, FontSize = 12 };
            _state = new TextBlock { FontSize = 10.5, Foreground = Brushes.DimGray };
            _canvas.Width = !_bus ? 150 : _vertical ? 130 : _busLength;
            _canvas.Height = !_bus ? 92 : _vertical ? _busLength + 20 : 44;
            _canvas.Children.Add(_body);
            var label = new StackPanel { Children = { _name, _state } };
            Canvas.SetLeft(label, !_bus ? 52 : _vertical ? 36 : 0);
            Canvas.SetTop(label, _bus ? 0 : 28);
            _canvas.Children.Add(label);

            var active = info.Ports.Where(port => port.Power && info.IsActive(port.ShownWhen, config)).ToList();
            var inputs = active.Where(port => port.Direction == PortDirection.Input).ToList();
            for (var i = 0; i < inputs.Count; i++)
            {
                // Incoming supplies on top (left of a vertical bar), spread along the bar.
                var along = _busLength * (i + 1) / (inputs.Count + 1);
                if (!_bus)
                {
                    AddPin(inputs[i], Center, 2);
                }
                else if (_vertical)
                {
                    AddPin(inputs[i], 10, 20 + along);
                }
                else
                {
                    AddPin(inputs[i], along, 18);
                }
            }

            // Outgoing feeders: pins along the bar for new links (a linked feeder leaves the bar where its feeder is).
            var outputs = active.Where(port => port.Direction == PortDirection.Output).ToList();
            for (var i = 0; i < outputs.Count; i++)
            {
                var along = _busLength * (i + 1) / (outputs.Count + 1);
                if (!_bus)
                {
                    AddPin(outputs[i], Center, 90);
                }
                else if (_vertical)
                {
                    AddPin(outputs[i], 30, 20 + along);
                }
                else
                {
                    AddPin(outputs[i], along, 34);
                }
            }

            if (_bus)
            {
                AddResizeHandle();
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

        /// <summary>
        /// A busbar feeds its feeders where they are: the link leaves the bar straight above (or beside) the breaker,
        /// so a breaker under the bar gets a straight line.
        /// </summary>
        public override Point Anchor(string port, Point? towards)
        {
            var point = base.Anchor(port, towards);
            if (_bus && towards is { } other && Info.Port(port) is { Direction: PortDirection.Output })
            {
                var left = Canvas.GetLeft(Element) + 4;
                var top = Canvas.GetTop(Element) + 4;
                return _vertical
                    ? new Point(left + 30, Math.Clamp(other.Y, top + 26, top + 20 + _busLength - 6))
                    : new Point(Math.Clamp(other.X, left + 6, left + _busLength - 6), top + 34);
            }

            return point;
        }

        /// <summary>A linked feeder's pin is not needed any more: its line starts where the feeder is.</summary>
        public override void LinkedOutput(string port)
        {
            if (_bus && PinElements.TryGetValue(port, out var pin))
            {
                pin.Visibility = Visibility.Hidden;
            }
        }

        /// <summary>Handle at the end of the bar: drag to make the busbar longer or shorter.</summary>
        private void AddResizeHandle()
        {
            _handle = new Rectangle
            {
                Width = 10,
                Height = 10,
                Fill = HighlightBrush,
                Cursor = _vertical ? Cursors.SizeNS : Cursors.SizeWE,
                ToolTip = "Drag to change the length of the busbar"
            };
            Panel.SetZIndex(_handle, 6);
            PlaceHandle();
            _handle.MouseLeftButtonDown += (_, e) =>
            {
                _resizeStart = e.GetPosition(_canvas);
                _resizeLength = _busLength;
                _handle.CaptureMouse();
                e.Handled = true;
            };
            _handle.MouseMove += (_, e) =>
            {
                if (!_handle.IsMouseCaptured)
                {
                    return;
                }

                var position = e.GetPosition(_canvas);
                var delta = _vertical ? position.Y - _resizeStart.Y : position.X - _resizeStart.X;
                _busLength = Math.Max(MinBusLength, Math.Round((_resizeLength + delta) / 10) * 10);
                if (_vertical)
                {
                    _canvas.Height = _busLength + 20;
                }
                else
                {
                    _canvas.Width = _busLength;
                }

                PlaceHandle();
                Draw(null);
            };
            _handle.MouseLeftButtonUp += (_, e) =>
            {
                if (!_handle.IsMouseCaptured)
                {
                    return;
                }

                _handle.ReleaseMouseCapture();
                e.Handled = true;
                Config.Params["Length"] = _busLength;
                _changed?.Invoke();
            };
            _canvas.Children.Add(_handle);
        }

        private void PlaceHandle()
        {
            Canvas.SetLeft(_handle!, _vertical ? 13 : _busLength - 10);
            Canvas.SetTop(_handle!, _vertical ? 20 + _busLength - 10 : 25);
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
                {
                    var live = On("Live");
                    var fill = instance is null ? DeadBrush : live ? LiveBrush : DeadBrush;
                    _body.Children.Add(_vertical
                        ? Shape(new Rectangle { Width = 8, Height = _busLength, Fill = fill }, 16, 20)
                        : Shape(new Rectangle { Width = _busLength, Height = 8, Fill = fill }, 0, 26));
                    _state.Text = "";
                    _name.Text = $"{Config.Name}   {(instance is null ? "" : !live ? "dead" : $"{Value(instance, "Feeder1")} V")}";
                    return;
                }

                case "Mcb":
                case "Breaker":
                case "Contactor":
                case "Disconnector":
                {
                    // IEC 60617: switch blade from the lower contact; at the fixed (upper) contact a cross for a
                    // circuit breaker, an arc for a contactor, a bar for a disconnector.
                    var closed = On(Info.Type switch { "Mcb" => "On", "Contactor" => "State", _ => "Closed" });
                    var tripped = Info.Type is "Mcb" or "Breaker" && On("Tripped") && !closed;
                    _body.Children.Add(Segment(Center, 2, Center, 30, Wire("Supply")));
                    _body.Children.Add(Segment(Center, 62, Center, 90, Wire("Live")));
                    _body.Children.Add(Blade(closed, tripped ? TripBrush : closed ? ClosedBrush : OpenBrush));
                    var marker = closed ? ClosedBrush : tripped ? TripBrush : OpenBrush;
                    switch (Info.Type)
                    {
                        case "Mcb":
                        case "Breaker":
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
                        "Breaker" => tripped ? "TRIPPED" : closed ? "CLOSED" : "OPEN",
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

                case "Transformer":
                {
                    // IEC: two overlapping circles, primary above, secondary below.
                    _body.Children.Add(Segment(Center, 2, Center, 22, Wire("Primary")));
                    _body.Children.Add(Segment(Center, 70, Center, 90, Wire("Secondary")));
                    _body.Children.Add(Shape(new Ellipse { Width = 30, Height = 30, StrokeThickness = 2.5, Stroke = Wire("Primary") }, Center - 15, 22));
                    _body.Children.Add(Shape(new Ellipse { Width = 30, Height = 30, StrokeThickness = 2.5, Stroke = Wire("Secondary") }, Center - 15, 40));
                    _state.Text = instance is null ? "" : $"{Value(instance, "Primary")} / {Value(instance, "Secondary")} V";
                    return;
                }

                case "AuxSupply":
                {
                    // Measuring instrument on the line: circle with W.
                    _body.Children.Add(Segment(Center, 2, Center, 40, Wire("Supply")));
                    _body.Children.Add(Shape(new Ellipse { Width = 34, Height = 34, StrokeThickness = 2, Stroke = Brushes.Black, Fill = Brushes.White }, Center - 17, 40));
                    _body.Children.Add(Shape(new TextBlock { Text = "W", FontWeight = FontWeights.Bold, FontSize = 13 }, Center - 6, 48));
                    _state.Text = instance is null ? "" : $"{Value(instance, "U")} V  {Value(instance, "P")} kW";
                    return;
                }

                case "Generator":
                {
                    // IEC: circle with G and ~, connected from above (generator breaker).
                    _body.Children.Add(Segment(Center, 2, Center, 40, Wire("Grid")));
                    var running = instance is not null && instance.Value("Terminal") >= 1;
                    _body.Children.Add(Shape(new Ellipse
                    {
                        Width = 38,
                        Height = 38,
                        StrokeThickness = 2.5,
                        Stroke = running ? ClosedBrush : OpenBrush,
                        Fill = running ? new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2)) : Brushes.White
                    }, Center - 19, 40));
                    _body.Children.Add(Shape(new TextBlock { Text = "G", FontWeight = FontWeights.Bold, FontSize = 14 }, Center - 5, 44));
                    _body.Children.Add(Shape(new TextBlock { Text = "~", FontWeight = FontWeights.Bold, FontSize = 13 }, Center - 4, 58));
                    _state.Text = instance is null ? "" : $"{Value(instance, "P")} kW  {Value(instance, "Terminal")} V";
                    return;
                }

                case "Grid":
                {
                    // Network infeed: hatched box, connection downwards.
                    var live = On("Live");
                    var brush = live ? LiveBrush : DeadBrush;
                    _body.Children.Add(Shape(new Rectangle { Width = 40, Height = 30, StrokeThickness = 2, Stroke = brush, Fill = Brushes.White }, Center - 20, 10));
                    for (var k = 0; k < 4; k++)
                    {
                        _body.Children.Add(Segment(Center - 20 + k * 10, 40, Center - 10 + k * 10, 10, brush, 1.2));
                    }

                    _body.Children.Add(Segment(Center, 40, Center, 90, Wire("Live")));
                    _state.Text = instance is null ? "" : live ? $"{Value(instance, "Live")} V  export {Value(instance, "P")} kW" : "BLACKOUT";
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
