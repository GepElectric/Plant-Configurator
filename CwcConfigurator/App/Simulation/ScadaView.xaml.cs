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
/// "SCADA" tab: the plant sheets of one PLC. Every object is a tile with pins (inputs left, outputs right), its live
/// values and active states; dragging from a pin to a pin links them. Energy connections (Supply/Live) are drawn as
/// power lines, red while live. A screen can be a single-line diagram: electrical objects are then SLD symbols.
/// Clicking an object shows its variants, parameters and output forcing.
/// </summary>
public partial class ScadaView : UserControl
{
    private static readonly Dictionary<string, Color> CategoryColors = new()
    {
        ["Waterway"] = Color.FromRgb(0x25, 0x63, 0xEB),
        ["Hydraulics"] = Color.FromRgb(0xD9, 0x77, 0x06),
        ["Turbine"] = Color.FromRgb(0x05, 0x96, 0x69),
        ["Electrical"] = Color.FromRgb(0x7C, 0x3A, 0xED),
        ["Generator"] = Color.FromRgb(0xDB, 0x27, 0x77),
        ["General"] = Color.FromRgb(0x6B, 0x72, 0x80)
    };

    internal static readonly Brush LiveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)));
    internal static readonly Brush DeadBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)));
    internal static readonly Brush ClosedBrush = LiveBrush;
    internal static readonly Brush OpenBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)));
    private static readonly Brush SignalLinkBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0x47, 0x55, 0x69)));
    private static readonly Brush HighlightBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)));

    private readonly Dictionary<string, SheetItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LinkView> _links = [];
    private readonly List<(Border Frame, string Group)> _frames = [];
    private List<ObjectConfig>? _groupDrag;
    private List<Point> _groupStart = [];
    private SimulationController? _controller;
    private string _plc = "";
    private string _screen = "";
    private bool _updatingScreens;
    private string? _selected;
    private List<ForceRow> _forces = [];
    private SheetItem? _drag;
    private Point _dragStart;
    private Point _itemStart;
    private bool _dragMoved;
    private (SheetItem Item, PortDef Port)? _wireFrom;
    private Line? _wireLine;

    public ScadaView()
    {
        InitializeComponent();
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    public void Attach(SimulationController controller)
    {
        _controller = controller;
        controller.ModelChanged += Rebuild;
        controller.LayoutChanged += Rebuild;
    }

    public void ShowPlc(string plc)
    {
        _plc = plc;
        _selected = null;
        _screen = "";
        Rebuild();
    }

    private ProcessModel? Model => _controller is not null && _controller.Models.TryGetValue(_plc, out var model) ? model : null;

    /// <summary>Updates values, symbols, power line colours and the force table (called from the window timer).</summary>
    public void RefreshLive()
    {
        if (_controller is null || !IsVisible)
        {
            return;
        }

        foreach (var item in _items.Values)
        {
            item.Refresh(_controller.Engine.Find(_plc, item.Config.Name));
        }

        foreach (var link in _links.Where(link => link.Power))
        {
            var live = _controller.Engine.Find(_plc, link.From)?.Value(link.FromPort) >= 0.5;
            link.Path.Stroke = live ? LiveBrush : DeadBrush;
        }

        foreach (var row in _forces)
        {
            row.Refresh();
        }
    }

    private void Rebuild()
    {
        CancelWire();
        Sheet.Children.Clear();
        _items.Clear();
        _links.Clear();
        _frames.Clear();
        if (Model is not { } model)
        {
            ShowDetails(null);
            return;
        }

        var screens = model.AllScreens();
        if (!screens.Contains(_screen, StringComparer.OrdinalIgnoreCase))
        {
            _screen = screens[0];
        }

        _updatingScreens = true;
        ScreenTabs.ItemsSource = screens;
        ScreenTabs.SelectedItem = screens.First(name => name.Equals(_screen, StringComparison.OrdinalIgnoreCase));
        SldCheck.IsChecked = model.IsSld(_screen);
        _updatingScreens = false;

        var sld = model.IsSld(_screen);
        var maxX = 0.0;
        var maxY = 0.0;
        foreach (var config in model.Objects.Where(item => model.IsOn(item, _screen)))
        {
            var info = ObjectCatalog.Find(config.Type);
            if (info is null)
            {
                continue;
            }

            SheetItem item = sld && SldSymbol.Supports(info.Type)
                ? new SldSymbol(config, info)
                : new Tile(config, info, CategoryColors.GetValueOrDefault(info.Category, Colors.Gray), model);
            item.Element.MouseLeftButtonDown += (_, e) => StartDrag(item, e);
            item.Element.MouseMove += (_, e) => Drag(e);
            item.Element.MouseLeftButtonUp += (_, _) => EndDrag();
            foreach (var (port, pin) in item.Pins)
            {
                pin.MouseLeftButtonDown += (_, e) => StartWire(item, port, e);
                pin.ContextMenuOpening += (_, _) => pin.ContextMenu = PinMenu(item, port);
                pin.ContextMenu = new ContextMenu();
            }

            var (x, y) = model.PositionOn(config, _screen);
            Canvas.SetLeft(item.Element, x);
            Canvas.SetTop(item.Element, y);
            Panel.SetZIndex(item.Element, 1);
            Sheet.Children.Add(item.Element);
            _items[config.Name] = item;
            maxX = Math.Max(maxX, x + 260);
            maxY = Math.Max(maxY, y + 260);
        }

        // Links are stored on the inputs ("@Object.Port"); only links between objects of this screen are drawn.
        foreach (var item in _items.Values)
        {
            foreach (var port in item.Info.Ports.Where(port => port.Direction == PortDirection.Input && item.Info.IsActive(port.ShownWhen, item.Config)))
            {
                if (!item.Config.Ports.TryGetValue(port.Name, out var text)
                    || PortBinding.Parse(text) is not { Kind: BindingKind.Link } link || link.LinkObject.Contains('/')
                    || !_items.TryGetValue(link.LinkObject, out var source))
                {
                    continue;
                }

                var sourcePort = source.Info.Port(link.LinkPort);
                var power = port.Power || sourcePort?.Power == true;
                var view = new LinkView(source.Config.Name, link.LinkPort, item.Config.Name, port.Name, power, sld && power);
                view.Hit.ContextMenu = LinkMenu(item, port, text);
                Panel.SetZIndex(view.Path, power ? 0 : 2);
                Panel.SetZIndex(view.Hit, power ? 0 : 2);
                Sheet.Children.Add(view.Path);
                Sheet.Children.Add(view.Hit);
                _links.Add(view);
            }
        }

        // Groups: one named frame behind the members; dragging the frame title moves the whole group.
        foreach (var group in _items.Values.Where(item => item.Config.Group is not null).Select(item => item.Config.Group!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var title = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Padding = new Thickness(8, 2, 8, 3),
                Cursor = Cursors.SizeAll,
                ToolTip = "Drag to move the group",
                Child = new TextBlock { Text = group, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold }
            };
            var frame = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x60, 0xCB, 0xD5, 0xE1)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(7),
                Child = new DockPanel { LastChildFill = false, Children = { Dock(title) } }
            };
            var name = group;
            title.MouseLeftButtonDown += (_, e) => StartGroupDrag(name, e, title);
            title.MouseMove += (_, e) => DragGroup(e);
            title.MouseLeftButtonUp += (_, _) => EndGroupDrag(title);
            Panel.SetZIndex(frame, -1);
            Sheet.Children.Add(frame);
            _frames.Add((frame, group));
        }

        Sheet.Width = Math.Max(1600, maxX + 200);
        Sheet.Height = Math.Max(1000, maxY + 200);
        Dispatcher.BeginInvoke(UpdateLines, System.Windows.Threading.DispatcherPriority.Loaded);
        ShowDetails(_selected is not null && _items.ContainsKey(_selected) ? _selected : null);
        RefreshLive();
    }

    // ---- links ----

    private void ShowLinks_Changed(object sender, RoutedEventArgs e) => UpdateLines();

    private void UpdateLines()
    {
        foreach (var (frame, group) in _frames)
        {
            var members = _items.Values.Where(item => string.Equals(item.Config.Group, group, StringComparison.OrdinalIgnoreCase)).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            var left = members.Min(item => Canvas.GetLeft(item.Element)) - 12;
            var top = members.Min(item => Canvas.GetTop(item.Element)) - 34;
            var right = members.Max(item => Canvas.GetLeft(item.Element) + item.Element.ActualWidth) + 12;
            var bottom = members.Max(item => Canvas.GetTop(item.Element) + item.Element.ActualHeight) + 12;
            Canvas.SetLeft(frame, left);
            Canvas.SetTop(frame, top);
            frame.Width = Math.Max(60, right - left);
            frame.Height = Math.Max(60, bottom - top);
        }

        var showAll = ShowLinksToggle.IsChecked == true;
        foreach (var link in _links)
        {
            var from = _items[link.From];
            var to = _items[link.To];
            var selected = link.From.Equals(_selected, StringComparison.OrdinalIgnoreCase) || link.To.Equals(_selected, StringComparison.OrdinalIgnoreCase);
            // Power lines are the diagram and always shown; signal links only on request or for the selected object.
            var visible = link.Power || showAll || selected;
            link.Path.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            link.Hit.Visibility = link.Path.Visibility;
            if (!visible)
            {
                continue;
            }

            var end = to.Anchor(link.ToPort, null);
            var start = from.Anchor(link.FromPort, end);
            link.SetGeometry(start, end);
            if (!link.Power)
            {
                link.Path.Stroke = selected ? HighlightBrush : SignalLinkBrush;
                link.Path.StrokeThickness = selected ? 2 : 1.4;
            }
        }
    }

    private void StartWire(SheetItem item, PortDef port, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _wireFrom = (item, port);
        var start = item.Anchor(port.Name, null);
        _wireLine = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = start.X,
            Y2 = start.Y,
            Stroke = HighlightBrush,
            StrokeThickness = 2,
            StrokeDashArray = [4, 3],
            IsHitTestVisible = false
        };
        Panel.SetZIndex(_wireLine, 10);
        Sheet.Children.Add(_wireLine);
        Sheet.CaptureMouse();
    }

    private void Sheet_MouseMove(object sender, MouseEventArgs e)
    {
        if (_wireLine is null)
        {
            return;
        }

        var position = e.GetPosition(Sheet);
        _wireLine.X2 = position.X;
        _wireLine.Y2 = position.Y;
    }

    private void Sheet_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_wireFrom is not { } from)
        {
            return;
        }

        var position = e.GetPosition(Sheet);
        CancelWire();
        var hit = Sheet.InputHitTest(position) as DependencyObject;
        while (hit is not null and not Canvas && !(hit is FrameworkElement { Tag: PinTag }))
        {
            hit = VisualTreeHelper.GetParent(hit);
        }

        if (hit is FrameworkElement { Tag: PinTag tag } && _items.TryGetValue(tag.Object, out var target) && target.Info.Port(tag.Port) is { } targetPort)
        {
            Connect(from.Item, from.Port, target, targetPort);
        }
    }

    private void CancelWire()
    {
        if (_wireLine is not null)
        {
            Sheet.Children.Remove(_wireLine);
            _wireLine = null;
        }

        _wireFrom = null;
        if (Sheet.IsMouseCaptured)
        {
            Sheet.ReleaseMouseCapture();
        }
    }

    /// <summary>Links an output to an input (either can be dragged first): the input gets "@Object.Port".</summary>
    private void Connect(SheetItem a, PortDef aPort, SheetItem b, PortDef bPort)
    {
        if (ReferenceEquals(a, b) || aPort.Direction == bPort.Direction)
        {
            return;
        }

        var (source, sourcePort, target, targetPort) = aPort.Direction == PortDirection.Output ? (a, aPort, b, bPort) : (b, bPort, a, aPort);
        var text = $"@{source.Config.Name}.{sourcePort.Name}";
        if (target.Config.Ports.TryGetValue(targetPort.Name, out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            if (existing.Equals(text, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (MessageBox.Show(Window.GetWindow(this)!, $"{target.Config.Name}.{targetPort.Name} is connected to {existing}.\nReplace it with {text}?",
                    "Simulator", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
        }

        target.Config.Ports[targetPort.Name] = text;
        _selected ??= target.Config.Name;
        _controller?.Rebuild();
    }

    private ContextMenu PinMenu(SheetItem item, PortDef port)
    {
        var menu = new ContextMenu();
        if (port.Direction == PortDirection.Input)
        {
            var bound = item.Config.Ports.TryGetValue(port.Name, out var text) ? text : null;
            var disconnect = new MenuItem { Header = bound is null ? "Not connected" : $"Disconnect ({bound})", IsEnabled = bound is not null };
            disconnect.Click += (_, _) => Disconnect(item.Config, port.Name);
            menu.Items.Add(disconnect);
        }
        else
        {
            var prefix = $"@{item.Config.Name}.{port.Name}";
            var users = Model?.Objects.SelectMany(config => config.Ports.Where(pair => pair.Value.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(pair => (config, pair.Key))).ToList() ?? [];
            var signals = item.Config.Ports.TryGetValue(port.Name, out var targets) ? targets : null;
            menu.Items.Add(new MenuItem { Header = signals is null ? "No PLC signal (bind it in Program)" : $"PLC: {signals}", IsEnabled = false });
            var remove = new MenuItem { Header = $"Remove {users.Count} link(s) from this output", IsEnabled = users.Count > 0 };
            remove.Click += (_, _) =>
            {
                foreach (var (config, key) in users)
                {
                    config.Ports.Remove(key);
                }

                _controller?.Rebuild();
            };
            menu.Items.Add(remove);
        }

        return menu;
    }

    private ContextMenu LinkMenu(SheetItem target, PortDef port, string text)
    {
        var delete = new MenuItem { Header = $"Delete link {text} → {target.Config.Name}.{port.Name}" };
        delete.Click += (_, _) => Disconnect(target.Config, port.Name);
        return new ContextMenu { Items = { delete } };
    }

    private void Disconnect(ObjectConfig config, string port)
    {
        config.Ports.Remove(port);
        _controller?.Rebuild();
    }

    // ---- screens ----

    private void ScreenTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingScreens || ScreenTabs.SelectedItem is not string screen || screen == _screen)
        {
            return;
        }

        _screen = screen;
        _selected = null;
        Rebuild();
    }

    private void SldCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingScreens || Model is not { } model)
        {
            return;
        }

        model.SldScreens.RemoveAll(screen => screen.Equals(_screen, StringComparison.OrdinalIgnoreCase));
        if (SldCheck.IsChecked == true)
        {
            model.SldScreens.Add(_screen);
        }

        _controller?.MarkDirty();
        Rebuild();
    }

    private void NewScreen_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        var name = TextPrompt.Ask(Window.GetWindow(this)!, "New screen", "Name of the new SCADA screen (e.g. AC distribution):", "");
        if (string.IsNullOrWhiteSpace(name) || model.AllScreens().Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        model.Screens = model.AllScreens();
        model.Screens.Add(name);
        _screen = name;
        _selected = null;
        _controller?.MarkDirty();
        Rebuild();
    }

    private void RenameScreen_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        var old = _screen;
        var name = TextPrompt.Ask(Window.GetWindow(this)!, "Rename screen", $"New name of the screen '{old}':", old);
        if (string.IsNullOrWhiteSpace(name) || name == old)
        {
            return;
        }

        string Rename(string screen) => screen.Equals(old, StringComparison.OrdinalIgnoreCase) ? name : screen;
        var first = model.AllScreens()[0];
        model.Screens = model.AllScreens().Select(Rename).ToList();
        model.SldScreens = model.SldScreens.Select(Rename).ToList();
        foreach (var item in model.Objects)
        {
            if ((item.Screen ?? first).Equals(old, StringComparison.OrdinalIgnoreCase))
            {
                item.Screen = name;
            }

            foreach (var view in item.Views ?? [])
            {
                view.Screen = Rename(view.Screen);
            }
        }

        _screen = name;
        _controller?.MarkDirty();
        Rebuild();
    }

    private void DeleteScreen_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        if (model.Objects.Any(item => model.ScreenOf(item).Equals(_screen, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(Window.GetWindow(this)!, $"Screen '{_screen}' still has objects. Move them to another screen first (Screen in the object panel).",
                "Simulator", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var all = model.AllScreens();
        if (all.Count <= 1)
        {
            return;
        }

        // Pin the screen of every object first, so deleting the first screen does not move objects; extra views go.
        foreach (var item in model.Objects)
        {
            item.Screen = model.ScreenOf(item);
            item.Views?.RemoveAll(view => view.Screen.Equals(_screen, StringComparison.OrdinalIgnoreCase));
            if (item.Views is { Count: 0 })
            {
                item.Views = null;
            }
        }

        model.SldScreens.RemoveAll(screen => screen.Equals(_screen, StringComparison.OrdinalIgnoreCase));
        model.Screens = all.Where(screen => !screen.Equals(_screen, StringComparison.OrdinalIgnoreCase)).ToList();
        _screen = model.Screens[0];
        _controller?.MarkDirty();
        Rebuild();
    }

    private void ObjectScreenBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingScreens || Model is not { } model || _selected is null || model.Find(_selected) is not { } config
            || ObjectScreenBox.SelectedItem is not string screen || screen.Equals(model.ScreenOf(config), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var item in model.Objects)
        {
            item.Screen = model.ScreenOf(item);
        }

        model.Screens = model.AllScreens();
        config.Views?.RemoveAll(view => view.Screen.Equals(screen, StringComparison.OrdinalIgnoreCase));
        config.Screen = screen;
        _screen = screen;
        _controller?.MarkDirty();
        Rebuild();
    }

    /// <summary>"Also on": the object is shown on more screens (one object, several places).</summary>
    private void AlsoOn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string screen } box || Model is not { } model || _selected is null || model.Find(_selected) is not { } config)
        {
            return;
        }

        config.Views ??= [];
        config.Views.RemoveAll(view => view.Screen.Equals(screen, StringComparison.OrdinalIgnoreCase));
        if (box.IsChecked == true)
        {
            var (x, y) = model.PositionOn(config, _screen);
            config.Views.Add(new ObjectView { Screen = screen, X = x, Y = y });
        }

        if (config.Views.Count == 0)
        {
            config.Views = null;
        }

        _controller?.MarkDirty();
        Rebuild();
    }

    private static UIElement Dock(UIElement element)
    {
        DockPanel.SetDock(element, System.Windows.Controls.Dock.Top);
        return element;
    }

    // ---- moving objects and groups ----

    private void StartGroupDrag(string group, MouseButtonEventArgs e, UIElement handle)
    {
        _groupDrag = _items.Values.Where(item => string.Equals(item.Config.Group, group, StringComparison.OrdinalIgnoreCase)).Select(item => item.Config).ToList();
        _groupStart = _groupDrag.Select(config => new Point(Canvas.GetLeft(_items[config.Name].Element), Canvas.GetTop(_items[config.Name].Element))).ToList();
        _dragStart = e.GetPosition(Sheet);
        _dragMoved = false;
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void DragGroup(MouseEventArgs e)
    {
        if (_groupDrag is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(Sheet);
        var dx = Math.Round((position.X - _dragStart.X) / 10) * 10;
        var dy = Math.Round((position.Y - _dragStart.Y) / 10) * 10;
        // Keep the whole group on the sheet.
        dx = Math.Max(dx, -_groupStart.Min(point => point.X));
        dy = Math.Max(dy, -_groupStart.Min(point => point.Y) + 40);
        _dragMoved |= dx != 0 || dy != 0;
        for (var i = 0; i < _groupDrag.Count; i++)
        {
            Canvas.SetLeft(_items[_groupDrag[i].Name].Element, _groupStart[i].X + dx);
            Canvas.SetTop(_items[_groupDrag[i].Name].Element, _groupStart[i].Y + dy);
        }

        UpdateLines();
    }

    private void EndGroupDrag(UIElement handle)
    {
        if (_groupDrag is null)
        {
            return;
        }

        handle.ReleaseMouseCapture();
        if (_dragMoved && Model is { } model)
        {
            foreach (var config in _groupDrag)
            {
                model.SetPosition(config, _screen, Canvas.GetLeft(_items[config.Name].Element), Canvas.GetTop(_items[config.Name].Element));
            }

            _controller?.MarkDirty();
        }

        _groupDrag = null;
    }

    private void StartDrag(SheetItem item, MouseButtonEventArgs e)
    {
        _drag = item;
        _dragMoved = false;
        _dragStart = e.GetPosition(Sheet);
        _itemStart = new Point(Canvas.GetLeft(item.Element), Canvas.GetTop(item.Element));
        item.Element.CaptureMouse();
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
        Canvas.SetLeft(_drag.Element, Math.Max(0, Math.Round((_itemStart.X + dx) / 10) * 10));
        Canvas.SetTop(_drag.Element, Math.Max(0, Math.Round((_itemStart.Y + dy) / 10) * 10));
        UpdateLines();
    }

    private void EndDrag()
    {
        if (_drag is null)
        {
            return;
        }

        var item = _drag;
        _drag = null;
        item.Element.ReleaseMouseCapture();
        if (_dragMoved)
        {
            Model?.SetPosition(item.Config, _screen, Canvas.GetLeft(item.Element), Canvas.GetTop(item.Element));
            _controller?.MarkDirty();
        }
        else
        {
            ShowDetails(item.Config.Name);
            UpdateLines();
        }
    }

    private void Sheet_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == Sheet)
        {
            ShowDetails(null);
            UpdateLines();
        }
    }

    /// <summary>A variant changes which inputs/outputs and options the object has: rebuild and show it again.</summary>
    private void VariantChanged()
    {
        _controller?.Rebuild();
        var name = _selected;
        Dispatcher.BeginInvoke(() => ShowDetails(name));
    }

    private void ShowDetails(string? name)
    {
        _selected = name;
        foreach (var item in _items.Values)
        {
            item.SetSelected(item.Config.Name == name);
        }

        var instance = name is null ? null : _controller?.Engine.Find(_plc, name);
        if (instance is null || Model is not { } model)
        {
            TitleText.Text = "Select an object";
            SubtitleText.Text = "Click an object to set its parameters and force its outputs. Drag objects to arrange the sheet; " +
                                "drag from a pin to a pin to link them (right-click a pin or a link to remove it).";
            ObjectPanel.Visibility = Visibility.Collapsed;
            _forces = [];
            return;
        }

        TitleText.Text = instance.Name;
        SubtitleText.Text = (instance.Config.Group is null ? "" : $"[{instance.Config.Group}]  ") +
                            (string.IsNullOrWhiteSpace(instance.Config.Description) ? instance.Info.Title : $"{instance.Info.Title} — {instance.Config.Description}");
        ObjectPanel.Visibility = Visibility.Visible;
        _updatingScreens = true;
        var screens = model.AllScreens();
        var own = model.ScreenOf(instance.Config);
        ObjectScreenBox.ItemsSource = screens;
        ObjectScreenBox.SelectedItem = screens.FirstOrDefault(screen => screen.Equals(own, StringComparison.OrdinalIgnoreCase));
        AlsoOnList.ItemsSource = screens.Where(screen => !screen.Equals(own, StringComparison.OrdinalIgnoreCase))
            .Select(screen => new CheckBox
            {
                Content = screen,
                Tag = screen,
                Margin = new Thickness(0, 0, 10, 2),
                IsChecked = instance.Config.Views?.Any(view => view.Screen.Equals(screen, StringComparison.OrdinalIgnoreCase)) == true
            })
            .Select(box =>
            {
                box.Click += AlsoOn_Click;
                return box;
            })
            .ToList();
        _updatingScreens = false;
        OptionsList.ItemsSource = instance.Info.Options.Where(option => instance.Info.IsActive(option.ShownWhen, instance.Config))
            .Select(option => new OptionRow(option, instance.Config, VariantChanged)).ToList();
        ParamsGrid.ItemsSource = instance.Info.Params.Where(param => instance.Info.IsActive(param.ShownWhen, instance.Config))
            .Select(param => new ParamRow(param, instance.Config, () => _controller?.MarkDirty())).ToList();
        _forces = instance.Info.Ports.Where(port => port.Direction == PortDirection.Output && instance.Info.IsActive(port.ShownWhen, instance.Config))
            .Select(port => new ForceRow(port, instance, () => _items.GetValueOrDefault(instance.Name)?.Refresh(instance)))
            .ToList();
        ForcesGrid.ItemsSource = _forces;
        RefreshLive();
    }

    // ---- sheet items ----

    /// <summary>Marks a pin element: which object and port it belongs to.</summary>
    internal sealed record PinTag(string Object, string Port);

    /// <summary>A link drawn between two pins (plus a wide invisible path to click it).</summary>
    private sealed class LinkView
    {
        public LinkView(string from, string fromPort, string to, string toPort, bool power, bool orthogonal)
        {
            From = from;
            FromPort = fromPort;
            To = to;
            ToPort = toPort;
            Power = power;
            Orthogonal = orthogonal;
            Path = new Path { Stroke = power ? DeadBrush : SignalLinkBrush, StrokeThickness = power ? 3 : 1.4, IsHitTestVisible = false };
            Hit = new Path { Stroke = Brushes.Transparent, StrokeThickness = 9, Cursor = Cursors.Hand, ToolTip = $"{from}.{fromPort} → {to}.{toPort}" };
        }

        public string From { get; }
        public string FromPort { get; }
        public string To { get; }
        public string ToPort { get; }
        public bool Power { get; }
        public bool Orthogonal { get; }
        public Path Path { get; }
        public Path Hit { get; }

        public void SetGeometry(Point start, Point end)
        {
            var figure = new PathFigure { StartPoint = start };
            if (Orthogonal)
            {
                // SLD: down from the source, across, down into the target.
                var middle = start.Y + Math.Max(10, (end.Y - start.Y) / 2);
                figure.Segments.Add(new PolyLineSegment([new Point(start.X, middle), new Point(end.X, middle), end], true));
            }
            else
            {
                var bend = Math.Max(40, Math.Abs(end.X - start.X) / 2);
                figure.Segments.Add(new BezierSegment(new Point(start.X + bend, start.Y), new Point(end.X - bend, end.Y), end, true));
            }

            var geometry = new PathGeometry([figure]);
            Path.Data = geometry;
            Hit.Data = geometry;
        }
    }

    /// <summary>An object on the sheet: a tile or an SLD symbol, with pins to link.</summary>
    internal abstract class SheetItem(ObjectConfig config, ObjectTypeInfo info)
    {
        public ObjectConfig Config => config;
        public ObjectTypeInfo Info => info;
        public abstract FrameworkElement Element { get; }

        /// <summary>Pin elements by port name.</summary>
        public Dictionary<string, FrameworkElement> PinElements { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<(PortDef Port, FrameworkElement Pin)> Pins =>
            PinElements.Select(pair => (Info.Port(pair.Key)!, pair.Value)).Where(pair => pair.Item1 is not null);

        public abstract void Refresh(SimObject? instance);

        public abstract void SetSelected(bool selected);

        /// <summary>Sheet position where a link to/from the port attaches (<paramref name="towards"/> = other end, for busbars).</summary>
        public virtual Point Anchor(string port, Point? towards)
        {
            var left = Canvas.GetLeft(Element);
            var top = Canvas.GetTop(Element);
            if (PinElements.TryGetValue(port, out var pin) && pin.IsLoaded && pin.ActualWidth > 0)
            {
                var center = pin.TranslatePoint(new Point(pin.ActualWidth / 2, pin.ActualHeight / 2), Element);
                return new Point(left + center.X, top + center.Y);
            }

            return new Point(left + Element.ActualWidth / 2, top + Element.ActualHeight / 2);
        }

        protected FrameworkElement Pin(PortDef port, FrameworkElement element, string tooltip)
        {
            element.Tag = new PinTag(Config.Name, port.Name);
            element.Cursor = Cursors.Cross;
            element.ToolTip = tooltip;
            PinElements[port.Name] = element;
            return element;
        }
    }

    /// <summary>An object as a tile: header, input pins left, output pins right, values and active states.</summary>
    private sealed class Tile : SheetItem
    {
        private const double Width = 230;
        private readonly StackPanel _values = new();
        private readonly WrapPanel _states = new() { Margin = new Thickness(0, 4, 0, 0) };
        private readonly Color _color;
        private readonly Border _border;

        public Tile(ObjectConfig config, ObjectTypeInfo info, Color color, ProcessModel model) : base(config, info)
        {
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

            var ports = info.Ports.Where(port => info.IsActive(port.ShownWhen, config)).ToList();
            var linkedOutputs = model.Objects.SelectMany(item => item.Ports.Values)
                .Select(text => PortBinding.Parse(text)).Where(binding => binding.Kind == BindingKind.Link && binding.LinkObject.Equals(config.Name, StringComparison.OrdinalIgnoreCase))
                .Select(binding => binding.LinkPort).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var inputs = new StackPanel();
            var outputs = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var port in ports)
            {
                config.Ports.TryGetValue(port.Name, out var text);
                var kind = PortBinding.Parse(text).Kind;
                var fill = kind switch
                {
                    BindingKind.Signal => Color.FromRgb(0x25, 0x63, 0xEB),
                    BindingKind.Link => Color.FromRgb(0x47, 0x55, 0x69),
                    BindingKind.Constant => Color.FromRgb(0xF5, 0x9E, 0x0B),
                    _ => port.Direction == PortDirection.Output && linkedOutputs.Contains(port.Name) ? Color.FromRgb(0x47, 0x55, 0x69) : Colors.White
                };
                var dot = new Border
                {
                    Width = 11,
                    Height = 11,
                    CornerRadius = new CornerRadius(port.Power ? 1 : 6),
                    Background = new SolidColorBrush(fill),
                    BorderBrush = port.Power ? LiveBrush : new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
                    BorderThickness = new Thickness(port.Power ? 2 : 1.2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var label = new TextBlock
                {
                    Text = port.Name,
                    FontSize = 10.5,
                    Margin = new Thickness(4, 0, 4, 0),
                    Foreground = port.Power ? LiveBrush : Brushes.DimGray,
                    FontWeight = port.Power ? FontWeights.SemiBold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var tooltip = $"{port.Name} ({(port.Direction == PortDirection.Input ? "input" : "output")}) — {port.Description}" +
                              (string.IsNullOrWhiteSpace(text) ? "" : $"\nBinding: {text}") +
                              "\nDrag to another pin to link; right-click to disconnect.";
                Pin(port, dot, tooltip);
                if (port.Direction == PortDirection.Input)
                {
                    dot.Margin = new Thickness(-6, 1, 0, 1);
                    inputs.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, label } });
                }
                else
                {
                    dot.Margin = new Thickness(0, 1, -6, 1);
                    outputs.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { label, dot } });
                }
            }

            var pins = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            pins.ColumnDefinitions.Add(new ColumnDefinition());
            pins.ColumnDefinitions.Add(new ColumnDefinition());
            pins.Children.Add(inputs);
            Grid.SetColumn(outputs, 1);
            pins.Children.Add(outputs);

            _border = new Border
            {
                Width = Width,
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
                        pins,
                        new StackPanel { Margin = new Thickness(8, 2, 8, 6), Children = { _values, _states } }
                    }
                }
            };
        }

        public override FrameworkElement Element => _border;

        public override void SetSelected(bool selected)
        {
            _border.BorderThickness = new Thickness(selected ? 3 : 1.5);
            _border.Effect = selected ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Opacity = 0.5, Color = _color } : null;
        }

        public override void Refresh(SimObject? instance)
        {
            _values.Children.Clear();
            _states.Children.Clear();
            if (instance is null)
            {
                return;
            }

            foreach (var name in Info.ScadaValues)
            {
                var port = Info.Port(name);
                if (port is null || !Info.IsActive(port.ShownWhen, Config))
                {
                    continue;
                }

                var forced = instance.Forces.ContainsKey(name);
                var value = instance.Value(name).ToString(Math.Abs(instance.Value(name)) >= 100 ? "0.#" : "0.##", CultureInfo.InvariantCulture);
                var row = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
                row.Children.Add(new TextBlock { Text = name, Foreground = Brushes.DimGray, FontSize = 11 });
                var text = new TextBlock { Text = $"{value} {port.Unit}".Trim(), FontWeight = FontWeights.SemiBold, FontSize = 11.5, Foreground = forced ? Brushes.DarkOrange : Brushes.Black };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                _values.Children.Add(row);
            }

            foreach (var port in Info.Ports.Where(port => port.Direction == PortDirection.Output && port.Type == PortType.Bool && !port.Power && Info.IsActive(port.ShownWhen, Config)))
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
    }
}
