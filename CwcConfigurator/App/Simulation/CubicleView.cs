using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CwcConfigurator.Services.Simulation;
using CwcConfigurator.Services.Simulation.Process;

namespace CwcConfigurator.App.Simulation;

/// <summary>
/// Cubicle layout: the front door of a cabinet with push buttons, selector switches, indicator lamps and label
/// plates, drawn to scale (millimetres). In "Edit layout" the devices are added, dragged and edited; otherwise push
/// buttons are pressed (held) and switches turned. A selector switch is the relay-logic switch with the same tag.
/// </summary>
public sealed class CubicleView : UserControl
{
    private const double Bezel = 30;      // mm, diameter of a 22 mm device with its front ring
    private const double Lens = 22;       // mm
    private const double PlateWidth = 56; // mm
    private const double PlateHeight = 15;
    private const double Grid = 5;        // mm snap
    private const double Margin = 40;     // px around the door

    private static readonly Brush DoorBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xD5, 0xD9, 0xDE)));
    private static readonly Brush DoorEdgeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)));
    private static readonly Brush BezelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)));
    private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
    private static readonly Brush SelectBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)));

    private static readonly string[] Colors = ["Green", "Red", "Yellow", "White", "Blue", "Black"];

    private readonly Canvas _door = new() { Background = Brushes.White };
    private readonly ComboBox _cubicles = new() { Width = 200, Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox _zoom = new() { Width = 70, Margin = new Thickness(0, 0, 6, 0) };
    private readonly CheckBox _edit = new() { Content = "Edit layout", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly StackPanel _properties = new() { Margin = new Thickness(10) };

    private SimulationController? _controller;
    private string _plc = "";
    private int _index;
    private object? _selected; // CubicleItem or Cubicle
    private bool _loading;
    private CubicleItem? _dragging;
    private Point _dragOffset;

    public CubicleView()
    {
        var toolbar = new WrapPanel { Margin = new Thickness(8, 5, 8, 5) };
        toolbar.Children.Add(new TextBlock { Text = "Cubicle", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _cubicles.SelectionChanged += (_, _) =>
        {
            if (!_loading && _cubicles.SelectedIndex >= 0)
            {
                _index = _cubicles.SelectedIndex;
                Select(Cubicle);
            }
        };
        toolbar.Children.Add(_cubicles);
        toolbar.Children.Add(ToolButton("New cubicle", "Add a cubicle door.", (_, _) => NewCubicle()));
        toolbar.Children.Add(ToolButton("Delete cubicle", "Delete this cubicle door with its devices.", (_, _) => DeleteCubicle()));
        toolbar.Children.Add(new Separator { Width = 14, Opacity = 0 });
        toolbar.Children.Add(ToolButton("Push button", "Add a push button (momentary).", (_, _) => AddItem(CubicleItemKind.PushButton)));
        toolbar.Children.Add(ToolButton("Selector switch", "Add a selector switch (2 or 3 positions) — the same switch as in the relay logic.", (_, _) => AddItem(CubicleItemKind.Switch)));
        toolbar.Children.Add(ToolButton("Lamp", "Add an indicator lamp.", (_, _) => AddItem(CubicleItemKind.Lamp)));
        toolbar.Children.Add(ToolButton("Label", "Add a label plate (text only).", (_, _) => AddItem(CubicleItemKind.Label)));
        toolbar.Children.Add(ToolButton("Delete", "Delete the selected device (Del).", (_, _) => DeleteItem()));
        toolbar.Children.Add(_edit);
        toolbar.Children.Add(new TextBlock { Text = "Zoom", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) });
        _zoom.ItemsSource = new[] { "50%", "75%", "100%", "125%", "150%" };
        _zoom.SelectedItem = "100%";
        _zoom.SelectionChanged += (_, _) => Redraw();
        toolbar.Children.Add(_zoom);
        _edit.Checked += (_, _) => Redraw();
        _edit.Unchecked += (_, _) => Redraw();

        var host = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _door,
            Background = Brushes.White,
            Focusable = true
        };
        host.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && _selected is CubicleItem && _edit.IsChecked == true)
            {
                DeleteItem();
                e.Handled = true;
            }
        };
        _door.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == _door)
            {
                Select(Cubicle);
            }

            host.Focus();
        };
        _door.MouseMove += Door_MouseMove;
        _door.MouseLeftButtonUp += (_, _) => EndDrag();

        var left = new DockPanel();
        var toolbarBorder = new Border { Child = toolbar, BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)), BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.White };
        DockPanel.SetDock(toolbarBorder, Dock.Top);
        left.Children.Add(toolbarBorder);
        left.Children.Add(host);

        var right = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB)),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _properties }
        };

        var grid = new System.Windows.Controls.Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        System.Windows.Controls.Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        Content = grid;
    }

    public void Attach(SimulationController controller)
    {
        _controller = controller;
        controller.ModelChanged += () =>
        {
            LoadCubicles();
            Redraw();
        };
    }

    public void ShowPlc(string plc)
    {
        _plc = plc;
        _index = 0;
        LoadCubicles();
        Select(Cubicle);
    }

    /// <summary>Live state (switch positions changed elsewhere); called by the window timer.</summary>
    public void RefreshLive()
    {
        if (IsVisible && _dragging is null && Mouse.LeftButton != MouseButtonState.Pressed)
        {
            Redraw();
        }
    }

    private ProcessModel? Model => _controller is not null && _controller.Models.TryGetValue(_plc, out var model) ? model : null;

    private Cubicle? Cubicle => Model is { } model && _index >= 0 && _index < model.Cubicles.Count ? model.Cubicles[_index] : null;

    private double Scale => 0.01 * (double.TryParse((_zoom.SelectedItem as string ?? "100%").TrimEnd('%'), out var percent) ? percent : 100) * 1.6;

    // ---------- editing ----------

    private void LoadCubicles()
    {
        _loading = true;
        try
        {
            var names = Model?.Cubicles.Select(cubicle => cubicle.Name).ToList() ?? [];
            _cubicles.ItemsSource = names;
            _index = Math.Clamp(_index, 0, Math.Max(0, names.Count - 1));
            _cubicles.SelectedIndex = names.Count == 0 ? -1 : _index;
        }
        finally
        {
            _loading = false;
        }
    }

    private void NewCubicle()
    {
        if (Model is not { } model)
        {
            return;
        }

        model.Cubicles.Add(new Cubicle { Name = "+" + (model.Cubicles.Count + 1).ToString(CultureInfo.InvariantCulture) + " Cubicle" });
        _index = model.Cubicles.Count - 1;
        Changed();
        Select(Cubicle);
    }

    private void DeleteCubicle()
    {
        if (Model is not { } model || Cubicle is not { } cubicle)
        {
            return;
        }

        if (MessageBox.Show(Window.GetWindow(this)!, $"Delete the cubicle '{cubicle.Name}' with its {cubicle.Items.Count} device(s)?", "Cubicle",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        model.Cubicles.Remove(cubicle);
        PruneSwitches();
        _index = Math.Max(0, _index - 1);
        Changed();
        Select(Cubicle);
    }

    private void AddItem(string kind)
    {
        if (Cubicle is null)
        {
            NewCubicle();
        }

        if (Model is not { } model || Cubicle is not { } cubicle)
        {
            return;
        }

        // Next free place in a 70 mm grid from the top-left corner.
        var (x, y) = FreePlace(cubicle);
        var item = new CubicleItem
        {
            Kind = kind,
            X = x,
            Y = y,
            Color = kind == CubicleItemKind.Lamp ? "White" : kind == CubicleItemKind.Switch ? "Black" : "Green",
            Tag = kind == CubicleItemKind.Label ? "" : NextTag(kind == CubicleItemKind.Lamp ? "-H" : "-S"),
            Text = kind == CubicleItemKind.Label ? "LABEL" : ""
        };
        if (item.IsSwitch)
        {
            // The first relay-logic switch not on a cubicle yet (e.g. -S1 from the EPLAN import), else a new one.
            var placed = model.Cubicles.SelectMany(door => door.Items).Where(other => other.IsSwitch).Select(other => other.Tag.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (model.RelaySwitches.FirstOrDefault(selector => !placed.Contains(selector.Tag.Trim())) is { } free)
            {
                item.Tag = free.Tag.Trim();
                item.Text = free.Text;
            }
            else if (!model.RelaySwitches.Any(selector => SameTag(selector.Tag, item.Tag)))
            {
                model.RelaySwitches.Add(new RelaySwitch { Tag = item.Tag, Positions = "0/1", Position = "0" });
            }
        }

        cubicle.Items.Add(item);
        _edit.IsChecked = true;
        Changed();
        Select(item);
    }

    private void DeleteItem()
    {
        if (_selected is not CubicleItem item || Cubicle is not { } cubicle)
        {
            return;
        }

        cubicle.Items.Remove(item);
        PruneSwitches();
        Changed();
        Select(cubicle);
    }

    private (double X, double Y) FreePlace(Cubicle cubicle)
    {
        for (var row = 0; ; row++)
        {
            for (var column = 0; column < Math.Max(1, (int)((cubicle.Width - 40) / 70)); column++)
            {
                var x = 55 + column * 70;
                var y = 60 + row * 80;
                if (!cubicle.Items.Any(item => Math.Abs(item.X - x) < 35 && Math.Abs(item.Y - y) < 40))
                {
                    return (x, Math.Min(y, Math.Max(40, cubicle.Height - 40)));
                }
            }

            if (row > 40)
            {
                return (55, 60);
            }
        }
    }

    private string NextTag(string prefix)
    {
        var used = (Model?.Cubicles.SelectMany(cubicle => cubicle.Items).Select(item => item.Tag) ?? [])
            .Concat(Model?.RelaySwitches.Select(selector => selector.Tag) ?? [])
            .Concat(Model?.RelaySheets.SelectMany(sheet => sheet.Circuits).Select(circuit => circuit.Coil.Tag) ?? [])
            .Select(tag => tag.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var number = 1; ; number++)
        {
            var tag = prefix + number.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(tag))
            {
                return tag;
            }
        }
    }

    /// <summary>Relay-logic switches used neither by a contact nor by a cubicle are removed.</summary>
    private void PruneSwitches()
    {
        if (Model is not { } model)
        {
            return;
        }

        var used = model.RelaySheets.SelectMany(sheet => sheet.Circuits).SelectMany(circuit => Contacts(circuit.Elements)).Select(contact => contact.Tag.Trim())
            .Concat(model.Cubicles.SelectMany(cubicle => cubicle.Items).Where(item => item.IsSwitch).Select(item => item.Tag.Trim()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        model.RelaySwitches.RemoveAll(selector => !used.Contains(selector.Tag.Trim()));
    }

    private static IEnumerable<RelayElement> Contacts(IEnumerable<RelayElement> series) =>
        series.SelectMany(element => element.IsParallel ? element.Branches.SelectMany(Contacts) : [element]);

    private RelaySwitch? SwitchOf(CubicleItem item) =>
        item.IsSwitch ? Model?.RelaySwitches.FirstOrDefault(selector => SameTag(selector.Tag, item.Tag)) : null;

    private static bool SameTag(string a, string b) => a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Renaming a switch renames it everywhere: the relay-logic switch and its contacts.</summary>
    private void RenameSwitch(CubicleItem item, string tag)
    {
        if (Model is not { } model)
        {
            return;
        }

        var old = item.Tag;
        if (SwitchOf(item) is { } selector)
        {
            selector.Tag = tag;
            foreach (var contact in model.RelaySheets.SelectMany(sheet => sheet.Circuits).SelectMany(circuit => Contacts(circuit.Elements)).Where(contact => SameTag(contact.Tag, old)))
            {
                contact.Tag = tag;
            }
        }
        else
        {
            model.RelaySwitches.Add(new RelaySwitch { Tag = tag, Positions = "0/1", Position = "0" });
        }

        item.Tag = tag;
    }

    private void Changed()
    {
        _controller?.Rebuild(); // raises ModelChanged → reload + redraw
        if (_controller is null)
        {
            Redraw();
        }
    }

    private void Select(object? item)
    {
        _selected = item;
        Redraw();
        ShowProperties();
    }

    // ---------- drawing ----------

    private void Redraw()
    {
        _door.Children.Clear();
        if (Cubicle is not { } cubicle)
        {
            _door.Width = 600;
            _door.Height = 200;
            Text("No cubicle yet. Use 'New cubicle', then add push buttons, selector switches, lamps and labels.\n" +
                 "They can be connected to the relay logic and to signals later.", 20, 20, 13, DimBrush, 560);
            return;
        }

        var scale = Scale;
        var width = cubicle.Width * scale;
        var height = cubicle.Height * scale;
        _door.Width = width + 2 * Margin;
        _door.Height = height + 2 * Margin;

        // Door with its edge, hinges (left) and handle (right).
        var door = new Rectangle { Width = width, Height = height, Fill = DoorBrush, Stroke = DoorEdgeBrush, StrokeThickness = 2, RadiusX = 4, RadiusY = 4, IsHitTestVisible = false };
        Place(door, Margin, Margin);
        foreach (var hinge in new[] { 0.15, 0.85 })
        {
            Place(new Rectangle { Width = 6, Height = 40 * scale, Fill = DoorEdgeBrush, IsHitTestVisible = false }, Margin - 6, Margin + height * hinge - 20 * scale);
        }

        Place(new Rectangle { Width = 14 * scale, Height = 90 * scale, Fill = BezelBrush, RadiusX = 3, RadiusY = 3, IsHitTestVisible = false }, Margin + width - 26 * scale, Margin + height / 2 - 45 * scale);
        Text($"{cubicle.Name}   {cubicle.Width:0} × {cubicle.Height:0} mm", Margin, 12, 12, DimBrush, 600);

        foreach (var item in cubicle.Items)
        {
            DrawItem(item, Margin + item.X * scale, Margin + item.Y * scale, scale);
        }
    }

    private void DrawItem(CubicleItem item, double cx, double cy, double scale)
    {
        var selected = ReferenceEquals(_selected, item) && _edit.IsChecked == true;
        var group = new Canvas { Width = 0, Height = 0, Cursor = _edit.IsChecked == true ? Cursors.SizeAll : item.IsLabel || item.IsLamp ? Cursors.Arrow : Cursors.Hand, Background = Brushes.Transparent };
        if (item.IsLabel)
        {
            var plate = Plate(item.Text, (PlateWidth + 20) * scale, (PlateHeight + 4) * scale, 11 * scale, selected);
            AddTo(group, plate, -(PlateWidth + 20) * scale / 2, -(PlateHeight + 4) * scale / 2);
        }
        else
        {
            var bezel = Bezel * scale;
            var lens = Lens * scale;
            AddTo(group, new Ellipse { Width = bezel, Height = bezel, Fill = BezelBrush, Stroke = selected ? SelectBrush : Brushes.Black, StrokeThickness = selected ? 3 : 1 }, -bezel / 2, -bezel / 2);
            if (item.IsSwitch)
            {
                DrawSwitch(group, item, lens, scale);
            }
            else if (item.IsLamp)
            {
                var on = _controller?.Running == true && _controller.Engine.Relays.IsLampOn(item);
                var color = LensColor(item.Color);
                AddTo(group, new Ellipse
                {
                    Width = lens,
                    Height = lens,
                    Fill = new RadialGradientBrush(on ? Brighter(color) : Darker(color), on ? color : Darker(Darker(color))),
                    Stroke = Brushes.Black,
                    StrokeThickness = 0.8
                }, -lens / 2, -lens / 2);
            }
            else
            {
                var pressed = item.Pressed;
                var size = pressed ? lens * 0.9 : lens;
                var color = LensColor(item.Color);
                AddTo(group, new Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = new RadialGradientBrush(pressed ? Darker(color) : Brighter(color), pressed ? Darker(Darker(color)) : color),
                    Stroke = Brushes.Black,
                    StrokeThickness = 0.8
                }, -size / 2, -size / 2);
            }

            // Label plate under the device: tag and text.
            var text = (item.Tag.Trim().Length > 0 ? item.Tag.Trim() + "\n" : "") + item.Text;
            AddTo(group, Plate(text, PlateWidth * scale, (PlateHeight + (item.Text.Contains('\n') ? 6 : 0)) * scale, 8.5 * scale, false),
                -PlateWidth * scale / 2, bezel / 2 + 4 * scale);
        }

        group.ToolTip = ItemTitle(item);
        Place(group, cx, cy);
        group.MouseLeftButtonDown += (_, e) => Item_MouseDown(item, e);
        group.MouseLeftButtonUp += (_, _) =>
        {
            if (item.Pressed)
            {
                item.Pressed = false;
                Redraw();
            }
        };
        group.MouseLeave += (_, _) =>
        {
            if (item.Pressed)
            {
                item.Pressed = false;
                Redraw();
            }
        };
    }

    /// <summary>Selector switch: a black knob with a white handle pointing to the position, position names around it.</summary>
    private void DrawSwitch(Canvas group, CubicleItem item, double lens, double scale)
    {
        var selector = SwitchOf(item);
        var positions = selector?.PositionList() ?? ["0", "1"];
        var index = selector is null ? 0 : Math.Max(0, positions.FindIndex(position => position.Equals(selector.Current, StringComparison.OrdinalIgnoreCase)));
        var span = positions.Count >= 3 ? 45.0 : 30.0;
        double Angle(int i) => (positions.Count <= 1 ? 0 : -span + 2 * span * i / (positions.Count - 1)) * Math.PI / 180;

        AddTo(group, new Ellipse { Width = lens, Height = lens, Fill = new SolidColorBrush(LensColor(item.Color)), Stroke = Brushes.Black, StrokeThickness = 0.8 }, -lens / 2, -lens / 2);
        var handle = new Rectangle { Width = 5 * scale, Height = lens * 0.95, Fill = Brushes.White, RadiusX = 2, RadiusY = 2, RenderTransformOrigin = new Point(0.5, 0.5) };
        handle.RenderTransform = new RotateTransform(Angle(index) * 180 / Math.PI);
        AddTo(group, handle, -2.5 * scale, -lens * 0.475);
        var radius = Bezel * scale / 2 + (positions.Count >= 3 ? 12 : 8) * scale;
        for (var i = 0; i < positions.Count; i++)
        {
            var a = Angle(i);
            var label = new TextBlock
            {
                Text = positions[i],
                FontSize = Math.Max(7, 7.5 * scale),
                FontWeight = i == index ? FontWeights.Bold : FontWeights.Normal,
                Foreground = i == index ? SelectBrush : TextBrush,
                Width = 40 * scale,
                TextAlignment = TextAlignment.Center
            };
            AddTo(group, label, Math.Sin(a) * radius - 20 * scale, -Math.Cos(a) * radius - 6 * scale);
        }
    }

    private void Item_MouseDown(CubicleItem item, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_edit.IsChecked == true)
        {
            Select(item);
            _dragging = item;
            var position = e.GetPosition(_door);
            _dragOffset = new Point(position.X - (Margin + item.X * Scale), position.Y - (Margin + item.Y * Scale));
            _door.CaptureMouse();
            return;
        }

        // Operating the devices.
        if (item.IsButton)
        {
            item.Pressed = true;
            Redraw();
        }
        else if (item.IsSwitch && SwitchOf(item) is { } selector)
        {
            var positions = selector.PositionList();
            if (positions.Count == 0)
            {
                return;
            }

            // Click on the left half turns left, on the right half turns right (like turning the knob).
            var index = positions.FindIndex(position => position.Equals(selector.Current, StringComparison.OrdinalIgnoreCase));
            var clickX = e.GetPosition(_door).X - (Margin + item.X * Scale);
            index = Math.Clamp(index + (clickX < 0 ? -1 : 1), 0, positions.Count - 1);
            selector.Position = positions[index];
            _controller?.MarkDirty();
            Redraw();
        }
    }

    private void Door_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging is null || e.LeftButton != MouseButtonState.Pressed || Cubicle is not { } cubicle)
        {
            return;
        }

        var position = e.GetPosition(_door);
        var x = Math.Round(((position.X - _dragOffset.X - Margin) / Scale) / Grid) * Grid;
        var y = Math.Round(((position.Y - _dragOffset.Y - Margin) / Scale) / Grid) * Grid;
        _dragging.X = Math.Clamp(x, 0, cubicle.Width);
        _dragging.Y = Math.Clamp(y, 0, cubicle.Height);
        Redraw();
    }

    private void EndDrag()
    {
        if (_dragging is null)
        {
            return;
        }

        _dragging = null;
        _door.ReleaseMouseCapture();
        _controller?.MarkDirty();
        ShowProperties();
    }

    private static Border Plate(string text, double width, double height, double fontSize, bool selected) => new()
    {
        Width = width,
        MinHeight = height,
        Background = Brushes.White,
        BorderBrush = selected ? SelectBrush : Brushes.Black,
        BorderThickness = new Thickness(selected ? 2 : 0.8),
        CornerRadius = new CornerRadius(1.5),
        Padding = new Thickness(1),
        Child = new TextBlock
        {
            Text = text,
            FontSize = Math.Max(6, fontSize),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = TextBrush,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static void AddTo(Canvas canvas, UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        canvas.Children.Add(element);
    }

    private void Place(UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        _door.Children.Add(element);
    }

    private void Text(string text, double x, double y, double size, Brush brush, double width)
    {
        Place(new TextBlock { Text = text, FontSize = size, Foreground = brush, Width = width, TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false }, x, y);
    }

    private static Color LensColor(string name) => name.Trim().ToLowerInvariant() switch
    {
        "red" => Color.FromRgb(0xDC, 0x26, 0x26),
        "yellow" => Color.FromRgb(0xFA, 0xCC, 0x15),
        "white" => Color.FromRgb(0xF3, 0xF4, 0xF6),
        "blue" => Color.FromRgb(0x25, 0x63, 0xEB),
        "black" => Color.FromRgb(0x1F, 0x29, 0x37),
        _ => Color.FromRgb(0x16, 0xA3, 0x4A)
    };

    private static Color Brighter(Color color) => Color.FromRgb((byte)(color.R + (255 - color.R) / 2), (byte)(color.G + (255 - color.G) / 2), (byte)(color.B + (255 - color.B) / 2));

    private static Color Darker(Color color) => Color.FromRgb((byte)(color.R * 0.6), (byte)(color.G * 0.6), (byte)(color.B * 0.6));

    private static string ItemTitle(CubicleItem item) => item.Kind switch
    {
        CubicleItemKind.PushButton => $"Push button {item.Tag}",
        CubicleItemKind.Switch => $"Selector switch {item.Tag}",
        CubicleItemKind.Lamp => $"Lamp {item.Tag}",
        _ => "Label"
    } + (item.Text.Trim().Length > 0 ? " — " + item.Text.Replace('\n', ' ').Trim() : "");

    // ---------- properties ----------

    private void ShowProperties()
    {
        _properties.Children.Clear();
        switch (_selected)
        {
            case CubicleItem item:
                ItemProperties(item);
                break;
            case Cubicle cubicle:
                Header("Cubicle door");
                Field("Name", cubicle.Name, value =>
                {
                    cubicle.Name = value.Trim();
                    Changed();
                }, "E.g. +LPY01 Local control.");
                Field("Width [mm]", cubicle.Width.ToString("0", CultureInfo.InvariantCulture), value =>
                {
                    cubicle.Width = Math.Clamp(Number(value, cubicle.Width), 200, 2000);
                    Changed();
                });
                Field("Height [mm]", cubicle.Height.ToString("0", CultureInfo.InvariantCulture), value =>
                {
                    cubicle.Height = Math.Clamp(Number(value, cubicle.Height), 200, 2400);
                    Changed();
                });
                Note($"{cubicle.Items.Count} device(s). 'Edit layout' on: add and drag devices; off: press the push buttons (hold) and turn the switches (click the left or right half of the knob). Signals and lamps work while the simulation runs.");
                break;
            default:
                Header("Cubicle");
                Note("Add a cubicle door with 'New cubicle'.");
                break;
        }
    }

    private void ItemProperties(CubicleItem item)
    {
        Header(item.Kind switch
        {
            CubicleItemKind.PushButton => "Push button",
            CubicleItemKind.Switch => "Selector switch",
            CubicleItemKind.Lamp => "Indicator lamp",
            _ => "Label plate"
        });
        if (!item.IsLabel)
        {
            if (item.IsSwitch)
            {
                // A switch of the relay logic: choosing one connects this knob to it (position, text and contacts shared).
                var switches = (Model?.RelaySwitches ?? []).Where(selector => selector.Tag.Trim().Length > 0)
                    .Select(selector => $"{selector.Tag.Trim()}{PortRow.Separator}{selector.Text.Replace("\r", "").Replace('\n', ' ').Trim()}  ({selector.Positions})")
                    .ToList();
                SignalSearchField.Add(_properties, "Switch", item.Tag, switches, value =>
                {
                    var tag = value.Trim();
                    if (Model?.RelaySwitches.FirstOrDefault(selector => SameTag(selector.Tag, tag)) is { } existing)
                    {
                        item.Tag = existing.Tag.Trim(); // connect to the relay-logic switch
                        if (item.Text.Trim().Length == 0)
                        {
                            item.Text = existing.Text;
                        }

                        PruneSwitches();
                    }
                    else if (tag.Length > 0)
                    {
                        RenameSwitch(item, tag); // a new tag renames this switch everywhere
                    }

                    Changed();
                    ShowProperties();
                }, "The selector switch of the relay logic this knob turns (type to search, e.g. s1 or gate control). A new tag renames the switch everywhere.", DimBrush);
            }
            else
            {
                Field("Tag", item.Tag, value =>
                {
                    item.Tag = value.Trim();
                    Changed();
                    ShowProperties();
                }, "Device tag, e.g. -S11 or -H1.");
            }
        }

        Field(item.IsLabel ? "Text" : "Plate text", item.Text, value =>
        {
            item.Text = value;
            Changed();
        }, "Enter = new line.", multiline: true);
        if (!item.IsLabel)
        {
            Choice("Colour", Colors, Colors.FirstOrDefault(color => color.Equals(item.Color, StringComparison.OrdinalIgnoreCase)) ?? Colors[0], value =>
            {
                item.Color = value;
                Changed();
            });
        }

        if (item.IsSwitch && SwitchOf(item) is { } selector)
        {
            Field("Positions", selector.Positions, value =>
            {
                selector.Positions = string.Join("/", value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                Changed();
                ShowProperties();
            }, "2 or 3 positions separated by '/', e.g. OFF/ON, LOCAL/0/REMOTE or MAN/0/AUTO.");
            Choice("Position now", selector.PositionList().ToArray(), selector.Current, value =>
            {
                selector.Position = value;
                _controller?.MarkDirty();
                Redraw();
            });
            var uses = Model?.RelaySheets.SelectMany(sheet => sheet.Circuits).SelectMany(circuit => Contacts(circuit.Elements)).Count(contact => SameTag(contact.Tag, selector.Tag)) ?? 0;
            Note(uses == 0 ? "Not used in the relay logic yet." : $"Used by {uses} contact(s) in the relay logic.");
        }
        if (item.IsSwitch && SwitchOf(item) is { } positions)
        {
            // One PLC input field per position: 1 while the switch is in that position.
            var outputs = item.OutputsByPosition();
            foreach (var position in positions.PositionList())
            {
                SignalSearchField.Add(_properties, $"Signal in {position}", outputs.GetValueOrDefault(position, ""), Signals(PortDirection.Output), value =>
                {
                    var current = item.OutputsByPosition();
                    current[position] = value;
                    item.SetOutputsByPosition(current);
                    Changed();
                }, null, DimBrush);
            }

            Note("Each position switches its PLC inputs to 1 (the others to 0) while the simulation runs. Contacts with this tag on the relay-logic sheets follow the switch too.");
        }
        else if (item.IsButton)
        {
            Choice("Contact", ["NO", "NC"], item.Contact.Equals("NC", StringComparison.OrdinalIgnoreCase) ? "NC" : "NO", value =>
            {
                item.Contact = value;
                Changed();
            });
            SignalSearchField.Add(_properties, "Drives", item.Outputs, Signals(PortDirection.Output), value =>
            {
                item.Outputs = value;
                Changed();
            }, "PLC inputs switched by the button (type to search; several: DI:69, DI:70). NO: 1 while pressed; NC: 0 while pressed.", DimBrush);
            Note("Contacts with this tag on the relay-logic sheets are operated while the button is held down.");
        }
        else if (item.IsLamp)
        {
            SignalSearchField.Add(_properties, "Lit by", item.Source, SignalSearchField.Devices(Model).Concat(Signals(PortDirection.Input)).ToList(), value =>
            {
                item.Source = value;
                Changed();
            }, "A relay (-K101), a push button, a PLC output (DO:12), an input (DI:3) or an object (@Gate1.Open).", DimBrush);
        }

        Note($"Position: X {item.X:0} mm, Y {item.Y:0} mm (drag it in 'Edit layout').");
    }

    private List<string> Signals(PortDirection direction) => SignalSearchField.Signals(_controller, _plc, direction);

    private void Header(string text) =>
        _properties.Children.Add(new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });

    private void Note(string text) =>
        _properties.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = DimBrush, Margin = new Thickness(0, 6, 0, 6) });

    private void Field(string label, string value, Action<string> apply, string? hint = null, bool multiline = false)
    {
        _properties.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2) });
        var box = new TextBox
        {
            Text = value,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 54 : 24,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center
        };
        var original = value;
        void Commit()
        {
            if (box.Text != original)
            {
                original = box.Text;
                apply(box.Text);
            }
        }

        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !multiline)
            {
                Commit();
                e.Handled = true;
            }
        };
        _properties.Children.Add(box);
        if (hint is not null)
        {
            _properties.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Foreground = DimBrush, FontSize = 11, Margin = new Thickness(0, 2, 0, 4) });
        }
    }

    private void Choice(string label, string[] values, string value, Action<string> apply)
    {
        _properties.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2) });
        var combo = new ComboBox { ItemsSource = values, SelectedItem = value, MinHeight = 24 };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string selected && selected != value)
            {
                value = selected;
                apply(selected);
            }
        };
        _properties.Children.Add(combo);
    }

    private static double Number(string text, double fallback) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static Button ToolButton(string text, string tooltip, RoutedEventHandler click)
    {
        var button = new Button { Content = text, ToolTip = tooltip, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), MinHeight = 26 };
        button.Click += click;
        return button;
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
