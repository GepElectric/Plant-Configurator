using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CwcConfigurator.Services.Simulation;
using CwcConfigurator.Services.Simulation.Process;

namespace CwcConfigurator.App.Simulation;

/// <summary>
/// "Program" tab of the simulator: the objects of one PLC's plant model, their variants, port bindings
/// (signals, links, constants), parameters and characteristic tables.
/// </summary>
public partial class ProgramView : UserControl
{
    private SimulationController? _controller;
    private string _plc = "";
    private ObjectConfig? _current;
    private List<PortRow> _ports = [];
    private bool _selfChange;
    private bool _reloading;

    public ProgramView()
    {
        InitializeComponent();
        TypeBox.ItemsSource = ObjectCatalog.All;
        TypeBox.SelectedIndex = 0;
    }

    private ProcessModel? Model => _controller is not null && _controller.Models.TryGetValue(_plc, out var model) ? model : null;

    public void Attach(SimulationController controller)
    {
        _controller = controller;
        controller.ModelChanged += () =>
        {
            if (!_selfChange)
            {
                ReloadList();
            }

            UpdateProblems();
        };
        controller.FileStateChanged += () => ModelPathText.Text = controller.ModelPath is null ? "Model not saved yet." : "Model file: " + controller.ModelPath;
        ModelPathText.Text = controller.ModelPath is null ? "Model not saved yet." : "Model file: " + controller.ModelPath;
    }

    public void ShowPlc(string plc)
    {
        _plc = plc;
        ReloadList();
    }

    /// <summary>Refreshes the live values of the shown ports (called from the window timer).</summary>
    public void RefreshLive()
    {
        if (_controller is null || _current is null || !IsVisible)
        {
            return;
        }

        var instance = _controller.Engine.Find(_plc, _current.Name);
        foreach (var row in _ports)
        {
            row.Refresh(instance);
        }
    }

    /// <summary>Object tree: groups (sorted by name) with their members, then the objects without a group.</summary>
    private void ReloadList(string? select = null)
    {
        var selected = select ?? _current?.Name;
        var collapsed = (ObjectTree.ItemsSource as IEnumerable<object>)?.OfType<ObjectGroupNode>().Where(group => !group.IsExpanded)
            .Select(group => group.Name).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var objects = (Model?.Objects ?? [])
            .OrderBy(item => ObjectCatalog.Find(item.Type)?.Category ?? "~")
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var nodes = new List<object>();
        foreach (var group in objects.Where(item => item.Group is not null).GroupBy(item => item.Group!, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            nodes.Add(new ObjectGroupNode(group.Key, group.Select(item => new ObjectNode(item)).ToList()) { IsExpanded = !collapsed.Contains(group.Key) });
        }

        nodes.AddRange(objects.Where(item => item.Group is null).Select(item => new ObjectNode(item)));
        var restore = AllObjectNodes(nodes).FirstOrDefault(node => node.Config.Name == selected);
        if (restore is not null)
        {
            restore.IsSelected = true;
        }

        _reloading = true;
        try
        {
            ObjectTree.ItemsSource = nodes;
        }
        finally
        {
            _reloading = false;
        }

        GroupBox.ItemsSource = nodes.OfType<ObjectGroupNode>().Select(group => group.Name).ToList();
        ShowObject(restore?.Config);
        UpdateProblems();
    }

    private static IEnumerable<ObjectNode> AllObjectNodes(IEnumerable<object> nodes) =>
        nodes.SelectMany(node => node is ObjectGroupNode group ? group.Members : node is ObjectNode item ? [item] : []);

    private void UpdateProblems()
    {
        if (_controller is null)
        {
            return;
        }

        var problems = _controller.Engine.Problems.Where(problem => problem.StartsWith(_plc + "/", StringComparison.OrdinalIgnoreCase)).ToList();
        ProblemsText.Text = problems.Count == 0 ? "" : string.Join(Environment.NewLine, problems.Take(10));
    }

    private void ObjectTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_reloading)
        {
            return;
        }

        if (e.NewValue is ObjectNode node)
        {
            ShowObject(node.Config);
        }
        else if (e.NewValue is ObjectGroupNode)
        {
            ShowObject(null);
        }
    }

    // ---- groups: drag and drop in the tree, buttons and the Group box ----

    private Point _dragStart;
    private ObjectConfig? _dragCandidate;

    private void ObjectTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(ObjectTree);
        _dragCandidate = (FindItem(e.OriginalSource as DependencyObject)?.DataContext as ObjectNode)?.Config;
    }

    private void ObjectTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var offset = e.GetPosition(ObjectTree) - _dragStart;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var config = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(ObjectTree, new DataObject(typeof(ObjectConfig), config), DragDropEffects.Move);
    }

    private void ObjectTree_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ObjectConfig)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void ObjectTree_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ObjectConfig)) is not ObjectConfig config)
        {
            return;
        }

        var target = FindItem(e.OriginalSource as DependencyObject)?.DataContext;
        var group = target switch
        {
            ObjectGroupNode node => node.Name,
            ObjectNode node => node.Config.Group,
            _ => null
        };
        SetGroup([config], group, config.Name);
    }

    private static TreeViewItem? FindItem(DependencyObject? element)
    {
        while (element is not null and not TreeViewItem)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return element as TreeViewItem;
    }

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            MessageBox.Show(Window.GetWindow(this)!, "Select the object that goes into the new group.", "Simulator", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = TextPrompt.Ask(Window.GetWindow(this)!, "New group", $"Name of the new group for {_current.Name}:", "");
        if (!string.IsNullOrWhiteSpace(name))
        {
            SetGroup([_current], name, _current.Name);
        }
    }

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = (ObjectTree.SelectedItem as ObjectGroupNode)?.Name ?? _current?.Group;
        if (group is null || Model is not { } model)
        {
            MessageBox.Show(Window.GetWindow(this)!, "Select a group or an object of a group.", "Simulator", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = TextPrompt.Ask(Window.GetWindow(this)!, "Rename group", $"New name of the group '{group}':", group);
        if (!string.IsNullOrWhiteSpace(name))
        {
            SetGroup(model.Objects.Where(item => string.Equals(item.Group, group, StringComparison.OrdinalIgnoreCase)).ToList(), name, _current?.Name);
        }
    }

    private void Ungroup_Click(object sender, RoutedEventArgs e)
    {
        if (ObjectTree.SelectedItem is ObjectGroupNode group && Model is { } model)
        {
            SetGroup(model.Objects.Where(item => string.Equals(item.Group, group.Name, StringComparison.OrdinalIgnoreCase)).ToList(), null, null);
        }
        else if (_current is not null)
        {
            SetGroup([_current], null, _current.Name);
        }
    }

    private void GroupBox_LostFocus(object sender, RoutedEventArgs e) => ApplyGroupBox();

    private void GroupBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyGroupBox();
        }
    }

    private void ApplyGroupBox()
    {
        if (_current is not null && !string.Equals(_current.Group ?? "", GroupBox.Text.Trim(), StringComparison.Ordinal))
        {
            SetGroup([_current], GroupBox.Text, _current.Name);
        }
    }

    /// <summary>Puts objects into a group (null/empty = no group); the tree and the SCADA frames follow.</summary>
    private void SetGroup(IReadOnlyList<ObjectConfig> objects, string? group, string? select)
    {
        group = string.IsNullOrWhiteSpace(group) ? null : group.Trim();
        if (Model is { } model && group is not null)
        {
            // Same spelling as an existing group.
            group = model.Objects.Select(item => item.Group).FirstOrDefault(name => string.Equals(name, group, StringComparison.OrdinalIgnoreCase)) ?? group;
        }

        foreach (var config in objects)
        {
            config.Group = group;
        }

        _controller?.MarkDirty();
        _controller?.NotifyLayoutChanged();
        ReloadList(select);
    }

    private void ShowObject(ObjectConfig? config)
    {
        _current = config;
        DetailsPanel.IsEnabled = config is not null;
        var info = config is null ? null : ObjectCatalog.Find(config.Type);
        if (config is null || info is null || _controller is null)
        {
            NameBox.Text = "";
            DescriptionBox.Text = "";
            TypeText.Text = config is null ? "" : $"Unknown type '{config.Type}'.";
            OptionsList.ItemsSource = null;
            PortsGrid.ItemsSource = null;
            ParamsGrid.ItemsSource = null;
            TablesList.ItemsSource = null;
            _ports = [];
            return;
        }

        NameBox.Text = config.Name;
        DescriptionBox.Text = config.Description;
        GroupBox.Text = config.Group ?? "";
        TypeText.Text = $"{info.Title} — {info.Description}";
        // Only the options and ports of the chosen variant are shown (e.g. no Setpoint for OPEN/CLOSE control,
        // no Esd without an ESD valve); hidden ports keep their binding but work as not connected.
        OptionsList.ItemsSource = info.Options.Where(option => info.IsActive(option.ShownWhen, config))
            .Select(option => new OptionRow(option, config, VariantChanged)).ToList();
        _ports = info.Ports
            .Where(port => info.IsActive(port.ShownWhen, config))
            .OrderBy(port => port.Direction)
            .Select(port => new PortRow(port, config, BindingSuggestions.For(port, _plc, _controller), StructureChanged))
            .ToList();
        PortsGrid.ItemsSource = _ports;
        ParamsGrid.ItemsSource = info.Params.Where(param => info.IsActive(param.ShownWhen, config)).Select(param => new ParamRow(param, config, Changed)).ToList();
        TablesList.ItemsSource = info.Tables.Select(table => new TableRow(table, config, Changed)).ToList();
        RefreshLive();
    }

    private void Changed() => _controller?.MarkDirty();

    private void VariantChanged()
    {
        StructureChanged();
        var config = _current;
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(config, _current))
            {
                ShowObject(config);
            }
        });
    }

    private void StructureChanged()
    {
        if (_controller is null)
        {
            return;
        }

        _selfChange = true;
        try
        {
            _controller.Rebuild();
        }
        finally
        {
            _selfChange = false;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || TypeBox.SelectedItem is not ObjectTypeInfo info)
        {
            return;
        }

        var name = UniqueName(model, info.Type);
        var top = model.Objects.Count == 0 ? 20 : model.Objects.Max(item => item.Y) + 150;
        model.Objects.Add(new ObjectConfig { Name = name, Type = info.Type, Description = info.Title, X = 20, Y = top });
        _controller!.Rebuild();
        Select(name);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || _current is null)
        {
            return;
        }

        if (MessageBox.Show(Window.GetWindow(this)!, $"Delete object '{_current.Name}'?", "Simulator", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        model.Objects.Remove(_current);
        _current = null;
        _controller!.Rebuild();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || _current is null)
        {
            return;
        }

        var copy = new ObjectConfig
        {
            Name = UniqueName(model, _current.Name),
            Type = _current.Type,
            Description = _current.Description,
            Options = new(_current.Options, StringComparer.OrdinalIgnoreCase),
            Params = new(_current.Params, StringComparer.OrdinalIgnoreCase),
            Tables = _current.Tables.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.OrdinalIgnoreCase),
            Ports = new(_current.Ports.Where(pair => ObjectCatalog.Find(_current.Type)?.Port(pair.Key)?.Direction == PortDirection.Input), StringComparer.OrdinalIgnoreCase),
            X = _current.X + 30,
            Y = _current.Y + 30
        };
        model.Objects.Add(copy);
        _controller!.Rebuild();
        Select(copy.Name);
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || _controller?.Session(_plc) is not { } session)
        {
            return;
        }

        var result = ModelGenerator.Generate(session, model);
        if (result.Objects.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this)!, string.Join(Environment.NewLine, result.Report), "Generate from signals", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var preview = string.Join(Environment.NewLine, result.Report.Take(40)) + (result.Report.Count > 40 ? $"{Environment.NewLine}… ({result.Report.Count - 40} more)" : "");
        if (MessageBox.Show(Window.GetWindow(this)!,
                $"Add {result.Objects.Count} objects to {_plc}: {string.Join(", ", result.Objects.Select(item => item.Name))}?{Environment.NewLine}{Environment.NewLine}{preview}",
                "Generate from signals", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        model.Objects.AddRange(result.Objects);
        _controller.Rebuild();
    }

    /// <summary>Save: the window's Save model (asks for a file when none is chosen yet).</summary>
    public event RoutedEventHandler? SaveRequested;

    private void Save_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, e);

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null)
        {
            return;
        }

        if (_controller.Dirty && MessageBox.Show(Window.GetWindow(this)!, "Discard the unsaved model changes and reload the file?", "Simulator",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!_controller.Reload())
        {
            MessageBox.Show(Window.GetWindow(this)!, "There is no saved model file yet.", "Simulator", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (_controller?.ModelPath is not { } path || !File.Exists(path))
        {
            MessageBox.Show(Window.GetWindow(this)!, "Save the model first.", "Simulator", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Rename();
        }
    }

    private void NameBox_LostFocus(object sender, RoutedEventArgs e) => Rename();

    private void Rename()
    {
        if (_current is null || Model is not { } model || _controller is null)
        {
            return;
        }

        var name = NameBox.Text.Trim().Replace("/", "_").Replace(".", "_").Replace("@", "");
        if (name.Length == 0 || name == _current.Name)
        {
            NameBox.Text = _current.Name;
            return;
        }

        if (model.Find(name) is not null)
        {
            MessageBox.Show(Window.GetWindow(this)!, $"An object named '{name}' already exists.", "Simulator", MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Text = _current.Name;
            return;
        }

        var old = _current.Name;
        foreach (var (plc, other) in _controller.Models)
        {
            var prefix = plc.Equals(_plc, StringComparison.OrdinalIgnoreCase) ? $"@{old}." : $"@{_plc}/{old}.";
            var replacement = plc.Equals(_plc, StringComparison.OrdinalIgnoreCase) ? $"@{name}." : $"@{_plc}/{name}.";
            foreach (var item in other.Objects)
            {
                foreach (var key in item.Ports.Keys.ToList())
                {
                    if (item.Ports[key].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Ports[key] = replacement + item.Ports[key][prefix.Length..];
                    }
                }
            }
        }

        _current.Name = name;
        _controller.Rebuild();
        Select(name);
    }

    private void DescriptionBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_current is not null && _current.Description != DescriptionBox.Text)
        {
            _current.Description = DescriptionBox.Text;
            Changed();
        }
    }

    private void Select(string name) => ReloadList(name);

    private static string UniqueName(ProcessModel model, string baseName)
    {
        var stem = baseName.TrimEnd("0123456789".ToCharArray());
        for (var index = 1; ; index++)
        {
            var name = stem + index;
            if (model.Find(name) is null)
            {
                return name;
            }
        }
    }

}

/// <summary>Group node of the object tree.</summary>
public sealed class ObjectGroupNode(string name, List<ObjectNode> members)
{
    public string Name => name;
    public List<ObjectNode> Members => members;
    public string CountText => $"({members.Count})";
    public bool IsExpanded { get; set; } = true;
    public bool IsSelected { get; set; }
}

/// <summary>Object node of the object tree.</summary>
public sealed class ObjectNode(ObjectConfig config)
{
    public ObjectConfig Config => config;
    public string TypeText => $"({ObjectCatalog.Find(config.Type)?.Title ?? config.Type})";
    public bool IsExpanded { get; set; }
    public bool IsSelected { get; set; }
}

/// <summary>Small text prompt (WPF has none).</summary>
public static class TextPrompt
{
    public static string? Ask(Window owner, string title, string text, string initial)
    {
        var box = new TextBox { Text = initial, Height = 24, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 10) };
        var ok = new Button { Content = "OK", MinWidth = 70, Height = 26, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 70, Height = 26, IsCancel = true };
        var window = new Window
        {
            Title = title,
            Owner = owner,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(14),
                Children =
                {
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
                }
            }
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return window.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
