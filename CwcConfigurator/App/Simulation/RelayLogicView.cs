using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CwcConfigurator.Services.Eplan;
using CwcConfigurator.Services.Simulation;
using CwcConfigurator.Services.Simulation.Process;

namespace CwcConfigurator.App.Simulation;

/// <summary>
/// Relay-logic sheets of one PLC, drawn like a circuit diagram page: current paths between L+ (top) and M (bottom),
/// changeover contacts (common / NC / NO terminals) with tag, cross reference and function text, and the relay coils
/// at the bottom with their contact mirror. Paths are edited by selecting an element and adding a contact below it
/// (series) or beside it (parallel); the properties are on the right. While the simulation runs, the wires that
/// carry current, the operated contacts and the picked-up relays are coloured.
/// </summary>
public sealed class RelayLogicView : UserControl
{
    private const double ColumnWidth = 190;
    private const double RowHeight = 118;
    private const double WireOffset = 128; // wire x inside a column; tag and texts are to its left
    private const double RailTop = 46;
    private const double PathTop = 70;
    private const double CoilHeight = 44;
    private const double CoilWidth = 28;
    private const double LeftMargin = 24;

    // Working area at least an A3 landscape page (420 × 297 mm at 4 px/mm).
    private const double PageWidth = 1680;
    private const double PageHeight = 1188;

    private static readonly Brush WireBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)));
    private static readonly Brush LiveBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)));
    private static readonly Brush OperatedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)));
    private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)));
    private static readonly Brush SelectBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x3B, 0x82, 0xF6)));
    private static readonly Brush CoilOnBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xBB, 0xF7, 0xD0)));
    private static readonly Brush CoilTimingBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFE, 0xF0, 0x8A)));

    private readonly Canvas _sheet = new() { Background = Brushes.White };
    private readonly ComboBox _sheets = new() { Width = 200, Margin = new Thickness(0, 0, 6, 0) };
    private readonly StackPanel _properties = new() { Margin = new Thickness(10) };
    private readonly TextBlock _status = new() { Foreground = DimBrush, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    private SimulationController? _controller;
    private string _plc = "";
    private int _sheetIndex;
    private object? _selected; // RelayElement, RelayCoil, RelaySwitch or RelaySheet
    private bool _loadingSheets;
    private bool _dry; // layout pass: positions only, nothing drawn
    private ScrollViewer _scroll = null!;
    private readonly ScaleTransform _zoom = new(1, 1);
    private Point? _panStart;
    private Point _panOffset;
    private bool _panned;
    private readonly Dictionary<string, (double X, double Bottom)> _positions = new(StringComparer.OrdinalIgnoreCase);

    public RelayLogicView()
    {
        var toolbar = new WrapPanel { Margin = new Thickness(8, 5, 8, 5) };
        toolbar.Children.Add(new TextBlock { Text = "Sheet", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _sheets.SelectionChanged += (_, _) =>
        {
            if (!_loadingSheets && _sheets.SelectedIndex >= 0)
            {
                _sheetIndex = _sheets.SelectedIndex;
                _selected = Sheet;
                Redraw();
                ShowProperties();
            }
        };
        toolbar.Children.Add(_sheets);
        toolbar.Children.Add(ToolButton("New sheet", "Add a relay-logic sheet.", (_, _) => NewSheet()));
        toolbar.Children.Add(ToolButton("Import EPLAN…", "Build relay-logic sheets from the schematic pages of an EPLAN export (.epj): contacts, coils, branches and selector switches.", async (_, _) => await ImportEplanAsync()));
        toolbar.Children.Add(ToolButton("Import EPLAN power…", "Build the power part from the EPLAN distribution pages: an SLD screen (busbar, breakers, contactors following their relays, motors) and an overview sheet of the feeders.", async (_, _) => await ImportEplanPowerAsync()));
        toolbar.Children.Add(ToolButton("Copy sheet", "Copy this sheet for the same circuit with other relays and signals (find / replace rules).", (_, _) => CopySheet()));
        toolbar.Children.Add(ToolButton("Remove duplicates", "Sheets imported twice (same name and page): keep the last import, delete the older copies — a relay must be drawn once.", (_, _) => RemoveDuplicateSheets()));
        toolbar.Children.Add(ToolButton("Delete sheet", "Delete this sheet with all its paths.", (_, _) => DeleteSheet()));
        toolbar.Children.Add(new Separator { Width = 14, Opacity = 0 });
        toolbar.Children.Add(ToolButton("New path", "Add a current path (one contact and a relay coil) at the right.", (_, _) => NewPath()));
        toolbar.Children.Add(ToolButton("Contact below", "Add a contact in series below the selected contact (or above the selected coil).", (_, _) => AddSeries()));
        toolbar.Children.Add(ToolButton("Path from here", "Start a new path below the selected contact: the current branches off there (e.g. after a switch, one path per position) to its own coil.", (_, _) => PathFromHere()));
        toolbar.Children.Add(ToolButton("Contact beside", "Add a contact in parallel to the selected contact or parallel group.", (_, _) => AddParallel()));
        toolbar.Children.Add(ToolButton("Delete", "Delete the selected contact (or the whole path when the coil is selected). Del key.", (_, _) => DeleteSelected()));
        toolbar.Children.Add(ToolButton("Switch contact", "Make the selected contact a contact of a selector switch (2 or 3 positions), or add one below the selected element. Turn the switch by clicking its knob on the sheet.", (_, _) => SwitchContact()));
        toolbar.Children.Add(ToolButton("◀", "Move the selected path to the left.", (_, _) => MovePath(-1)));
        toolbar.Children.Add(ToolButton("▶", "Move the selected path to the right.", (_, _) => MovePath(1)));
        toolbar.Children.Add(_status);

        var canvasHost = _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _sheet,
            Background = Brushes.White,
            Focusable = true
        };
        canvasHost.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && _selected is RelayElement or RelayCoil)
            {
                DeleteSelected();
                e.Handled = true;
            }
        };
        // Pan: drag the empty sheet with the left button, or anywhere with the middle button. Zoom: Ctrl + wheel.
        _sheet.LayoutTransform = _zoom;
        _sheet.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == _sheet)
            {
                StartPan(e);
            }

            canvasHost.Focus();
        };
        canvasHost.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                StartPan(e);
                e.Handled = true;
            }
        };
        canvasHost.PreviewMouseMove += (_, e) =>
        {
            if (_panStart is not { } start)
            {
                return;
            }

            var position = e.GetPosition(canvasHost);
            var delta = position - start;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 4)
            {
                _panned = true;
            }

            canvasHost.ScrollToHorizontalOffset(_panOffset.X - delta.X);
            canvasHost.ScrollToVerticalOffset(_panOffset.Y - delta.Y);
        };
        canvasHost.PreviewMouseUp += (_, e) =>
        {
            if (_panStart is null)
            {
                return;
            }

            _panStart = null;
            canvasHost.ReleaseMouseCapture();
            canvasHost.Cursor = null;
            if (!_panned && e.ChangedButton == MouseButton.Left)
            {
                Select(Sheet); // a click (not a drag) on the empty sheet selects the sheet
            }
        };
        canvasHost.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control)
            {
                return;
            }

            var scale = Math.Clamp(_zoom.ScaleX * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.3, 2.5);
            _zoom.ScaleX = scale;
            _zoom.ScaleY = scale;
            e.Handled = true;
        };

        var left = new DockPanel();
        var toolbarBorder = new Border { Child = toolbar, BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)), BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.White };
        DockPanel.SetDock(toolbarBorder, Dock.Top);
        left.Children.Add(toolbarBorder);
        left.Children.Add(canvasHost);

        var right = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB)),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _properties }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        Content = grid;
    }

    private void StartPan(MouseButtonEventArgs e)
    {
        _panStart = e.GetPosition(_scroll);
        _panOffset = new Point(_scroll.HorizontalOffset, _scroll.VerticalOffset);
        _panned = false;
        _scroll.CaptureMouse();
        _scroll.Cursor = Cursors.ScrollAll;
    }

    public void Attach(SimulationController controller)
    {
        _controller = controller;
        controller.ModelChanged += () =>
        {
            LoadSheets();
            Redraw();
        };
    }

    public void ShowPlc(string plc)
    {
        _plc = plc;
        _sheetIndex = 0;
        LoadSheets();
        _selected = Sheet;
        LinkPlcOutputs();
        Redraw();
        ShowProperties();
    }

    /// <summary>PLC digital outputs drive their output relays (2-K…) and input relays (1-K…) drive their inputs from the start (only empty sources are filled).</summary>
    private void LinkPlcOutputs()
    {
        if (Model is { } model && _controller?.Session(_plc)?.Project is { } project && RelayPlcSignals.Link(model, project.DigitalOutputs, project.DigitalInputs) > 0)
        {
            Changed();
        }
    }

    /// <summary>Live colours (called by the window timer).</summary>
    public void RefreshLive()
    {
        if (IsVisible)
        {
            Redraw();
        }
    }

    private ProcessModel? Model => _controller is not null && _controller.Models.TryGetValue(_plc, out var model) ? model : null;

    private RelaySheet? Sheet => Model is { } model && _sheetIndex >= 0 && _sheetIndex < model.RelaySheets.Count ? model.RelaySheets[_sheetIndex] : null;

    private RelayLogicRunner? Relays => _controller?.Engine.Relays;

    // ---------- sheets and editing ----------

    private void LoadSheets()
    {
        _loadingSheets = true;
        try
        {
            var names = Model?.RelaySheets.Select((sheet, index) => $"{(sheet.Page > 0 ? sheet.Page : index + 1)}  {sheet.Name}").ToList() ?? [];
            _sheets.ItemsSource = names;
            _sheetIndex = Math.Clamp(_sheetIndex, 0, Math.Max(0, names.Count - 1));
            _sheets.SelectedIndex = names.Count == 0 ? -1 : _sheetIndex;
        }
        finally
        {
            _loadingSheets = false;
        }
    }

    private void NewSheet()
    {
        if (Model is not { } model)
        {
            return;
        }

        model.RelaySheets.Add(new RelaySheet { Name = "Relay logic " + (model.RelaySheets.Count + 1) });
        _sheetIndex = model.RelaySheets.Count - 1;
        _selected = Sheet;
        Changed();
        ShowProperties();
    }

    /// <summary>
    /// Copies the sheet with replacement rules (prefilled with every tag and signal of the sheet as "x=x", so only the
    /// right side needs editing). Switches of renamed switch contacts are copied with their positions.
    /// </summary>
    /// <summary>
    /// Relay logic from an EPLAN export: choose the .epj and the schematic pages; each page becomes a sheet (its
    /// coils' paths, branches, selector switches). Sources of contacts that are no relay of the sheets are set later.
    /// </summary>
    private async Task ImportEplanAsync()
    {
        if (Model is not { } model)
        {
            return;
        }

        var open = new Microsoft.Win32.OpenFileDialog { Filter = "EPLAN export (*.epj)|*.epj|All files (*.*)|*.*", Title = "EPLAN project export" };
        if (open.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        List<EplanSchematicPage> pages;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            _status.Text = "Reading the EPLAN pages…";
            pages = await Task.Run(() => EplanRelayImporter.ListPages(open.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "The EPLAN export could not be read: " + ex.Message, "Import EPLAN", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _status.Text = "";
        }

        if (pages.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this)!, "No page of this export has relay coils or contacts.", "Import EPLAN", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Pages with coils are ticked: each becomes a sheet.
        var boxes = pages.Select(page => new CheckBox
        {
            Content = $"{page.Title}   ({page.Coils} coil(s), {page.Contacts} contact(s))",
            IsChecked = page.Coils > 0,
            Tag = page.Index,
            Margin = new Thickness(0, 2, 0, 2)
        }).ToList();
        var list = new StackPanel();
        boxes.ForEach(box => list.Children.Add(box));
        var ok = new Button { Content = "Import", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var panel = new DockPanel { Margin = new Thickness(14) };
        var header = new TextBlock
        {
            Text = $"{System.IO.Path.GetFileName(open.FileName)} — tick the schematic pages to import. Each page becomes a relay-logic sheet; " +
                   "the wires are rebuilt from the drawing (EPLAN autoconnect).",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var dialog = new Window
        {
            Title = "Import relay logic from EPLAN",
            Content = panel,
            Width = 720,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this)
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var chosen = boxes.Where(box => box.IsChecked == true).Select(box => (int)box.Tag).ToHashSet();
        if (chosen.Count == 0)
        {
            return;
        }

        EplanRelayImport import;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            import = await Task.Run(() => EplanRelayImporter.Import(open.FileName, chosen));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "The import failed: " + ex.Message, "Import EPLAN", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        foreach (var selector in import.Switches.Where(selector => !model.RelaySwitches.Any(item => item.Tag.Trim().Equals(selector.Tag.Trim(), StringComparison.OrdinalIgnoreCase))))
        {
            model.RelaySwitches.Add(selector);
        }

        // Pages imported before are replaced (a relay must not be drawn twice); older duplicates are removed.
        var importedPages = import.Sheets.Where(sheet => sheet.Page > 0).Select(sheet => sheet.Page).ToHashSet();
        var existing = model.RelaySheets.Where(sheet => importedPages.Contains(sheet.Page)).ToList();
        var first = existing.Count > 0 ? model.RelaySheets.IndexOf(existing[0]) : model.RelaySheets.Count;
        existing.ForEach(sheet => model.RelaySheets.Remove(sheet));
        first = Math.Min(first, model.RelaySheets.Count);
        model.RelaySheets.InsertRange(first, import.Sheets);
        var removed = existing.Count + RemoveDuplicates(model);

        // Operator devices of the project (push buttons, mushrooms, switches, lamps) on a cubicle door, to be arranged.
        var onDoor = CubicleLayout.AddDevices(model, import.CubicleDevices, "EPLAN devices");
        var linked = _controller?.Session(_plc)?.Project is { } project ? RelayPlcSignals.Link(model, project.DigitalOutputs, project.DigitalInputs) : 0;
        _sheetIndex = import.Sheets.Count > 0 ? first : _sheetIndex;
        _selected = Sheet;
        Changed();
        ShowProperties();

        var paths = import.Sheets.Sum(sheet => sheet.Circuits.Count);
        var message = $"Imported {import.Sheets.Count} sheet(s) with {paths} path(s) and {import.Switches.Count} selector switch(es); " +
                      $"{linked} contact(s) / coil(s) connected to their PLC input or output.";
        if (removed > 0)
        {
            message += $" {removed} older sheet(s) with the same pages were replaced.";
        }

        if (onDoor > 0)
        {
            message += $" {onDoor} push button(s), switch(es) and lamp(s) were put on the cubicle 'EPLAN devices' (Cubicle tab) — arrange them there.";
        }
        if (import.Warnings.Count > 0)
        {
            message += "\n\nTo check:\n" + string.Join("\n", import.Warnings.Take(25).Select(warning => "• " + warning))
                       + (import.Warnings.Count > 25 ? $"\n… and {import.Warnings.Count - 25} more." : "");
        }

        message += "\n\nDevices that are not on the imported sheets (breakers, field contacts) need their input: select a contact and fill 'Device input'.";
        MessageBox.Show(Window.GetWindow(this)!, message, "Import EPLAN", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// The power part of an EPLAN project: simulator objects on an SLD screen (busbar, breakers / motor protection,
    /// contactors commanded by their relay coils, motors) linked by their supply, an overview sheet with one path per
    /// feeder, and the auxiliary contacts of breakers in the relay logic connected to them.
    /// </summary>
    private async Task ImportEplanPowerAsync()
    {
        if (Model is not { } model)
        {
            return;
        }

        var open = new Microsoft.Win32.OpenFileDialog { Filter = "EPLAN export (*.epj)|*.epj|All files (*.*)|*.*", Title = "EPLAN project export (power part)" };
        if (open.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        List<EplanSchematicPage> pages;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            pages = await Task.Run(() => EplanPowerImporter.ListPages(open.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "The EPLAN export could not be read: " + ex.Message, "Import EPLAN power", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (pages.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this)!, "No page of this export has power devices (breakers, contactors, motors).", "Import EPLAN power", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var boxes = pages.Select(page => new CheckBox { Content = $"{page.Title}   ({page.Contacts} power device(s))", IsChecked = true, Tag = page.Index, Margin = new Thickness(0, 2, 0, 2) }).ToList();
        if (!ChoosePages("Import power part from EPLAN",
                "Tick the power pages (the busbars L1/L2/L3 run through them). An SLD screen and an overview sheet are created; contactors follow the relays with their tag.",
                boxes))
        {
            return;
        }

        var chosen = boxes.Where(box => box.IsChecked == true).Select(box => (int)box.Tag).ToHashSet();
        EplanPowerImport import;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            import = await Task.Run(() => EplanPowerImporter.Import(open.FileName, chosen));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "The import failed: " + ex.Message, "Import EPLAN power", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (import.Devices.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this)!, "No power device was reached from the busbar L1 on these pages.", "Import EPLAN power", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // SLD screen with the simulator objects.
        var screen = "Power (EPLAN)";
        for (var i = 2; model.AllScreens().Contains(screen, StringComparer.OrdinalIgnoreCase); i++)
        {
            screen = $"Power (EPLAN) {i}";
        }

        var coils = model.RelaySheets.SelectMany(sheet => sheet.Circuits).Select(circuit => circuit.Coil.Tag.Trim()).Where(tag => tag.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var objects = EplanPowerBuilder.Build(import.Devices, screen, coils, model.Objects.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase), out var names);
        model.Objects.AddRange(objects);
        model.Screens.Add(screen);
        model.SldScreens.Add(screen);

        // State of a power device for a contact: breakers On, contactors their relay (or their state).
        var kinds = import.Devices.ToDictionary(device => device.Id, device => device.Kind, StringComparer.OrdinalIgnoreCase);
        string SourceOf(string key, string tag) =>
            !names.TryGetValue(key, out var name) || !kinds.TryGetValue(key, out var kind) ? ""
            : kind == PowerDeviceKind.Contactor ? (coils.Contains(tag.Trim()) ? "" : $"@{name}.State")
            : kind == PowerDeviceKind.Motor ? $"@{name}.Running"
            : $"@{name}.On";

        // Overview sheet: one path per feeder (single line), the load at its end.
        var overview = new RelaySheet { Name = "Power distribution (EPLAN)" };
        foreach (var (_, chain, load) in import.Feeders.OrderBy(feeder => int.TryParse(feeder.Page, out var number) ? number : 0))
        {
            var elements = new List<RelayElement>();
            foreach (var item in chain)
            {
                var source = SourceOf(item.Key, item.Tag);
                if (item.Tag.Trim().Length == 0)
                {
                    elements.Add(new RelayElement { Tag = source, Contact = "NO", Pins = "1//2" });
                    continue;
                }

                if (source.Length > 0)
                {
                    RelayDevices.Define(model, item.Tag, source);
                }

                elements.Add(new RelayElement { Tag = item.Tag, Contact = "NO", Pins = "1//2" });
            }

            overview.Circuits.Add(new RelayCircuit { Elements = elements.Count > 0 ? elements : [new RelayElement { Tag = "=1" }], Coil = new RelayCoil { Text = load } });
        }

        model.RelaySheets.Add(overview);

        // Auxiliary contacts of breakers / motor protection in the relay logic (tag, no source yet) follow them.
        var byTag = import.Devices.Where(device => device.Kind != PowerDeviceKind.Contactor)
            .GroupBy(device => device.Tag.Trim(), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var auxiliary = 0;
        foreach (var contact in model.RelaySheets.Where(sheet => sheet != overview).SelectMany(sheet => sheet.Circuits).SelectMany(circuit => RelayLogicRunner.AllElements(circuit.Elements))
                     .Where(element => !element.IsParallel && !coils.Contains(element.Tag.Trim())))
        {
            if (byTag.TryGetValue(contact.Tag.Trim(), out var device) && SourceOf(device.Id, device.Tag) is { Length: > 0 } source
                && RelayDevices.Define(model, contact.Tag, source))
            {
                auxiliary++;
            }
        }

        _sheetIndex = model.RelaySheets.Count - 1;
        _selected = Sheet;
        Changed();
        ShowProperties();

        var message = $"Power part imported: {objects.Count} object(s) on the SLD screen '{screen}' (SCADA tab), " +
                      $"{import.Feeders.Count} feeder(s) on the sheet '{overview.Name}', {auxiliary} auxiliary contact(s) connected.\n\n" +
                      $"Contactors follow the relay coils with their tag ({objects.Count(item => item.Ports.ContainsKey("Command"))} connected); turn breakers on/off on the SLD.";
        if (import.Warnings.Count > 0)
        {
            message += "\n\nTo check:\n" + string.Join("\n", import.Warnings.Take(15).Select(warning => "• " + warning));
        }

        MessageBox.Show(Window.GetWindow(this)!, message, "Import EPLAN power", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Page selection dialog (tick boxes); true when confirmed.</summary>
    private bool ChoosePages(string title, string text, List<CheckBox> boxes)
    {
        var list = new StackPanel();
        boxes.ForEach(box => list.Children.Add(box));
        var ok = new Button { Content = "Import", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var panel = new DockPanel { Margin = new Thickness(14) };
        var header = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var dialog = new Window
        {
            Title = title,
            Content = panel,
            Width = 720,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this)
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true && boxes.Any(box => box.IsChecked == true);
    }

    /// <summary>Older copies of sheets that are in the model twice (same name and page).</summary>
    private static List<RelaySheet> Duplicates(ProcessModel model) => model.RelaySheets
        .GroupBy(sheet => (sheet.Name.Trim().ToUpperInvariant(), sheet.Page))
        .Where(group => group.Count() > 1)
        .SelectMany(group => group.Take(group.Count() - 1))
        .ToList();

    private static int RemoveDuplicates(ProcessModel model)
    {
        var duplicates = Duplicates(model);
        duplicates.ForEach(sheet => model.RelaySheets.Remove(sheet));
        return duplicates.Count;
    }

    /// <summary>Sheets with the same name and page (an EPLAN page imported twice): the last one is kept.</summary>
    private void RemoveDuplicateSheets()
    {
        if (Model is not { } model)
        {
            return;
        }

        var duplicates = Duplicates(model);
        if (duplicates.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this)!, "No sheet is in the model twice.", "Remove duplicates", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var list = string.Join("\n", duplicates.Select(sheet => $"• {sheet.Name} (page {sheet.Page})").Take(30));
        if (MessageBox.Show(Window.GetWindow(this)!,
                $"{duplicates.Count} older copy(ies) of sheets that are in the model twice will be deleted (the last import of each is kept):\n\n{list}",
                "Remove duplicates", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        duplicates.ForEach(sheet => model.RelaySheets.Remove(sheet));
        _sheetIndex = Math.Clamp(_sheetIndex, 0, Math.Max(0, model.RelaySheets.Count - 1));
        _selected = Sheet;
        Changed();
        ShowProperties();
    }

    private void CopySheet()
    {
        if (Model is not { } model || Sheet is not { } sheet)
        {
            return;
        }

        var items = new List<string>();
        void Collect(IEnumerable<RelayElement> series)
        {
            foreach (var element in series)
            {
                if (element.IsParallel)
                {
                    element.Branches.ForEach(Collect);
                    continue;
                }

                items.AddRange(element.Tag.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                if (Model is { } current && RelayDevices.Find(current, element.Tag) is { } device)
                {
                    items.AddRange(device.Input.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                }
            }
        }

        foreach (var circuit in sheet.Circuits)
        {
            items.Add(circuit.Coil.Tag.Trim());
            Collect(circuit.Elements);
            items.AddRange(circuit.Coil.Outputs.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }

        var rules = string.Join(Environment.NewLine, items.Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.StartsWith('-') ? 0 : 1).ThenBy(item => item, StringComparer.OrdinalIgnoreCase)
            .Select(item => item + "=" + item));

        var nameBox = new TextBox { Text = sheet.Name + " (copy)", MinHeight = 24, Margin = new Thickness(0, 4, 0, 8) };
        var rulesBox = new TextBox
        {
            Text = rules,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"),
            Height = 320
        };
        var ok = new Button { Content = "Copy", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "Name of the new sheet", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock
        {
            Text = "Replace (old=new, one per line). Edit the right side of the tags and signals you want different, e.g. -K107=-K207, DI:64=DI:84.\n" +
                   "Prefix rule: -K1*=-K2* (every tag starting with -K1). Text rule: GATE 1=GATE 2 (replaced in all texts and the sheet name).",
            TextWrapping = TextWrapping.Wrap,
            Foreground = DimBrush,
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(rulesBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = $"Copy sheet '{sheet.Name}'",
            Content = panel,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this)
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var parsed = RelaySheetCopy.ParseRules(rulesBox.Text);
        var copy = RelaySheetCopy.Copy(sheet, parsed);
        copy.Name = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : copy.Name;
        RelaySheetCopy.CopyDevices(model, copy, parsed);

        // Switch contacts that got a new tag: a copy of the switch with the same positions.
        foreach (var selector in model.RelaySwitches.ToList())
        {
            var tag = RelaySheetCopy.ReplaceItem(selector.Tag, parsed);
            if (!tag.Equals(selector.Tag.Trim(), StringComparison.OrdinalIgnoreCase)
                && !model.RelaySwitches.Any(item => item.Tag.Trim().Equals(tag, StringComparison.OrdinalIgnoreCase))
                && copy.Circuits.SelectMany(circuit => AllContacts(circuit.Elements)).Any(contact => contact.Tag.Trim().Equals(tag, StringComparison.OrdinalIgnoreCase)))
            {
                model.RelaySwitches.Add(new RelaySwitch { Tag = tag, Text = selector.Text, Positions = selector.Positions, Position = selector.Position });
            }
        }

        model.RelaySheets.Insert(_sheetIndex + 1, copy);
        _sheetIndex++;
        _selected = copy;
        Changed();
        ShowProperties();
    }

    private void DeleteSheet()
    {
        if (Model is not { } model || Sheet is not { } sheet)
        {
            return;
        }

        if (MessageBox.Show(Window.GetWindow(this)!, $"Delete the sheet '{sheet.Name}' with its {sheet.Circuits.Count} path(s)?", "Relay logic",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        model.RelaySheets.Remove(sheet);
        _sheetIndex = Math.Max(0, _sheetIndex - 1);
        _selected = Sheet;
        Changed();
        ShowProperties();
    }

    /// <summary>
    /// The selected contact becomes a contact of a selector switch (a new switch, or the existing one with its tag);
    /// with a coil or nothing selected, a new switch contact is added. The switch itself is drawn at its contacts.
    /// </summary>
    private void SwitchContact()
    {
        if (Model is not { } model)
        {
            return;
        }

        if (_selected is not RelayElement { IsParallel: false } contact)
        {
            if (_selected is RelayCoil)
            {
                AddSeries();
            }
            else
            {
                NewPath();
                _selected = Sheet?.Circuits.LastOrDefault()?.Elements.FirstOrDefault();
            }

            if (_selected is not RelayElement { IsParallel: false } added)
            {
                return;
            }

            contact = added;
            contact.Tag = "";
        }

        if (Relays?.SwitchOf(_plc, contact.Tag) is null)
        {
            var tag = contact.Tag.Trim();
            if (tag.Length == 0 || Relays?.IsRelay(_plc, tag) == true || model.RelaySwitches.Any(item => item.Tag.Trim().Equals(tag, StringComparison.OrdinalIgnoreCase)))
            {
                var used = model.RelaySwitches.Select(item => item.Tag.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var number = 1;
                while (used.Contains("-S" + number.ToString(CultureInfo.InvariantCulture)))
                {
                    number++;
                }

                tag = "-S" + number.ToString(CultureInfo.InvariantCulture);
            }

            if (!model.RelaySwitches.Any(item => item.Tag.Trim().Equals(tag, StringComparison.OrdinalIgnoreCase)))
            {
                model.RelaySwitches.Add(new RelaySwitch { Tag = tag, Positions = "0/1", Position = "0" });
            }

            contact.Tag = tag;
            contact.Pins = "13/12/14";
            contact.Position = "1";
        }

        _selected = contact;
        Changed();
        ShowProperties();
    }

    /// <summary>Switches whose tag no contact uses any more are removed.</summary>
    private void PruneSwitches()
    {
        if (Model is not { } model)
        {
            return;
        }

        var used = model.RelaySheets.SelectMany(sheet => sheet.Circuits).SelectMany(circuit => AllContacts(circuit.Elements))
            .Select(contact => contact.Tag.Trim())
            .Concat(model.Cubicles.SelectMany(cubicle => cubicle.Items).Where(item => item.IsSwitch).Select(item => item.Tag.Trim()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        model.RelaySwitches.RemoveAll(item => !used.Contains(item.Tag.Trim()));
    }

    /// <summary>Turns a selector switch from its knob on the sheet.</summary>
    private void TurnSwitch(RelaySwitch selector, UIElement knob)
    {
        var menu = new ContextMenu { PlacementTarget = knob };
        menu.Items.Add(new MenuItem { Header = $"{Display(selector.Tag, "Switch")}{(selector.Text.Trim().Length > 0 ? " — " + selector.Text.Replace("\r", "").Replace('\n', ' ').Trim() : "")}", IsEnabled = false });
        foreach (var position in selector.PositionList())
        {
            var item = new MenuItem { Header = position, IsCheckable = true, IsChecked = position.Equals(selector.Current, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) =>
            {
                selector.Position = position;
                _controller?.MarkDirty();
                Redraw();
                ShowProperties();
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>Settings of the selector switch of a contact (shared by all its contacts).</summary>
    private void SwitchProperties(RelaySwitch selector)
    {
        _properties.Children.Add(new TextBlock { Text = $"Selector switch {selector.Tag}", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        Choice("Position now", selector.PositionList().ToArray(), selector.Current, value =>
        {
            selector.Position = value;
            _controller?.MarkDirty();
            Redraw();
        }, "Or click the knob of the switch on the sheet (also while the simulation runs).");
        Field("Positions", selector.Positions, value =>
        {
            selector.Positions = string.Join("/", value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            Changed();
            ShowProperties();
        }, "2 or 3 positions separated by '/', e.g. OFF/ON, LOCAL/0/REMOTE or MAN/0/AUTO.");
        Field("Switch text", selector.Text, value =>
        {
            selector.Text = value;
            Changed();
        }, "Function text of the switch, e.g. GATE 1 LOCAL / REMOTE.", multiline: true);
        var uses = Model?.RelaySheets.SelectMany((sheet, sheetIndex) => sheet.Circuits.SelectMany((circuit, path) =>
                AllContacts(circuit.Elements).Where(contact => contact.Tag.Trim().Equals(selector.Tag.Trim(), StringComparison.OrdinalIgnoreCase))
                    .Select(contact => $"{RelayLogicRunner.Location(sheet, sheetIndex, path)} ({(contact.Position.Length > 0 ? contact.Position : "no position")})")))
            .ToList() ?? [];
        Note("Contacts of this switch: " + string.Join(", ", uses));
    }

    private static IEnumerable<RelayElement> AllContacts(IEnumerable<RelayElement> series) =>
        series.SelectMany(element => element.IsParallel ? element.Branches.SelectMany(AllContacts) : [element]);

    private void NewPath()
    {
        if (Sheet is null)
        {
            NewSheet();
        }

        if (Sheet is not { } sheet)
        {
            return;
        }

        var circuit = new RelayCircuit { Elements = [NewContact()], Coil = new RelayCoil { Tag = NextRelayTag() } };
        sheet.Circuits.Add(circuit);
        _selected = circuit.Coil;
        Changed();
        ShowProperties();
    }

    /// <summary>A new path that takes its supply below the selected contact (a branch to its own coil).</summary>
    private void PathFromHere()
    {
        if (_selected is not RelayElement element || Sheet is not { } sheet)
        {
            _status.Text = "Select the contact the new path branches off from.";
            return;
        }

        var feeder = sheet.Circuits.FindIndex(circuit => RelayLogicRunner.AllElements(circuit.Elements).Contains(element));
        var contact = new RelayElement { Tag = "", Contact = "NO", Pins = "11/12/14" };
        var circuit = new RelayCircuit { Feed = element.Id, Elements = [contact], Coil = new RelayCoil { Tag = NextRelayTag() } };
        var index = feeder + 1;
        while (index < sheet.Circuits.Count && sheet.Circuits[index].Feed.Length > 0 && FeedsFrom(sheet, index, feeder))
        {
            index++; // after the other branches of the same path
        }

        sheet.Circuits.Insert(Math.Clamp(index, 0, sheet.Circuits.Count), circuit);
        _selected = contact;
        Changed();
        ShowProperties();
    }

    private static bool FeedsFrom(RelaySheet sheet, int path, int feeder) =>
        feeder >= 0 && RelayLogicRunner.AllElements(sheet.Circuits[feeder].Elements).Any(element => element.Id.Equals(sheet.Circuits[path].Feed, StringComparison.OrdinalIgnoreCase));

    private void AddSeries()
    {
        var contact = NewContact();
        switch (_selected)
        {
            case RelayCoil coil when CircuitOf(coil) is { } circuit:
                circuit.Elements.Add(contact);
                break;
            case RelayElement element when Locate(element) is { } place:
                place.Series.Insert(place.Index + 1, contact);
                break;
            default:
                _status.Text = "Select a contact or a coil first.";
                return;
        }

        _selected = contact;
        Changed();
        ShowProperties();
    }

    private void AddParallel()
    {
        if (_selected is not RelayElement element || Locate(element) is not { } place)
        {
            _status.Text = "Select a contact (or a parallel group) first.";
            return;
        }

        var contact = NewContact();
        if (element.IsParallel)
        {
            element.Branches.Add([contact]);
        }
        else
        {
            place.Series[place.Index] = new RelayElement { Kind = RelayElementKind.Parallel, Branches = [[element], [contact]] };
        }

        _selected = contact;
        Changed();
        ShowProperties();
    }

    private void DeleteSelected()
    {
        if (_selected is RelayCoil coil && CircuitOf(coil) is { } circuit && Sheet is { } sheet)
        {
            if (MessageBox.Show(Window.GetWindow(this)!, $"Delete the whole path of {Display(coil.Tag, "this coil")}?", "Relay logic",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            sheet.Circuits.Remove(circuit);
            PruneSwitches();
            _selected = sheet;
        }
        else if (_selected is RelayElement element && Locate(element) is { } place)
        {
            place.Series.RemoveAt(place.Index);
            var removed = RelayLogicRunner.AllElements([element]).Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var fed in Sheet!.Circuits.Where(circuit => removed.Contains(circuit.Feed)))
            {
                fed.Feed = "";
            }

            Simplify(Sheet!);
            PruneSwitches();
            _selected = Sheet;
        }
        else
        {
            return;
        }

        Changed();
        ShowProperties();
    }

    private void MovePath(int direction)
    {
        var circuit = _selected switch
        {
            RelayCoil coil => CircuitOf(coil),
            RelayElement element => Sheet?.Circuits.FirstOrDefault(item => Contains(item.Elements, element)),
            _ => null
        };
        if (circuit is null || Sheet is not { } sheet)
        {
            _status.Text = "Select a contact or a coil of the path first.";
            return;
        }

        var index = sheet.Circuits.IndexOf(circuit);
        var target = index + direction;
        if (target < 0 || target >= sheet.Circuits.Count)
        {
            return;
        }

        sheet.Circuits.RemoveAt(index);
        sheet.Circuits.Insert(target, circuit);
        Changed();
    }

    private RelayElement NewContact()
    {
        // A new contact copies the relay of the selected one, so a chain of the same relay's contacts is quick.
        var tag = _selected is RelayElement { IsParallel: false } element ? element.Tag : "";
        return new RelayElement { Tag = tag, Contact = "NO", Pins = "11/12/14" };
    }

    private string NextRelayTag()
    {
        var used = Model?.RelaySheets.SelectMany(sheet => sheet.Circuits).Select(circuit => circuit.Coil.Tag.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        for (var number = 1; ; number++)
        {
            var tag = "-K" + number.ToString(CultureInfo.InvariantCulture);
            if (!used.Contains(tag))
            {
                return tag;
            }
        }
    }

    /// <summary>A parallel group left with one branch becomes that series again; empty branches go away.</summary>
    private static void Simplify(RelaySheet sheet)
    {
        foreach (var circuit in sheet.Circuits)
        {
            SimplifySeries(circuit.Elements);
        }
    }

    private static void SimplifySeries(List<RelayElement> series)
    {
        for (var i = 0; i < series.Count; i++)
        {
            if (!series[i].IsParallel)
            {
                continue;
            }

            var group = series[i];
            group.Branches.RemoveAll(branch => branch.Count == 0);
            foreach (var branch in group.Branches)
            {
                SimplifySeries(branch);
            }

            if (group.Branches.Count <= 1)
            {
                series.RemoveAt(i);
                series.InsertRange(i, group.Branches.FirstOrDefault() ?? []);
                i--;
            }
        }
    }

    private (List<RelayElement> Series, int Index)? Locate(RelayElement element)
    {
        foreach (var circuit in Sheet?.Circuits ?? [])
        {
            if (Find(circuit.Elements, element) is { } place)
            {
                return place;
            }
        }

        return null;
    }

    private static (List<RelayElement> Series, int Index)? Find(List<RelayElement> series, RelayElement element)
    {
        for (var i = 0; i < series.Count; i++)
        {
            if (ReferenceEquals(series[i], element))
            {
                return (series, i);
            }

            if (series[i].IsParallel)
            {
                foreach (var branch in series[i].Branches)
                {
                    if (Find(branch, element) is { } place)
                    {
                        return place;
                    }
                }
            }
        }

        return null;
    }

    private static bool Contains(List<RelayElement> series, RelayElement element) => Find(series, element) is not null;

    private RelayCircuit? CircuitOf(RelayCoil coil) => Sheet?.Circuits.FirstOrDefault(circuit => ReferenceEquals(circuit.Coil, coil));

    /// <summary>Structural change: rebuild the engine (keeps running state), mark the model dirty, redraw.</summary>
    private void Changed()
    {
        _status.Text = "";
        _controller?.Rebuild(); // raises ModelChanged → LoadSheets + Redraw
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
        _sheet.Children.Clear();
        var sheet = Sheet;
        if (sheet is null)
        {
            _sheet.Width = 600;
            _sheet.Height = 200;
            AddText("No relay-logic sheet yet. Use 'New sheet' and 'New path' to draw current paths:\n" +
                    "contacts (relay -K… or a signal / object) in series and in parallel, and the relay coil at the bottom.", 20, 20, 13, DimBrush);
            return;
        }

        var problems = _controller?.Engine.Problems.Where(problem => problem.StartsWith(_plc + "/", StringComparison.OrdinalIgnoreCase) || problem.StartsWith(_plc + ":", StringComparison.OrdinalIgnoreCase))
            .Where(problem => problem.Contains("relay", StringComparison.OrdinalIgnoreCase) || problem.Contains(sheet.Name, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        if (_status.Text.Length == 0 || _status.Tag as string == "problem")
        {
            _status.Text = problems.Count > 0 ? "⚠ " + string.Join("  ·  ", problems) : "";
            _status.Tag = problems.Count > 0 ? "problem" : null;
        }

        var sheetIndex = Model!.RelaySheets.IndexOf(sheet);
        var lefts = new double[sheet.Circuits.Count];
        var x = LeftMargin;
        for (var path = 0; path < sheet.Circuits.Count; path++)
        {
            lefts[path] = x;
            x += Math.Max(1, Measure(sheet.Circuits[path].Elements).Columns) * ColumnWidth;
        }

        // Layout pass: where each element ends, so a path branching off a contact starts below it.
        var tops = new double[sheet.Circuits.Count];
        var bottoms = new double[sheet.Circuits.Count];
        _positions.Clear();
        _dry = true;
        try
        {
            foreach (var path in DrawOrder(sheet))
            {
                var circuit = sheet.Circuits[path];
                tops[path] = circuit.Feed.Length > 0 && _positions.TryGetValue(circuit.Feed, out var feed) ? feed.Bottom + 16 : PathTop;
                bottoms[path] = circuit.Coil.HasInput ? tops[path] + InputBoxHeight : DrawSeries(circuit.Elements, lefts[path], tops[path]);
            }
        }
        finally
        {
            _dry = false;
        }

        var coilTop = Math.Max(PathTop + RowHeight, bottoms.DefaultIfEmpty(PathTop).Max()) + 10;
        var railBottom = coilTop + CoilHeight + 26;
        var width = Math.Max(x, LeftMargin + ColumnWidth) + LeftMargin;
        var mirrorRows = sheet.Circuits.Select(circuit => Relays?.ContactsOf(_plc, circuit.Coil.Tag).Count ?? 0).DefaultIfEmpty(0).Max();
        _sheet.Width = Math.Max(width, PageWidth);
        _sheet.Height = Math.Max(railBottom + 70 + mirrorRows * 15, PageHeight);

        // Page frame (A3 landscape or larger) and the rails across the page.
        var frame = new Rectangle { Width = _sheet.Width - 8, Height = _sheet.Height - 8, Stroke = DimBrush, StrokeThickness = 0.8, IsHitTestVisible = false };
        Canvas.SetLeft(frame, 4);
        Canvas.SetTop(frame, 4);
        _sheet.Children.Add(frame);
        AddText($"{sheet.Name}   ·   page {(sheet.Page > 0 ? sheet.Page : sheetIndex + 1)}", _sheet.Width - 360, _sheet.Height - 26, 11, DimBrush);
        width = _sheet.Width;
        AddLine(LeftMargin, RailTop, width - LeftMargin, RailTop, WireBrush, 2);
        AddText("L+", 4, RailTop - 20, 12, TextBrush, FontWeights.SemiBold);
        AddLine(LeftMargin, railBottom, width - LeftMargin, railBottom, WireBrush, 2);
        AddText("M", 6, railBottom - 20, 12, TextBrush, FontWeights.SemiBold);

        foreach (var path in DrawOrder(sheet))
        {
            var circuit = sheet.Circuits[path];
            var wireX = lefts[path] + WireOffset;

            // Path number on top, like the column numbers of a diagram page.
            AddText((path + 1).ToString(CultureInfo.InvariantCulture), wireX - 4, 14, 11, DimBrush);
            if (circuit.Coil.HasInput)
            {
                // Relay driven directly by its input (PLC output card): no supply from the rail, no contact.
                var driven = Relays?.IsPowered(circuit) == true;
                DrawRelayInput(circuit.Coil, wireX, tops[path], driven);
                AddLine(wireX, tops[path] + InputBoxHeight, wireX, coilTop, IsLive(driven) ? LiveBrush : WireBrush, 1.4);
                DrawCoil(circuit, sheet, sheetIndex, path, wireX, coilTop, railBottom);
                continue;
            }

            if (circuit.Feed.Length > 0 && _positions.TryGetValue(circuit.Feed, out var feed))
            {
                // Branch: from the junction below the feeding contact across and down to this path.
                var feedLive = IsLive(FeedLive(circuit.Feed));
                var junctionY = feed.Bottom + 8;
                AddLine(feed.X, junctionY, wireX, junctionY, feedLive ? LiveBrush : WireBrush, 1.4);
                AddLine(wireX, junctionY, wireX, tops[path], feedLive ? LiveBrush : WireBrush, 1.4);
                AddDot(feed.X, junctionY, feedLive ? LiveBrush : WireBrush);
            }
            else
            {
                AddLine(wireX, RailTop, wireX, tops[path], IsLive(true) ? LiveBrush : WireBrush, 1.4);
            }

            var bottom = DrawSeries(circuit.Elements, lefts[path], tops[path]);
            var powered = Relays?.IsPowered(circuit) == true;
            AddLine(wireX, bottom, wireX, coilTop, IsLive(powered) ? LiveBrush : WireBrush, 1.4);
            DrawCoil(circuit, sheet, sheetIndex, path, wireX, coilTop, railBottom);
        }
    }

    /// <summary>Paths in an order where a feeding path comes before the paths branching off it.</summary>
    private static List<int> DrawOrder(RelaySheet sheet)
    {
        var owner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var path = 0; path < sheet.Circuits.Count; path++)
        {
            foreach (var element in RelayLogicRunner.AllElements(sheet.Circuits[path].Elements))
            {
                owner.TryAdd(element.Id, path);
            }
        }

        var order = new List<int>();
        var seen = new HashSet<int>();
        void Visit(int path)
        {
            if (!seen.Add(path))
            {
                return;
            }

            if (owner.TryGetValue(sheet.Circuits[path].Feed, out var feeder) && feeder != path)
            {
                Visit(feeder);
            }

            order.Add(path);
        }

        for (var path = 0; path < sheet.Circuits.Count; path++)
        {
            Visit(path);
        }

        return order;
    }

    private bool FeedLive(string id)
    {
        var element = Sheet?.Circuits.SelectMany(circuit => RelayLogicRunner.AllElements(circuit.Elements)).FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return element is not null && Relays?.StateOf(element) is { PoweredIn: true, Closed: true };
    }

    private void AddDot(double x, double y, Brush brush)
    {
        if (_dry)
        {
            return;
        }

        var dot = new Ellipse { Width = 7, Height = 7, Fill = brush, IsHitTestVisible = false };
        Canvas.SetLeft(dot, x - 3.5);
        Canvas.SetTop(dot, y - 3.5);
        _sheet.Children.Add(dot);
    }

    private bool IsLive(bool powered) => powered && _controller?.Running == true;

    private static (int Columns, int Rows) Measure(List<RelayElement> series)
    {
        var columns = 1;
        var rows = 0;
        foreach (var element in series)
        {
            if (element.IsParallel)
            {
                var sizes = element.Branches.Select(Measure).ToList();
                columns = Math.Max(columns, sizes.Sum(size => Math.Max(1, size.Columns)));
                rows += Math.Max(1, sizes.Select(size => size.Rows).DefaultIfEmpty(1).Max());
            }
            else
            {
                rows++;
            }
        }

        return (columns, rows);
    }

    /// <summary>Draws a series from the top of its wire; returns the y where its wire ends.</summary>
    private double DrawSeries(List<RelayElement> series, double left, double top)
    {
        var wireX = left + WireOffset;
        var y = top;
        foreach (var element in series)
        {
            if (!element.IsParallel)
            {
                DrawContact(element, wireX, y);
                y += RowHeight;
                _positions[element.Id] = (wireX, y);
                continue;
            }

            var (poweredIn, closed) = Relays?.StateOf(element) ?? (false, false);
            var rows = Math.Max(1, element.Branches.Select(branch => Measure(branch).Rows).DefaultIfEmpty(1).Max());
            var groupBottom = y + rows * RowHeight;
            var branchLeft = left;
            var firstX = wireX;
            var lastX = wireX;
            var branchTop = y + 8;
            foreach (var branch in element.Branches)
            {
                var branchX = branchLeft + WireOffset;
                lastX = branchX;
                AddLine(branchX, y, branchX, branchTop, IsLive(poweredIn) ? LiveBrush : WireBrush, 1.4);
                var end = DrawSeries(branch, branchLeft, branchTop);
                var branchOut = branch.Count > 0 && Relays?.StateOf(branch[^1]) is { } last && last.PoweredIn && last.Closed;
                AddLine(branchX, end, branchX, groupBottom, IsLive(branchOut) ? LiveBrush : WireBrush, 1.4);
                branchLeft += Math.Max(1, Measure(branch).Columns) * ColumnWidth;
            }

            // Bus bars joining the branches at the top and at the bottom of the group.
            AddLine(firstX, y, lastX, y, IsLive(poweredIn) ? LiveBrush : WireBrush, 1.4);
            AddLine(firstX, groupBottom, lastX, groupBottom, IsLive(poweredIn && closed) ? LiveBrush : WireBrush, 1.4);
            AddHitArea(element, firstX - 20, y - 6, lastX - firstX + 40, 12, "Parallel group — 'Contact beside' adds a branch.");
            y = groupBottom;
            _positions[element.Id] = (wireX, groupBottom);
        }

        return y;
    }

    /// <summary>
    /// A changeover contact as in a circuit diagram: the common terminal at the top, the blade resting on the NC
    /// terminal (right) and the NO terminal straight below the pivot. The path continues from the terminal it uses.
    /// </summary>
    private void DrawContact(RelayElement contact, double x, double y)
    {
        var (poweredIn, closed) = Relays?.StateOf(contact) ?? (false, false);
        var operated = contact.IsNormallyClosed ? !closed : closed;
        var running = _controller?.Running == true;
        var (common, nc, no) = contact.Terminals();
        var pivotY = y + 34;
        var contactY = y + 70;
        var ncX = x + 16;
        var exitY = y + RowHeight - 14;
        var wireIn = IsLive(poweredIn) ? LiveBrush : WireBrush;
        var wireOut = IsLive(poweredIn && closed) ? LiveBrush : WireBrush;

        if (ReferenceEquals(_selected, contact))
        {
            AddRect(x - WireOffset + 6, y + 2, ColumnWidth - 12, RowHeight - 4, SelectBrush);
        }

        // Common terminal and pivot.
        AddLine(x, y, x, pivotY, wireIn, 1.4);
        AddText(common, x + 4, pivotY - 20, 10, TextBrush);

        // NO terminal (below the pivot, left bracket) and NC terminal (right, hook).
        AddLine(x - 6, contactY, x, contactY, WireBrush, 1.2);
        AddText(no, x - 22, contactY - 2, 10, TextBrush);
        AddLine(ncX, contactY - 6, ncX, contactY, WireBrush, 1.2);
        AddLine(ncX, contactY, ncX + 6, contactY, WireBrush, 1.2);
        AddText(nc, ncX + 6, contactY - 2, 10, TextBrush);

        // Blade: on NC when the relay is released, on NO when it is operated.
        var bladeBrush = running && operated ? OperatedBrush : wireIn;
        if (operated && running)
        {
            AddLine(x, pivotY, x, contactY - 2, bladeBrush, 2);
        }
        else
        {
            AddLine(x, pivotY, ncX + 2, contactY - 4, bladeBrush, 2);
        }

        // The path leaves from the used terminal and comes back to the wire line.
        if (contact.IsNormallyClosed)
        {
            AddLine(ncX, contactY, ncX, exitY, wireOut, 1.4);
            AddLine(ncX, exitY, x, exitY, wireOut, 1.4);
            AddLine(x, exitY, x, y + RowHeight, wireOut, 1.4);
        }
        else
        {
            AddLine(x, contactY, x, y + RowHeight, wireOut, 1.4);
        }

        // Tag, cross reference to the coil and function text, right-aligned to the left of the contact.
        var relay = Relays?.RelayOfContact(_plc, contact);
        var tagText = relay?.Tag ?? contact.Tag.Trim();
        var location = relay is { } owner ? (owner.Plc.Equals(_plc, StringComparison.OrdinalIgnoreCase) ? "" : owner.Plc + " ") + Relays!.CoilLocation(owner.Plc, owner.Tag) : "";
        var text = contact.Text.Trim().Length > 0 ? contact.Text : relay is { } coilOf ? Relays!.CoilText(coilOf.Plc, coilOf.Tag) : "";
        RelaySwitch? switchKnob = null;
        if (Relays?.SwitchOf(_plc, contact.Tag) is { } selector)
        {
            // Manually operated: the dashed actuator line to the switch knob (drawn after the hit area, so it is clickable).
            var actuator = new Line { X1 = x + 8, Y1 = (pivotY + contactY) / 2, X2 = x + 34, Y2 = (pivotY + contactY) / 2, Stroke = WireBrush, StrokeThickness = 1, StrokeDashArray = [3, 2], IsHitTestVisible = false };
            if (!_dry)
            {
                _sheet.Children.Add(actuator);
            }

            switchKnob = selector;
            location = $"closed in {(contact.Position.Length > 0 ? contact.Position : "?")}";
            if (contact.Text.Trim().Length == 0)
            {
                text = selector.Text;
            }
        }
        else if (Relays?.ButtonOf(_plc, contact.Tag) is { } button)
        {
            location = "push button" + (button.Pressed ? " · pressed" : "");
            if (contact.Text.Trim().Length == 0)
            {
                text = button.Text;
            }
        }
        else if (relay is null)
        {
            // A device (its input) or directly a PLC signal: module and port with the ID, its name as the function text.
            var device = Relays?.DeviceOf(_plc, contact.Tag);
            var input = device?.Input.Trim() ?? (PortBinding.Parse(contact.Tag).Kind != BindingKind.None ? contact.Tag.Trim() : "");
            if (device is not null && contact.Text.Trim().Length == 0 && device.Text.Trim().Length > 0)
            {
                text = device.Text;
            }

            if (SignalOf(input) is { } signal)
            {
                location = $"{signal.ModuleText} port {signal.Channel} · {signal.Kind}:{signal.Id}";
                if (text.Trim().Length == 0)
                {
                    text = signal.Name;
                }

                if (device is null)
                {
                    tagText = "";
                }
            }
            else if (device is not null)
            {
                location = "← " + input;
            }
        }

        var labelRight = x - 26;
        AddTextRight(tagText, labelRight, y + 22, 15, TextBrush);
        AddTextRight(location, labelRight, y + 42, 10.5, DimBrush);
        AddTextRight(text, labelRight, y + 56, 10.5, TextBrush);

        AddHitArea(contact, x - WireOffset + 6, y + 2, ColumnWidth - 12, RowHeight - 4,
            $"{Display(contact.Tag, "Contact")} {(contact.IsNormallyClosed ? "NC" : "NO")} {contact.Pins}");
        if (switchKnob is not null && !_dry)
        {
            DrawSwitchKnob(switchKnob, x + 46, (pivotY + contactY) / 2);
        }
    }

    /// <summary>
    /// The selector switch at its contact: a round knob whose pointer shows the position (left / middle / right),
    /// the current position above it. Clicking it opens the positions.
    /// </summary>
    private void DrawSwitchKnob(RelaySwitch selector, double cx, double cy)
    {
        const double radius = 12;
        var positions = selector.PositionList();
        var index = Math.Max(0, positions.FindIndex(item => item.Equals(selector.Current, StringComparison.OrdinalIgnoreCase)));
        var knob = new Ellipse { Width = radius * 2, Height = radius * 2, Stroke = WireBrush, StrokeThickness = 1.4, Fill = Brushes.White, Cursor = Cursors.Hand };
        knob.ToolTip = $"{Display(selector.Tag, "Switch")} in {selector.Current} — click to turn ({selector.Positions})";
        Canvas.SetLeft(knob, cx - radius);
        Canvas.SetTop(knob, cy - radius);
        _sheet.Children.Add(knob);

        // Pointer: positions spread over -45°..+45° from the top (two positions: -30° / +30°).
        var span = positions.Count >= 3 ? 45.0 : 30.0;
        var angle = positions.Count <= 1 ? 0 : -span + 2 * span * index / (positions.Count - 1);
        var radians = angle * Math.PI / 180;
        AddLine(cx, cy, cx + Math.Sin(radians) * (radius - 2), cy - Math.Cos(radians) * (radius - 2), OperatedBrush, 2.4);
        foreach (var mark in Enumerable.Range(0, positions.Count))
        {
            var markAngle = (positions.Count <= 1 ? 0 : -span + 2 * span * mark / (positions.Count - 1)) * Math.PI / 180;
            AddLine(cx + Math.Sin(markAngle) * (radius + 2), cy - Math.Cos(markAngle) * (radius + 2),
                cx + Math.Sin(markAngle) * (radius + 5), cy - Math.Cos(markAngle) * (radius + 5), WireBrush, 1.2);
        }

        var label = new TextBlock { Text = selector.Current, FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = OperatedBrush, TextAlignment = TextAlignment.Center, Width = 60, IsHitTestVisible = false };
        Canvas.SetLeft(label, cx - 30);
        Canvas.SetTop(label, cy - radius - 20);
        _sheet.Children.Add(label);
        knob.MouseLeftButtonDown += (_, e) =>
        {
            TurnSwitch(selector, knob);
            e.Handled = true;
        };
    }

    private void DrawCoil(RelayCircuit circuit, RelaySheet sheet, int sheetIndex, int path, double x, double top, double railBottom)
    {
        var coil = circuit.Coil;
        var on = Relays?.IsOn(circuit) == true && _controller?.Running == true;
        var powered = Relays?.IsPowered(circuit) == true && _controller?.Running == true;
        if (ReferenceEquals(_selected, coil))
        {
            AddRect(x - WireOffset + 6, top - 8, ColumnWidth - 12, CoilHeight + 16, SelectBrush);
        }

        if (coil.Tag.Trim().Length == 0 && coil.Outputs.Trim().Length > 0)
        {
            DrawPlcInput(coil, x, top, on);
            AddHitArea(coil, x - WireOffset + 6, top - 8, ColumnWidth - 12, CoilHeight + 16, $"PLC input {coil.Outputs} — {RelayLogicRunner.Location(sheet, sheetIndex, path)}");
            return;
        }

        var box = new Rectangle
        {
            Width = CoilWidth,
            Height = CoilHeight,
            Stroke = WireBrush,
            StrokeThickness = 1.6,
            Fill = on ? CoilOnBrush : powered ? CoilTimingBrush : Brushes.White
        };
        Canvas.SetLeft(box, x - CoilWidth / 2);
        Canvas.SetTop(box, top);
        _sheet.Children.Add(box);
        if (coil.Timer.Length > 0)
        {
            // Time relay: the delay mark of the coil symbol.
            AddLine(x - CoilWidth / 2, top + 10, x + CoilWidth / 2, top + 10, WireBrush, 1);
            AddText(coil.Timer.Equals(RelayTimer.OffDelay, StringComparison.OrdinalIgnoreCase) ? "OFF" : "ON", x - 10, top + 12, 8, TextBrush);
        }

        AddText("A1", x + CoilWidth / 2 + 3, top - 4, 9, DimBrush);
        AddText("A2", x + CoilWidth / 2 + 3, top + CoilHeight - 10, 9, DimBrush);
        AddLine(x, top + CoilHeight, x, railBottom, WireBrush, 1.4);

        var labelRight = x - CoilWidth / 2 - 8;
        AddTextRight(Display(coil.Tag, coil.Text.Trim().Length > 0 ? "" : "(no tag)"), labelRight, top - 4, 15, TextBrush);
        var info = coil.Text;
        if (coil.Timer.Length > 0)
        {
            info = $"{coil.Time.ToString("0.##", CultureInfo.InvariantCulture)} s {(coil.Timer.Equals(RelayTimer.OffDelay, StringComparison.OrdinalIgnoreCase) ? "off" : "on")}-delay" + (info.Length > 0 ? "\n" + info : "");
        }

        AddTextRight(info, labelRight, top + 16, 10.5, TextBrush);
        if (coil.Outputs.Trim().Length > 0)
        {
            AddText("→ " + coil.Outputs.Trim(), x + 6, top + CoilHeight + 4, 10, OperatedBrush);
        }

        // Contact mirror below the rail: where this relay's contacts are used.
        var mirrorY = railBottom + 8;
        foreach (var (location, contact) in Relays?.ContactsOf(_plc, coil.Tag) ?? [])
        {
            var (common, ncPin, noPin) = contact.Terminals();
            AddText($"{common}-{(contact.IsNormallyClosed ? ncPin : noPin)}  {location}", x - 30, mirrorY, 10, DimBrush);
            mirrorY += 15;
        }

        AddHitArea(coil, x - WireOffset + 6, top - 8, ColumnWidth - 12, CoilHeight + 16, $"Coil {Display(coil.Tag, "")} — {RelayLogicRunner.Location(sheet, sheetIndex, path)}");
    }

    private const double InputBoxHeight = 84;

    /// <summary>The signal of a source "DO:160" in this PLC (module, port, name), or null.</summary>
    private SimulatorSignal? SignalOf(string source) =>
        PortBinding.Parse(source) is { Kind: BindingKind.Signal } binding ? _controller?.Session(_plc)?.Find(binding.SignalKind, binding.SignalId).FirstOrDefault() : null;


    /// <summary>The direct input of a relay (e.g. the PLC output DO:160): a box with the signal and its state.</summary>
    private void DrawRelayInput(RelayCoil coil, double x, double top, bool on)
    {
        if (_dry)
        {
            return;
        }

        var input = coil.Input.Trim();
        var signal = PortBinding.Parse(input);
        var title = signal is { Kind: BindingKind.Signal, SignalKind: SimulatorSignalKind.DO } ? "PLC OUTPUT" : "INPUT";
        var label = new TextBlock { TextAlignment = TextAlignment.Center, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var found = SignalOf(input);
        if (found is { } info)
        {
            label.Inlines.Add(new System.Windows.Documents.Run($"{info.ModuleText}  port {info.Channel}") { FontWeight = FontWeights.SemiBold });
            label.Inlines.Add(new System.Windows.Documents.LineBreak());
            label.Inlines.Add(new System.Windows.Documents.Run($"{info.Kind}:{info.Id}  {info.Name}"));
        }
        else
        {
            label.Inlines.Add(new System.Windows.Documents.Run(input) { FontWeight = FontWeights.SemiBold });
        }

        label.Inlines.Add(new System.Windows.Documents.LineBreak());
        label.Inlines.Add(new System.Windows.Documents.Run(title) { FontSize = 9, Foreground = DimBrush });
        var running = _controller?.Running == true;
        var box = new Border
        {
            Width = ColumnWidth - 24,
            Height = InputBoxHeight - 12,
            BorderBrush = WireBrush,
            BorderThickness = new Thickness(1.6),
            CornerRadius = new CornerRadius(3),
            Background = on && running ? CoilOnBrush : Brushes.White,
            Child = label,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(box, x - (ColumnWidth - 24) / 2);
        Canvas.SetTop(box, top);
        _sheet.Children.Add(box);
        AddHitArea(coil, x - (ColumnWidth - 24) / 2, top, ColumnWidth - 24, InputBoxHeight - 12,
            $"{Display(coil.Tag, "Relay")} is driven by {input} (its simulator variable; SET TRUE / SET FALSE in the signal list act here).");
    }

    /// <summary>A path without a relay, wired straight to PLC inputs: a terminal box with the input addresses.</summary>
    private void DrawPlcInput(RelayCoil coil, double x, double top, bool on)
    {
        var box = new Border
        {
            Width = 64,
            Height = CoilHeight - 10,
            BorderBrush = WireBrush,
            BorderThickness = new Thickness(1.6),
            Background = on ? CoilOnBrush : Brushes.White,
            Child = new TextBlock { Text = "PLC", FontSize = 10, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            IsHitTestVisible = false
        };
        Canvas.SetLeft(box, x - 32);
        Canvas.SetTop(box, top + 5);
        _sheet.Children.Add(box);
        AddText(coil.Outputs.Trim(), x + 36, top + 12, 10.5, OperatedBrush);
        AddTextRight(coil.Text, x - 40, top + 4, 10.5, TextBrush);
    }

    private void AddHitArea(object item, double x, double y, double width, double height, string tooltip)
    {
        if (_dry)
        {
            return;
        }

        var area = new Rectangle { Width = Math.Max(4, width), Height = Math.Max(4, height), Fill = Brushes.Transparent, ToolTip = tooltip, Cursor = Cursors.Hand };
        area.MouseLeftButtonDown += (_, e) =>
        {
            Select(item);
            e.Handled = true;
        };
        Canvas.SetLeft(area, x);
        Canvas.SetTop(area, y);
        _sheet.Children.Add(area);
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush brush, double thickness)
    {
        if (!_dry)
        {
            _sheet.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness, IsHitTestVisible = false });
        }
    }

    private void AddRect(double x, double y, double width, double height, Brush fill)
    {
        if (_dry)
        {
            return;
        }

        var rect = new Rectangle { Width = width, Height = height, Fill = fill, RadiusX = 3, RadiusY = 3, IsHitTestVisible = false };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        _sheet.Children.Add(rect);
    }

    private void AddText(string text, double x, double y, double size, Brush brush, FontWeight? weight = null)
    {
        if (_dry)
        {
            return;
        }

        var block = new TextBlock { Text = text, FontSize = size, Foreground = brush, FontWeight = weight ?? FontWeights.Normal, IsHitTestVisible = false };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        _sheet.Children.Add(block);
    }

    private void AddTextRight(string text, double right, double y, double size, Brush brush)
    {
        if (_dry || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        const double width = WireOffset - 34;
        var block = new TextBlock
        {
            Text = text.Replace("\\n", "\n"),
            FontSize = size,
            Foreground = brush,
            TextAlignment = TextAlignment.Right,
            TextWrapping = TextWrapping.Wrap,
            Width = width,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(block, right - width);
        Canvas.SetTop(block, y);
        _sheet.Children.Add(block);
    }

    // ---------- properties ----------

    private void ShowProperties()
    {
        _properties.Children.Clear();
        switch (_selected)
        {
            case RelayElement { IsParallel: true }:
                Header("Parallel group");
                Note("Branches are joined at the top and at the bottom. Select a contact in a branch and use 'Contact beside' to add a branch, or 'Contact below' to extend it.");
                break;
            case RelayElement contact:
                ContactProperties(contact);
                break;
            case RelayCoil coil:
                CoilProperties(coil);
                break;
            case RelaySheet sheet:
                SheetProperties(sheet);
                break;
            default:
                Header("Relay logic");
                Note("Add a sheet with 'New sheet', then current paths with 'New path'.");
                break;
        }
    }

    private void SheetProperties(RelaySheet sheet)
    {
        Header("Sheet");
        Field("Name", sheet.Name, value =>
        {
            sheet.Name = value.Trim();
            LoadSheets();
            _controller?.MarkDirty();
        });
        Field("Page", sheet.Page > 0 ? sheet.Page.ToString(CultureInfo.InvariantCulture) : "", value =>
        {
            sheet.Page = int.TryParse(value.Trim(), out var page) && page > 0 ? page : 0;
            LoadSheets();
            Changed();
        }, "Page number in cross references (/page.path). Empty = the sheet's position.");
        Note($"{sheet.Circuits.Count} path(s). Select a contact or a coil to edit it.");
    }

    private void ContactProperties(RelayElement contact)
    {
        Header("Contact");
        SearchField("Tag", contact.Tag, RelaySuggestions().Concat(Suggestions(PortDirection.Input)).ToList(), value =>
        {
            contact.Tag = value;
            PruneSwitches();
            Changed();
            ShowProperties();
        }, "What the contact belongs to (type to search): a relay or switch of these sheets (-K107), a push button, a device (-F1 with its input), or directly a signal DO / DI, an object (@Object.Port) or =1.");
        Choice("Contact", ["NO", "NC"], contact.IsNormallyClosed ? "NC" : "NO", value =>
        {
            contact.Contact = value;
            Changed();
        }, "NO = closes when operated (11-14), NC = opens when operated (11-12).");
        Field("Terminals", contact.Pins, value =>
        {
            contact.Pins = value.Trim();
            Changed();
        }, "Common / NC / NO, e.g. 11/12/14 or 21/22/24.");
        var selector = Relays?.SwitchOf(_plc, contact.Tag);
        if (selector is not null)
        {
            SwitchPositions(contact, selector);
        }
        else
        {
            SourceField(contact);
        }

        Field("Text", contact.Text, value =>
        {
            contact.Text = value;
            Changed();
        }, "Function text (Enter = new line). Empty = the text of the relay coil or switch.", multiline: true);
        if (_controller?.Running == true && Relays is { } relays)
        {
            var (poweredIn, closed) = relays.StateOf(contact);
            Note($"Live: {(closed ? "closed" : "open")}{(poweredIn ? ", current at the common terminal" : "")}.");
        }

        if (selector is not null)
        {
            SwitchProperties(selector);
        }
    }

    /// <summary>Switching table of a selector-switch contact: the positions in which it is operated.</summary>
    private void SwitchPositions(RelayElement contact, RelaySwitch selector)
    {
        _properties.Children.Add(new TextBlock { Text = "Operated in position", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2) });
        var chosen = contact.Position.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var row = new WrapPanel();
        foreach (var position in selector.PositionList())
        {
            var box = new CheckBox { Content = position, IsChecked = chosen.Contains(position), Margin = new Thickness(0, 2, 12, 2) };
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true)
                {
                    chosen.Add(position);
                }
                else
                {
                    chosen.Remove(position);
                }

                contact.Position = string.Join(",", selector.PositionList().Where(chosen.Contains));
                Changed();
            };
            row.Children.Add(box);
        }

        _properties.Children.Add(row);
        _properties.Children.Add(new TextBlock
        {
            Text = $"Switch {selector.Tag} ({selector.Positions}). NO: closed in the ticked positions; NC: open in them.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = DimBrush,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 4)
        });
    }

    /// <summary>
    /// What the tag of a contact is; a device that is not a relay, switch, push button or signal gets its input here
    /// (defined once for the tag, all its contacts follow it).
    /// </summary>
    private void SourceField(RelayElement contact)
    {
        var tag = contact.Tag.Trim();
        if (tag.Length == 0 || Model is not { } model)
        {
            Note("Enter the tag of the device the contact belongs to.");
            return;
        }

        if (Relays?.RelayOfContact(_plc, contact) is { } relay)
        {
            Note($"Auxiliary contact of relay {relay.Tag} ({Relays!.CoilLocation(relay.Plc, relay.Tag)}).");
            return;
        }

        if (Relays?.ButtonOf(_plc, tag) is not null)
        {
            Note($"Contact of push button {tag} on the cubicle.");
            return;
        }

        if (PortBinding.Parse(tag).Kind != BindingKind.None)
        {
            Note(SignalOf(tag) is { } signal ? $"Directly the signal {signal.ModuleText} port {signal.Channel} · {signal.Kind}:{signal.Id} {signal.Name}." : $"Directly {tag}.");
            return;
        }

        var device = RelayDevices.Find(model, tag);
        SearchField("Device input", device?.Input ?? "", Suggestions(PortDirection.Input), value =>
        {
            if (RelayDevices.Find(model, tag) is { } existing)
            {
                existing.Input = value.Trim();
                if (existing.Input.Length == 0)
                {
                    model.RelayDevices.Remove(existing);
                }
            }
            else
            {
                RelayDevices.Define(model, tag, value);
            }

            Changed();
        }, $"What operates device {tag} (all its contacts): a PLC signal DI / DO, an object (@F53.On) or =1. Defined once for the tag.");
    }

    private void CoilProperties(RelayCoil coil)
    {
        Header("Relay coil");
        if (CircuitOf(coil) is { Feed.Length: > 0 } fed)
        {
            var feed = Sheet?.Circuits.SelectMany(circuit => RelayLogicRunner.AllElements(circuit.Elements)).FirstOrDefault(element => element.Id.Equals(fed.Feed, StringComparison.OrdinalIgnoreCase));
            Note($"This path branches off below {(feed is null ? "a contact that no longer exists" : Display(feed.Tag, "a contact"))}.");
            var toRail = new Button { Content = "Supply from L+ instead", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 0, 8) };
            toRail.Click += (_, _) =>
            {
                fed.Feed = "";
                Changed();
                ShowProperties();
            };
            _properties.Children.Add(toRail);
        }

        Field("Tag", coil.Tag, value =>
        {
            coil.Tag = value.Trim();
            Changed();
        }, "Relay name, e.g. -K107. Contacts with this tag follow the coil. Empty tag + Drives = the path is wired straight to PLC inputs (no relay).");
        Field("Text", coil.Text, value =>
        {
            coil.Text = value;
            Changed();
        }, "Function text, e.g. GATE 1 / OPEN (CMD) (Enter = new line).", multiline: true);
        SearchField("Input", coil.Input, Suggestions(PortDirection.Input), value =>
        {
            coil.Input = value.Trim();
            Changed();
        }, "What drives the relay directly, e.g. the PLC output DO:160 (module/port) — the relay follows its simulator variable. Empty = the current path above the coil.");
        if (SignalOf(coil.Input.Trim()) is { } inputSignal)
        {
            Note($"PLC output: {inputSignal.ModuleText} port {inputSignal.Channel} · {inputSignal.Kind}:{inputSignal.Id} {inputSignal.Name}{(inputSignal.IsEnabled ? "" : " (spare)")}");
        }

        SearchField("Drives", coil.Outputs, Suggestions(PortDirection.Output), value =>
        {
            coil.Outputs = value;
            Changed();
        }, "PLC inputs switched by the relay (type to search by ID, module or name, e.g. A111 port 3; spare channels too; several: DI:23, DI:24; empty = none).");
        foreach (var output in coil.Outputs.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (SignalOf(output) is { } outputSignal)
            {
                Note($"PLC input: {outputSignal.ModuleText} port {outputSignal.Channel} · {outputSignal.Kind}:{outputSignal.Id} {outputSignal.Name}{(outputSignal.IsEnabled ? "" : " (spare)")}");
            }
        }
        var timer = coil.Timer.Equals(RelayTimer.OnDelay, StringComparison.OrdinalIgnoreCase) ? "On-delay"
            : coil.Timer.Equals(RelayTimer.OffDelay, StringComparison.OrdinalIgnoreCase) ? "Off-delay" : "Normal relay";
        Choice("Type", ["Normal relay", "On-delay", "Off-delay"], timer, value =>
        {
            coil.Timer = value switch { "On-delay" => RelayTimer.OnDelay, "Off-delay" => RelayTimer.OffDelay, _ => RelayTimer.None };
            Changed();
            ShowProperties();
        }, "Time relay: on-delay picks up after the time, off-delay drops out after the time.");
        if (coil.Timer.Length > 0)
        {
            Field("Time [s]", coil.Time.ToString("0.###", CultureInfo.InvariantCulture), value =>
            {
                coil.Time = double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var time) ? Math.Max(0, time) : 0;
                Changed();
            });
        }

        if (Relays is { } relays && coil.Tag.Trim().Length > 0)
        {
            var uses = relays.ContactsOf(_plc, coil.Tag);
            Note(uses.Count == 0 ? "No contact of this relay is used yet." : $"Contacts used: {string.Join(", ", uses.Select(use => use.Location))}.");
        }
    }

    private List<string> Suggestions(PortDirection direction) => SignalSearchField.Signals(_controller, _plc, direction);

    private List<string> RelaySuggestions() => SignalSearchField.Devices(Model);

    private void SearchField(string label, string value, List<string> suggestions, Action<string> apply, string? hint = null) =>
        SignalSearchField.Add(_properties, label, value, suggestions, apply, hint, DimBrush);

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

    private void Choice(string label, string[] values, string value, Action<string> apply, string? hint = null)
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
        if (hint is not null)
        {
            _properties.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Foreground = DimBrush, FontSize = 11, Margin = new Thickness(0, 2, 0, 4) });
        }
    }

    private static Button ToolButton(string text, string tooltip, RoutedEventHandler click)
    {
        var button = new Button { Content = text, ToolTip = tooltip, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0), MinHeight = 26 };
        button.Click += click;
        return button;
    }

    private static string Display(string tag, string fallback) => string.IsNullOrWhiteSpace(tag) ? fallback : tag.Trim();

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
