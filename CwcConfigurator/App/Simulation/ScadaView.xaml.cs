using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CwcConfigurator.Services.Simulation;
using CwcConfigurator.Services.Simulation.Process;

namespace CwcConfigurator.App.Simulation;

/// <summary>
/// "SCADA" tab: the plant sheet of one PLC. Every object is a tile with its live values and active states,
/// links are drawn between tiles. Clicking a tile shows its variants, parameters and output forcing.
/// </summary>
public partial class ScadaView : UserControl
{
    private const double TileWidth = 180;
    private static readonly Dictionary<string, Color> CategoryColors = new()
    {
        ["Waterway"] = Color.FromRgb(0x25, 0x63, 0xEB),
        ["Hydraulics"] = Color.FromRgb(0xD9, 0x77, 0x06),
        ["Turbine"] = Color.FromRgb(0x05, 0x96, 0x69),
        ["Electrical"] = Color.FromRgb(0x7C, 0x3A, 0xED),
        ["Generator"] = Color.FromRgb(0xDB, 0x27, 0x77),
        ["General"] = Color.FromRgb(0x6B, 0x72, 0x80)
    };

    private readonly Dictionary<string, Tile> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Line Line, string From, string To)> _lines = [];
    private SimulationController? _controller;
    private string _plc = "";
    private string? _selected;
    private List<ForceRow> _forces = [];
    private Tile? _drag;
    private Point _dragStart;
    private Point _tileStart;
    private bool _dragMoved;

    public ScadaView()
    {
        InitializeComponent();
    }

    public void Attach(SimulationController controller)
    {
        _controller = controller;
        controller.ModelChanged += Rebuild;
    }

    public void ShowPlc(string plc)
    {
        _plc = plc;
        _selected = null;
        Rebuild();
    }

    /// <summary>Updates tile values and the force table (called from the window timer).</summary>
    public void RefreshLive()
    {
        if (_controller is null || !IsVisible)
        {
            return;
        }

        foreach (var tile in _tiles.Values)
        {
            tile.Refresh(_controller.Engine.Find(_plc, tile.Config.Name));
        }

        foreach (var row in _forces)
        {
            row.Refresh();
        }
    }

    private void Rebuild()
    {
        Sheet.Children.Clear();
        _tiles.Clear();
        _lines.Clear();
        if (_controller is null || !_controller.Models.TryGetValue(_plc, out var model))
        {
            ShowDetails(null);
            return;
        }

        var maxX = 0.0;
        var maxY = 0.0;
        foreach (var config in model.Objects)
        {
            var info = ObjectCatalog.Find(config.Type);
            if (info is null)
            {
                continue;
            }

            var tile = new Tile(config, info, CategoryColors.GetValueOrDefault(info.Category, Colors.Gray));
            tile.Border.MouseLeftButtonDown += (_, e) => StartDrag(tile, e);
            tile.Border.MouseMove += (_, e) => Drag(e);
            tile.Border.MouseLeftButtonUp += (_, _) => EndDrag();
            Canvas.SetLeft(tile.Border, config.X);
            Canvas.SetTop(tile.Border, config.Y);
            Panel.SetZIndex(tile.Border, 1);
            Sheet.Children.Add(tile.Border);
            _tiles[config.Name] = tile;
            maxX = Math.Max(maxX, config.X + TileWidth);
            maxY = Math.Max(maxY, config.Y + 160);
        }

        foreach (var config in model.Objects)
        {
            foreach (var text in config.Ports.Values)
            {
                if (PortBinding.Parse(text) is { Kind: BindingKind.Link } link && !link.LinkObject.Contains('/')
                    && _tiles.ContainsKey(link.LinkObject) && _tiles.ContainsKey(config.Name)
                    && !_lines.Any(item => item.From == link.LinkObject && item.To == config.Name))
                {
                    var line = new Line { Stroke = new SolidColorBrush(Color.FromArgb(0x90, 0x6B, 0x72, 0x80)), StrokeThickness = 1.5 };
                    Panel.SetZIndex(line, 0);
                    Sheet.Children.Add(line);
                    _lines.Add((line, link.LinkObject, config.Name));
                }
            }
        }

        Sheet.Width = Math.Max(1600, maxX + 200);
        Sheet.Height = Math.Max(1000, maxY + 200);
        Dispatcher.BeginInvoke(UpdateLines, System.Windows.Threading.DispatcherPriority.Loaded);
        ShowDetails(_selected is not null && _tiles.ContainsKey(_selected) ? _selected : null);
        RefreshLive();
    }

    private void UpdateLines()
    {
        foreach (var (line, from, to) in _lines)
        {
            var a = _tiles[from].Border;
            var b = _tiles[to].Border;
            var ax = Canvas.GetLeft(a) + a.ActualWidth / 2;
            var ay = Canvas.GetTop(a) + a.ActualHeight / 2;
            var bx = Canvas.GetLeft(b) + b.ActualWidth / 2;
            var by = Canvas.GetTop(b) + b.ActualHeight / 2;
            line.X1 = ax;
            line.Y1 = ay;
            line.X2 = bx;
            line.Y2 = by;
        }
    }

    private void StartDrag(Tile tile, MouseButtonEventArgs e)
    {
        _drag = tile;
        _dragMoved = false;
        _dragStart = e.GetPosition(Sheet);
        _tileStart = new Point(Canvas.GetLeft(tile.Border), Canvas.GetTop(tile.Border));
        tile.Border.CaptureMouse();
        e.Handled = true;
    }

    private void Drag(MouseEventArgs e)
    {
        if (_drag is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(Sheet);
        var dx = position.X - _dragStart.X;
        var dy = position.Y - _dragStart.Y;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 4)
        {
            return;
        }

        _dragMoved = true;
        var x = Math.Max(0, Math.Round((_tileStart.X + dx) / 10) * 10);
        var y = Math.Max(0, Math.Round((_tileStart.Y + dy) / 10) * 10);
        Canvas.SetLeft(_drag.Border, x);
        Canvas.SetTop(_drag.Border, y);
        UpdateLines();
    }

    private void EndDrag()
    {
        if (_drag is null)
        {
            return;
        }

        var tile = _drag;
        _drag = null;
        tile.Border.ReleaseMouseCapture();
        if (_dragMoved)
        {
            tile.Config.X = Canvas.GetLeft(tile.Border);
            tile.Config.Y = Canvas.GetTop(tile.Border);
            _controller?.MarkDirty();
        }
        else
        {
            ShowDetails(tile.Config.Name);
        }
    }

    private void Sheet_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == Sheet)
        {
            ShowDetails(null);
        }
    }

    private void ShowDetails(string? name)
    {
        _selected = name;
        foreach (var tile in _tiles.Values)
        {
            tile.SetSelected(tile.Config.Name == name);
        }

        var instance = name is null ? null : _controller?.Engine.Find(_plc, name);
        if (instance is null)
        {
            TitleText.Text = "Select an object";
            SubtitleText.Text = "Click an object on the plant sheet to set its parameters and force its outputs. Drag objects to arrange the sheet.";
            ObjectPanel.Visibility = Visibility.Collapsed;
            _forces = [];
            return;
        }

        TitleText.Text = instance.Name;
        SubtitleText.Text = string.IsNullOrWhiteSpace(instance.Config.Description) ? instance.Info.Title : $"{instance.Info.Title} — {instance.Config.Description}";
        ObjectPanel.Visibility = Visibility.Visible;
        OptionsList.ItemsSource = instance.Info.Options.Select(option => new OptionRow(option, instance.Config, () => _controller?.MarkDirty())).ToList();
        ParamsGrid.ItemsSource = instance.Info.Params.Select(param => new ParamRow(param, instance.Config, () => _controller?.MarkDirty())).ToList();
        _forces = instance.Info.Ports.Where(port => port.Direction == PortDirection.Output)
            .Select(port => new ForceRow(port, instance, () => _tiles.GetValueOrDefault(instance.Name)?.Refresh(instance)))
            .ToList();
        ForcesGrid.ItemsSource = _forces;
        RefreshLive();
    }

    /// <summary>One object on the sheet.</summary>
    private sealed class Tile
    {
        private readonly ObjectTypeInfo _info;
        private readonly StackPanel _values = new();
        private readonly WrapPanel _states = new() { Margin = new Thickness(0, 4, 0, 0) };
        private readonly Color _color;

        public Tile(ObjectConfig config, ObjectTypeInfo info, Color color)
        {
            Config = config;
            _info = info;
            _color = color;
            var header = new Border
            {
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(5, 5, 0, 0),
                Padding = new Thickness(8, 4, 8, 4),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = config.Name, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = info.Title, Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)), FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis }
                    }
                }
            };
            Border = new Border
            {
                Width = TileWidth,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(color),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6),
                Cursor = Cursors.Hand,
                ToolTip = string.IsNullOrWhiteSpace(config.Description) ? info.Description : config.Description,
                Child = new StackPanel
                {
                    Children =
                    {
                        header,
                        new StackPanel { Margin = new Thickness(8, 4, 8, 6), Children = { _values, _states } }
                    }
                }
            };
        }

        public ObjectConfig Config { get; }
        public Border Border { get; }

        public void SetSelected(bool selected)
        {
            Border.BorderThickness = new Thickness(selected ? 3 : 1.5);
            Border.Effect = selected ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Opacity = 0.5, Color = _color } : null;
        }

        public void Refresh(SimObject? instance)
        {
            _values.Children.Clear();
            _states.Children.Clear();
            if (instance is null)
            {
                return;
            }

            foreach (var name in _info.ScadaValues)
            {
                var port = _info.Port(name);
                if (port is null)
                {
                    continue;
                }

                var forced = instance.Forces.ContainsKey(name);
                var value = instance.Value(name).ToString(Math.Abs(instance.Value(name)) >= 100 ? "0.#" : "0.##", CultureInfo.InvariantCulture);
                _values.Children.Add(new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
                    Children =
                    {
                        new TextBlock { Text = name, Foreground = Brushes.DimGray, FontSize = 11 },
                        Column(new TextBlock
                        {
                            Text = $"{value} {port.Unit}".Trim(),
                            FontWeight = FontWeights.SemiBold,
                            FontSize = 11.5,
                            Foreground = forced ? Brushes.DarkOrange : Brushes.Black
                        }, 1)
                    }
                });
            }

            foreach (var port in _info.Ports.Where(port => port.Direction == PortDirection.Output && port.Type == PortType.Bool))
            {
                if (instance.Value(port.Name) < 0.5)
                {
                    continue;
                }

                var forced = instance.Forces.ContainsKey(port.Name);
                _states.Children.Add(new Border
                {
                    Background = forced ? new SolidColorBrush(Color.FromRgb(0xFE, 0xD7, 0xAA)) : new SolidColorBrush(Color.FromRgb(0xB7, 0xE4, 0xC7)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 0, 4, 0),
                    Margin = new Thickness(0, 0, 3, 3),
                    Child = new TextBlock { Text = port.Name, FontSize = 10.5 }
                });
            }
        }

        private static UIElement Column(UIElement element, int column)
        {
            Grid.SetColumn(element, column);
            return element;
        }
    }
}
