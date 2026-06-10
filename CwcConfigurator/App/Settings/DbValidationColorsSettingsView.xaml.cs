using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App.Settings;

public sealed class DbValidationColorsCommit
{
    public required Action<IEnumerable<DbColorSetting>, DbTextLengthSettings, IEnumerable<DbValidationListSetting>> Commit { get; init; }
}

public partial class DbValidationColorsSettingsView : SettingsEditorViewBase
{
    private readonly DbValidationColorsCommit _commit;
    private DbValidationListSetting? _selectedValidationRow;

    public ObservableCollection<DbColorSetting> ColorRows { get; }
    public ObservableCollection<DbValidationListSetting> ValidationRows { get; }
    public ObservableCollection<ValidationOptionRow> ValidationOptionRows { get; } = [];

    public IReadOnlyList<string> ColorPalette { get; } =
    [
        "White", "Yellow", "Red", "LimeGreen", "DodgerBlue", "Orange", "Gray", "Black", "Transparent"
    ];

    public DbValidationColorsSettingsView(
        IEnumerable<DbColorSetting> colorRows,
        DbTextLengthSettings textLengths,
        bool showColors,
        IEnumerable<DbValidationListSetting>? validationRows,
        DbValidationColorsCommit commit)
    {
        InitializeComponent();
        _commit = commit;

        ColorRows = new ObservableCollection<DbColorSetting>(colorRows.Select(r => r.Clone()));
        ValidationRows = new ObservableCollection<DbValidationListSetting>((validationRows ?? []).Select(r => r.Clone()));
        ColorsGrid.ItemsSource = ColorRows;
        ValidationGrid.ItemsSource = ValidationRows;
        ValidationOptionsGrid.ItemsSource = ValidationOptionRows;
        ColorsTab.Visibility = showColors ? Visibility.Visible : Visibility.Collapsed;
        if (ValidationRows.Count > 0)
        {
            ValidationGrid.SelectedIndex = 0;
        }

        SystemEngMaxText.Text = textLengths.StatusSystemEnglishMax.ToString();
        NameEngMaxText.Text = textLengths.StatusNameEnglishMax.ToString();
        SystemOtherMaxText.Text = textLengths.StatusSystemOtherMax.ToString();
        NameOtherMaxText.Text = textLengths.StatusNameOtherMax.ToString();
    }

    public override bool Apply()
    {
        SyncSelectedValidationOptions();
        if (!TryReadPositiveInt(SystemEngMaxText.Text, out var systemEng)
            || !TryReadPositiveInt(NameEngMaxText.Text, out var nameEng)
            || !TryReadPositiveInt(SystemOtherMaxText.Text, out var systemOther)
            || !TryReadPositiveInt(NameOtherMaxText.Text, out var nameOther))
        {
            MessageBox.Show(
                Window.GetWindow(this),
                "Maximum lengths must be positive whole numbers.",
                "Invalid settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var lengths = new DbTextLengthSettings
        {
            StatusSystemEnglishMax = systemEng,
            StatusNameEnglishMax = nameEng,
            StatusSystemOtherMax = systemOther,
            StatusNameOtherMax = nameOther
        };

        _commit.Commit(ColorRows.Select(r => r.Clone()), lengths, ValidationRows.Select(r => r.Clone()));
        return true;
    }

    private void ColorCombo_DropDownOpened(object sender, EventArgs e)
    {
        if (sender is not ComboBox combo)
        {
            return;
        }

        combo.IsDropDownOpen = false;
        var dialog = new ColorPickerDialog(combo.Text)
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        combo.Text = dialog.SelectedColor;
        combo.SelectedItem = combo.Text;
        if (combo.DataContext is DbColorSetting colorSetting)
        {
            colorSetting.Color = combo.Text;
        }
    }

    private void AddValidationField_Click(object sender, RoutedEventArgs e)
    {
        SyncSelectedValidationOptions();
        var row = new DbValidationListSetting
        {
            Key = "NEW.Settings",
            Name = "New setting",
            ValuesText = "0 - New value"
        };
        ValidationRows.Add(row);
        ValidationGrid.SelectedItem = row;
        ValidationGrid.ScrollIntoView(row);
        LoadValidationOptions(row);
    }

    private void DeleteValidationField_Click(object sender, RoutedEventArgs e)
    {
        if (ValidationGrid.SelectedItem is DbValidationListSetting row)
        {
            ValidationRows.Remove(row);
            ValidationOptionsGrid.ItemsSource = null;
            ValidationOptionRows.Clear();
            ValidationOptionsGrid.ItemsSource = ValidationOptionRows;
            _selectedValidationRow = null;
        }
    }

    private void AddValidationValue_Click(object sender, RoutedEventArgs e)
    {
        var nextValue = ValidationOptionRows
            .Select(row => int.TryParse(row.Value, out var v) ? v : -1)
            .DefaultIfEmpty(-1)
            .Max() + 1;
        var row = new ValidationOptionRow { Value = nextValue.ToString(), Name = "New value" };
        ValidationOptionRows.Add(row);
        ValidationOptionsGrid.SelectedItem = row;
        ValidationOptionsGrid.ScrollIntoView(row);
    }

    private void DeleteValidationValue_Click(object sender, RoutedEventArgs e)
    {
        if (ValidationOptionsGrid.SelectedItem is ValidationOptionRow row)
        {
            ValidationOptionRows.Remove(row);
        }
    }

    private void ValidationGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncSelectedValidationOptions();
        if (ValidationGrid.SelectedItem is DbValidationListSetting row)
        {
            LoadValidationOptions(row);
        }
    }

    private void LoadValidationOptions(DbValidationListSetting row)
    {
        _selectedValidationRow = row;
        ValidationOptionRows.Clear();
        foreach (var option in DbValidationListSetting.SplitOptions(row.ValuesText))
        {
            ValidationOptionRows.Add(new ValidationOptionRow { Value = option.Value, Name = option.Text });
        }
    }

    private void SyncSelectedValidationOptions()
    {
        if (_selectedValidationRow is null)
        {
            return;
        }
        _selectedValidationRow.ValuesText = string.Join(
            Environment.NewLine,
            ValidationOptionRows
                .Where(row => !string.IsNullOrWhiteSpace(row.Value) || !string.IsNullOrWhiteSpace(row.Name))
                .Select(row => $"{row.Value.Trim()} - {row.Name.Trim()}"));
    }

    private static bool TryReadPositiveInt(string text, out int value)
        => int.TryParse(text.Trim(), out value) && value > 0;

    public sealed class ValidationOptionRow
    {
        public string Value { get; set; } = "";
        public string Name { get; set; } = "";
    }
}
