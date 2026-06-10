using System.Windows;
using CwcConfigurator.Services.Validation;

namespace CwcConfigurator.App;

public partial class MainWindow
{
    private void SyncMultilingualFromProject()
    {
        var multilingual = _project.Multilingual;
        RepairContext.IsMultilingual = multilingual;
        if (MultilingualMenuItem is not null)
        {
            MultilingualMenuItem.IsChecked = multilingual;
        }
    }

    private void MultilingualMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var newValue = MultilingualMenuItem.IsChecked;
        _project.Multilingual = newValue;
        RepairContext.IsMultilingual = newValue;
        RecordUndoSnapshot();
        RefreshAllDbGridsAfterMultilingualToggle();
        ShowMessage(newValue
            ? "Multilingual mode ON — *Other text fields are validated."
            : "Monolingual mode — *Other text fields are skipped by repair/validate.");
    }

    private void RefreshAllDbGridsAfterMultilingualToggle()
    {
        // Repair summary columns are computed on the fly; we just nudge each
        // collection view to re-evaluate (so rows leaving/entering repair scope
        // update) and request a visual refresh on the grids.
        _dbDiView?.Refresh();
        _dbDoView?.Refresh();
        _dbMeasView?.Refresh();
        _dbAoRowsView?.Refresh();
        _dbAlmView?.Refresh();
        _dbParView?.Refresh();
        DbDiGrid?.Items.Refresh();
        DbDoGrid?.Items.Refresh();
        DbMeasGrid?.Items.Refresh();
        DbAoGrid?.Items.Refresh();
        DbAlmGrid?.Items.Refresh();
        DbParGrid?.Items.Refresh();
    }
}
