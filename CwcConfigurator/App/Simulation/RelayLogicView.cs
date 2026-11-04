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
        toolbar.Children.Add(ToolButton("Delete sheet", "Delete this sheet with all its paths.", (_, _) => DeleteSheet()));
        toolbar.Children.Add(new Separator { Width = 14, Opacity = 0 });
        toolbar.Children.Add(ToolButton("New path", "Add a current path (one contact and a relay coil) at the right.", (_, _) => NewPath()));
        toolbar.Children.Add(ToolButton("Contact below", "Add a contact in series below the selected contact (or above the selected coil).", (_, _) => AddSeries()));
        toolbar.Children.Add(ToolButton("Contact beside", "Add a contact in parallel to the selected contact or parallel group.", (_, _) => AddParallel()));
        toolbar.Children.Add(ToolButton("Delete", "Delete the selected contact (or the whole path when the coil is selected). Del key.", (_, _) => DeleteSelected()));
        toolbar.Children.Add(ToolButton("Switch contact", "Make the selected contact a contact of a selector switch (2 or 3 positions), or add one below the selected element. Turn the switch by clicking its knob on the sheet.", (_, _) => SwitchContact()));
        toolbar.Children.Add(ToolButton("◀", "Move the selected path to the left.", (_, _) => MovePath(-1)));
        toolbar.Children.Add(ToolButton("▶", "Move the selected path to the right.", (_, _) => MovePath(1)));
        toolbar.Children.Add(_status);

        var canvasHost = new ScrollViewer
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
        _sheet.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == _sheet)
            {
                Select(Sheet);
            }

            canvasHost.Focus();
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
        Redraw();
        ShowProperties();
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
            contact.Source = "";
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
            .Select(contact => contact.Tag.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        var maxRows = Math.Max(1, sheet.Circuits.Select(circuit => Measure(circuit.Elements).Rows).DefaultIfEmpty(1).Max());
        var coilTop = PathTop + maxRows * RowHeight + 10;
        var railBottom = coilTop + CoilHeight + 26;
        var columns = sheet.Circuits.Sum(circuit => Math.Max(1, Measure(circuit.Elements).Columns));
        var width = LeftMargin * 2 + Math.Max(1, columns) * ColumnWidth;
        var mirrorRows = sheet.Circuits.Select(circuit => Relays?.ContactsOf(_plc, circuit.Coil.Tag).Count ?? 0).DefaultIfEmpty(0).Max();
        _sheet.Width = Math.Max(width, 700);
        _sheet.Height = railBottom + 70 + mirrorRows * 15;

        // Rails.
        AddLine(LeftMargin, RailTop, width - LeftMargin, RailTop, WireBrush, 2);
        AddText("L+", 4, RailTop - 20, 12, TextBrush, FontWeights.SemiBold);
        AddLine(LeftMargin, railBottom, width - LeftMargin, railBottom, WireBrush, 2);
        AddText("M", 6, railBottom - 20, 12, TextBrush, FontWeights.SemiBold);

        var x = LeftMargin;
        for (var path = 0; path < sheet.Circuits.Count; path++)
        {
            var circuit = sheet.Circuits[path];
            var size = Measure(circuit.Elements);
            var wireX = x + WireOffset;

            // Path number on top, like the column numbers of a diagram page.
            AddText((path + 1).ToString(CultureInfo.InvariantCulture), wireX - 4, 14, 11, DimBrush);
            AddLine(wireX, RailTop, wireX, PathTop, IsLive(true) ? LiveBrush : WireBrush, 1.4);
            var bottom = DrawSeries(circuit.Elements, x, PathTop);
            var powered = Relays?.IsPowered(circuit) == true;
            AddLine(wireX, bottom, wireX, coilTop, IsLive(powered) ? LiveBrush : WireBrush, 1.4);
            DrawCoil(circuit, sheet, sheetIndex, path, wireX, coilTop, railBottom);
            x += Math.Max(1, size.Columns) * ColumnWidth;
        }
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
        var location = Relays?.CoilLocation(_plc, contact.Tag) ?? "";
        var text = contact.Text.Trim().Length > 0 ? contact.Text : Relays?.CoilText(_plc, contact.Tag) ?? "";
        RelaySwitch? switchKnob = null;
        if (Relays?.SwitchOf(_plc, contact.Tag) is { } selector)
        {
            // Manually operated: the dashed actuator line to the switch knob (drawn after the hit area, so it is clickable).
            var actuator = new Line { X1 = x + 8, Y1 = (pivotY + contactY) / 2, X2 = x + 34, Y2 = (pivotY + contactY) / 2, Stroke = WireBrush, StrokeThickness = 1, StrokeDashArray = [3, 2], IsHitTestVisible = false };
            _sheet.Children.Add(actuator);
            switchKnob = selector;
            location = $"closed in {(contact.Position.Length > 0 ? contact.Position : "?")}";
            if (contact.Text.Trim().Length == 0)
            {
                text = selector.Text;
            }
        }
        else if (location.Length == 0 && contact.Source.Trim().Length > 0)
        {
            location = contact.Source.Trim();
        }

        var labelRight = x - 26;
        AddTextRight(Display(contact.Tag, ""), labelRight, y + 22, 15, TextBrush);
        AddTextRight(location, labelRight, y + 42, 10.5, DimBrush);
        AddTextRight(text, labelRight, y + 56, 10.5, TextBrush);

        AddHitArea(contact, x - WireOffset + 6, y + 2, ColumnWidth - 12, RowHeight - 4,
            $"{Display(contact.Tag, "Contact")} {(contact.IsNormallyClosed ? "NC" : "NO")} {contact.Pins}" + (contact.Source.Length > 0 ? $" · {contact.Source}" : ""));
        if (switchKnob is not null)
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
        AddTextRight(Display(coil.Tag, "(no tag)"), labelRight, top - 4, 15, TextBrush);
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

    private void AddLine(double x1, double y1, double x2, double y2, Brush brush, double thickness) =>
        _sheet.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness, IsHitTestVisible = false });

    private void AddRect(double x, double y, double width, double height, Brush fill)
    {
        var rect = new Rectangle { Width = width, Height = height, Fill = fill, RadiusX = 3, RadiusY = 3, IsHitTestVisible = false };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        _sheet.Children.Add(rect);
    }

    private void AddText(string text, double x, double y, double size, Brush brush, FontWeight? weight = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, Foreground = brush, FontWeight = weight ?? FontWeights.Normal, IsHitTestVisible = false };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        _sheet.Children.Add(block);
    }

    private void AddTextRight(string text, double right, double y, double size, Brush brush)
    {
        if (string.IsNullOrWhiteSpace(text))
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
        SearchField("Tag", contact.Tag, RelaySuggestions(), value =>
        {
            contact.Tag = value;
            PruneSwitches();
            Changed();
            ShowProperties();
        }, "Relay or selector switch of these sheets (type to search, e.g. k107 or gate open). Any other device tag (-F1, -Q1) needs a source.");
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

    private void SourceField(RelayElement contact)
    {
        var isRelay = Relays?.IsRelay(_plc, contact.Tag) == true;
        SearchField("Source", contact.Source, Suggestions(PortDirection.Input), value =>
        {
            contact.Source = value;
            Changed();
        }, isRelay
            ? $"Not used: {contact.Tag} is a relay on {Relays!.CoilLocation(_plc, contact.Tag)}."
            : "What operates the contact (type to search, e.g. gate 1 open): a PLC output DO, an input DI, an object (@Object.Port) or =1.");
    }

    private void CoilProperties(RelayCoil coil)
    {
        Header("Relay coil");
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
        SearchField("Drives", coil.Outputs, Suggestions(PortDirection.Output), value =>
        {
            coil.Outputs = value;
            Changed();
        }, "PLC inputs switched by the relay (type to search; several: DI:23, DI:24; empty = none).");
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

    /// <summary>Signals, objects and constants as in the Program editor ("DO:0  —  PLC name · text").</summary>
    private List<string> Suggestions(PortDirection direction) =>
        _controller is null ? [] : BindingSuggestions.For(new PortDef("Contact", direction, PortType.Bool, ""), _plc, _controller);

    /// <summary>Relays (coils) of all sheets of this PLC: "-K107  —  GATE 1 OPEN (CMD)  /37.1".</summary>
    private List<string> RelaySuggestions()
    {
        var list = new List<string>();
        var model = Model;
        for (var sheetIndex = 0; sheetIndex < (model?.RelaySheets.Count ?? 0); sheetIndex++)
        {
            var sheet = model!.RelaySheets[sheetIndex];
            for (var path = 0; path < sheet.Circuits.Count; path++)
            {
                var coil = sheet.Circuits[path].Coil;
                if (coil.Tag.Trim().Length > 0)
                {
                    list.Add($"{coil.Tag.Trim()}{PortRow.Separator}{coil.Text.Replace("\r", "").Replace('\n', ' ').Trim()}  {RelayLogicRunner.Location(sheet, sheetIndex, path)}");
                }
            }
        }

        foreach (var selector in model?.RelaySwitches ?? [])
        {
            if (selector.Tag.Trim().Length > 0)
            {
                list.Add($"{selector.Tag.Trim()}{PortRow.Separator}{selector.Text.Replace("\r", "").Replace('\n', ' ').Trim()}  switch {selector.Positions}");
            }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Editable, searchable list (all typed words must match, like the Program editor). Shows the stored codes with
    /// their texts; stores only the codes ("DO:0, DI:3", "-K107").
    /// </summary>
    private void SearchField(string label, string value, List<string> suggestions, Action<string> apply, string? hint = null)
    {
        _properties.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2) });
        string Display(string stored) => string.Join(", ", stored.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => suggestions.FirstOrDefault(item => item.StartsWith(part + PortRow.Separator, StringComparison.OrdinalIgnoreCase)) ?? part));
        var combo = new ComboBox
        {
            ItemsSource = suggestions,
            MinHeight = 24,
            ToolTip = "Type to narrow the list (all words must match); Down opens it, Enter takes the first match."
        };
        ComboFilter.SetIsEnabled(combo, true);
        combo.Text = Display(value);
        var original = value;
        void Commit()
        {
            var text = Code(combo.Text);
            if (text != original)
            {
                original = text;
                apply(text);
            }
        }

        combo.LostKeyboardFocus += (_, e) =>
        {
            if (!combo.IsKeyboardFocusWithin)
            {
                Commit();
            }
        };
        combo.DropDownClosed += (_, _) =>
        {
            if (combo.SelectedItem is string)
            {
                Commit();
            }
        };
        combo.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        _properties.Children.Add(combo);
        if (hint is not null)
        {
            _properties.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Foreground = DimBrush, FontSize = 11, Margin = new Thickness(0, 2, 0, 4) });
        }
    }

    /// <summary>"DO:0  —  text, DI:3  —  text" → "DO:0, DI:3"; "-K107  —  text" → "-K107".</summary>
    private static string Code(string text) => PortRow.StripDescriptions(text ?? "").Trim() is { Length: > 0 } stripped && !stripped.StartsWith('-')
        ? stripped
        : (text ?? "").Split(PortRow.Separator.Trim())[0].Trim();

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
