using System.Collections.ObjectModel;
using System.Windows;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App.Settings;

public partial class DbDataBlockSettingsView : SettingsEditorViewBase
{
    private readonly Action<IEnumerable<DbDataBlockNameSetting>> _commit;
    public ObservableCollection<DbDataBlockNameSetting> Rows { get; }

    public DbDataBlockSettingsView(
        IEnumerable<DbDataBlockNameSetting> rows,
        Action<IEnumerable<DbDataBlockNameSetting>> commit)
    {
        InitializeComponent();
        _commit = commit;
        Rows = new ObservableCollection<DbDataBlockNameSetting>(rows.Select(r => r.Clone()));
        DataBlockNamesGrid.ItemsSource = Rows;
    }

    public override bool Apply()
    {
        _commit(Rows);
        return true;
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        var nextNumber = Rows.Count == 0 ? 1 : Rows.Max(row => row.Number) + 1;
        var row = new DbDataBlockNameSetting
        {
            Number = nextNumber,
            BlockKind = DbDataBlockNameDefaults.StringKind
        };
        Rows.Add(row);
        DataBlockNamesGrid.SelectedItem = row;
        DataBlockNamesGrid.ScrollIntoView(row);
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        var selected = DataBlockNamesGrid.SelectedItems
            .OfType<DbDataBlockNameSetting>()
            .ToList();
        foreach (var row in selected)
        {
            Rows.Remove(row);
        }
    }
}
