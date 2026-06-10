using System.Windows;
using System.Windows.Controls;

namespace CwcConfigurator.App;

public enum SettingsHubKey
{
    None,
    ProjectPaths,
    TableDesign,
    Bilingual,
    DbNamesAndNumbers,
    DbDiValidationColors,
    DbDoValidationColors,
    DbAoValidationColors,
    DbMeasValidationColors,
    DbAlmValidationColors,
    DbParValidationColors,
    DbDiPlcNames,
    DbDoPlcNames,
    DbAoPlcNames,
    DbMeasPlcNames,
    DbAlmPlcNames,
    DbParPlcNames
}

public partial class SettingsHubDialog : Window
{
    private readonly Func<SettingsHubKey, SettingsEditorViewBase?> _viewFactory;
    private readonly Action<SettingsHubKey> _onApplied;
    private readonly Func<bool> _readBilingual;
    private readonly Action<bool> _writeBilingual;

    private SettingsHubKey _currentKey = SettingsHubKey.None;
    private SettingsEditorViewBase? _activeView;

    public SettingsHubDialog(
        Func<SettingsHubKey, SettingsEditorViewBase?> viewFactory,
        Action<SettingsHubKey> onApplied,
        Func<bool> readBilingual,
        Action<bool> writeBilingual)
    {
        InitializeComponent();
        _viewFactory = viewFactory;
        _onApplied = onApplied;
        _readBilingual = readBilingual;
        _writeBilingual = writeBilingual;
        BuildTree();
        ShowPlaceholder();
    }

    private void BuildTree()
    {
        SettingsTree.Items.Clear();

        var general = NewBranch("General");
        general.Items.Add(NewLeaf("Project paths", SettingsHubKey.ProjectPaths));
        general.Items.Add(NewLeaf("Table design", SettingsHubKey.TableDesign));
        general.Items.Add(NewLeaf("Multilingual mode", SettingsHubKey.Bilingual));
        general.IsExpanded = true;
        SettingsTree.Items.Add(general);

        SettingsTree.Items.Add(NewLeaf("DB names and numbers", SettingsHubKey.DbNamesAndNumbers));

        SettingsTree.Items.Add(BuildDbBranch("DB DI", SettingsHubKey.DbDiValidationColors, SettingsHubKey.DbDiPlcNames));
        SettingsTree.Items.Add(BuildDbBranch("DB DO", SettingsHubKey.DbDoValidationColors, SettingsHubKey.DbDoPlcNames));
        SettingsTree.Items.Add(BuildDbBranch("DB AO", SettingsHubKey.DbAoValidationColors, SettingsHubKey.DbAoPlcNames));
        SettingsTree.Items.Add(BuildDbBranch("DB MEAS", SettingsHubKey.DbMeasValidationColors, SettingsHubKey.DbMeasPlcNames));
        SettingsTree.Items.Add(BuildDbBranch("DB ALM", SettingsHubKey.DbAlmValidationColors, SettingsHubKey.DbAlmPlcNames));
        SettingsTree.Items.Add(BuildDbBranch("DB PAR", SettingsHubKey.DbParValidationColors, SettingsHubKey.DbParPlcNames));
    }

    private static TreeViewItem NewBranch(string header) => new()
    {
        Header = header,
        FontWeight = FontWeights.SemiBold,
        Padding = new Thickness(2, 4, 2, 4)
    };

    private static TreeViewItem NewLeaf(string header, SettingsHubKey key) => new()
    {
        Header = header,
        Tag = key,
        Padding = new Thickness(2, 3, 2, 3)
    };

    private static TreeViewItem BuildDbBranch(string label, SettingsHubKey validationKey, SettingsHubKey plcNamesKey)
    {
        var branch = NewBranch(label);
        branch.Items.Add(NewLeaf("Validation & colors", validationKey));
        branch.Items.Add(NewLeaf("PLC names & types", plcNamesKey));
        return branch;
    }

    private void SettingsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem item && item.Tag is SettingsHubKey key)
        {
            LoadLeaf(key, item.Header?.ToString() ?? "");
        }
        else
        {
            ShowPlaceholder();
        }
    }

    private void ShowPlaceholder()
    {
        _currentKey = SettingsHubKey.None;
        _activeView = null;
        EditorHost.Content = EditorPlaceholder;
        BilingualCheckBox.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Visible;
        DetailTitle.Text = "Select a setting on the left";
        DetailSubtitle.Text = "";
        DetailDescription.Text = "Each branch groups related settings. Pick a leaf to start editing. " +
                                 "Apply commits your changes to the project; switching to another leaf or " +
                                 "pressing Close without Apply discards them.";
        ApplyButton.IsEnabled = false;
        RevertButton.IsEnabled = false;
        StatusText.Text = "";
    }

    private void LoadLeaf(SettingsHubKey key, string leafLabel)
    {
        _currentKey = key;
        _activeView = null;
        BilingualCheckBox.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Collapsed;
        StatusText.Text = "";

        SetDetailText(key, leafLabel);

        if (key == SettingsHubKey.Bilingual)
        {
            EditorHost.Content = EditorPlaceholder;
            BilingualCheckBox.Visibility = Visibility.Visible;
            BilingualCheckBox.IsChecked = _readBilingual();
            ApplyButton.IsEnabled = false;
            RevertButton.IsEnabled = false;
            return;
        }

        var view = _viewFactory(key);
        if (view is null)
        {
            EditorHost.Content = EditorPlaceholder;
            EmptyHint.Visibility = Visibility.Visible;
            ApplyButton.IsEnabled = false;
            RevertButton.IsEnabled = false;
            return;
        }

        _activeView = view;
        EditorHost.Content = view;
        ApplyButton.IsEnabled = true;
        RevertButton.IsEnabled = true;
    }

    private void SetDetailText(SettingsHubKey key, string leafLabel)
    {
        (string title, string subtitle, string description) = key switch
        {
            SettingsHubKey.ProjectPaths => (
                "Project paths",
                "General · Project paths",
                "Folders the generator writes to and reads from."),
            SettingsHubKey.TableDesign => (
                "Table design",
                "General · Table design",
                "Visual style for the spreadsheet tabs: alternating row tint, frozen-column separator, " +
                "and conditional cell colors. Affects look only."),
            SettingsHubKey.Bilingual => (
                "Multilingual project",
                "General · Multilingual",
                "When ON, repair and validation check the *Other text fields (NameOther, DescriptionOther, SystemNameOther) " +
                "the same way as English. When OFF, *Other fields are ignored."),
            SettingsHubKey.DbNamesAndNumbers => (
                "DB names and numbers",
                "Database · Naming",
                "PLC data-block numbers and names for every DB. Generated source files use these as `DATA_BLOCK \"name\"` " +
                "and DB number references."),
            _ when IsValidationColors(key) => (
                $"{DbLabel(key)} — validation & colors",
                $"Database · {DbLabel(key)} · Validation",
                "Per-column dropdown validation values, text length limits used by Validate (F5), and conditional cell colors."),
            _ when IsPlcNames(key) => (
                $"{DbLabel(key)} — PLC names & types",
                $"Database · {DbLabel(key)} · PLC structure",
                "System name rows that define the PLC TYPE structure, struct type prefix, and (where applicable) simulator type prefix."),
            _ => (leafLabel, "", "")
        };

        DetailTitle.Text = title;
        DetailSubtitle.Text = subtitle;
        DetailDescription.Text = description;
    }

    private static bool IsValidationColors(SettingsHubKey key) =>
        key is SettingsHubKey.DbDiValidationColors
            or SettingsHubKey.DbDoValidationColors
            or SettingsHubKey.DbAoValidationColors
            or SettingsHubKey.DbMeasValidationColors
            or SettingsHubKey.DbAlmValidationColors
            or SettingsHubKey.DbParValidationColors;

    private static bool IsPlcNames(SettingsHubKey key) =>
        key is SettingsHubKey.DbDiPlcNames
            or SettingsHubKey.DbDoPlcNames
            or SettingsHubKey.DbAoPlcNames
            or SettingsHubKey.DbMeasPlcNames
            or SettingsHubKey.DbAlmPlcNames
            or SettingsHubKey.DbParPlcNames;

    private static string DbLabel(SettingsHubKey key) => key switch
    {
        SettingsHubKey.DbDiValidationColors or SettingsHubKey.DbDiPlcNames => "DB DI",
        SettingsHubKey.DbDoValidationColors or SettingsHubKey.DbDoPlcNames => "DB DO",
        SettingsHubKey.DbAoValidationColors or SettingsHubKey.DbAoPlcNames => "DB AO",
        SettingsHubKey.DbMeasValidationColors or SettingsHubKey.DbMeasPlcNames => "DB MEAS",
        SettingsHubKey.DbAlmValidationColors or SettingsHubKey.DbAlmPlcNames => "DB ALM",
        SettingsHubKey.DbParValidationColors or SettingsHubKey.DbParPlcNames => "DB PAR",
        _ => ""
    };

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_activeView is null)
        {
            return;
        }

        try
        {
            if (!_activeView.Apply())
            {
                StatusText.Text = "Apply skipped — fix highlighted errors.";
                return;
            }
            _onApplied(_currentKey);
            StatusText.Text = $"Applied at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Apply failed: {ex.Message}";
        }
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_currentKey == SettingsHubKey.None || _currentKey == SettingsHubKey.Bilingual)
        {
            return;
        }

        var label = (SettingsTree.SelectedItem as TreeViewItem)?.Header?.ToString() ?? "";
        LoadLeaf(_currentKey, label);
        StatusText.Text = "Reverted to last saved values.";
    }

    private void BilingualCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _writeBilingual(BilingualCheckBox.IsChecked == true);
        StatusText.Text = $"Multilingual mode {(BilingualCheckBox.IsChecked == true ? "ON" : "OFF")}.";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
