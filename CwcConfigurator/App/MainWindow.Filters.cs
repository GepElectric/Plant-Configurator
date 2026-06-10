using System.Windows.Controls;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public partial class MainWindow
{
    private bool FilterDiRow(object item)
    {
        return item is not DigitalSignalConfig row || IsModuleVisible(DiModuleFilter, row.ModuleNumber);
    }

    private bool FilterDoRow(object item)
    {
        return item is not DigitalSignalConfig row || IsModuleVisible(DoModuleFilter, row.ModuleNumber);
    }

    private bool FilterAiRow(object item)
    {
        return item is not AnalogSignalConfig row || IsModuleVisible(AiModuleFilter, row.ModuleNumber);
    }

    private bool FilterRtdRow(object item)
    {
        return item is not AnalogSignalConfig row || IsModuleVisible(RtdModuleFilter, row.ModuleNumber);
    }

    private bool FilterCounterRow(object item)
    {
        return item is not CounterConfig row || IsModuleVisible(CounterModuleFilter, row.ModuleNumber);
    }

    private bool FilterAoRow(object item)
    {
        return item is not AnalogOutputConfig row || IsModuleVisible(AoModuleFilter, row.ModuleNumber);
    }

    private static bool IsModuleVisible(ListBox slicer, int moduleNumber)
    {
        var selectedItems = slicer.SelectedItems
            .OfType<ModuleFilterItem>()
            .ToList();

        return selectedItems.Count == 0
            || selectedItems.Any(item => item.ModuleNumber is null)
            || selectedItems.Any(item => item.ModuleNumber == moduleNumber);
    }

    private static List<ModuleFilterItem> BuildModuleFilterItems(
        IEnumerable<int> moduleNumbers,
        IReadOnlyList<ModuleNameRow> moduleNames)
    {
        var items = moduleNumbers
            .Distinct()
            .OrderBy(value => value)
            .Select(moduleNumber =>
            {
                var moduleName = moduleNames.FirstOrDefault(row => row.ModuleNumber == moduleNumber)?.ModuleName;
                var displayText = string.IsNullOrWhiteSpace(moduleName)
                    ? moduleNumber.ToString()
                    : $"{moduleNumber} - {moduleName}";

                return new ModuleFilterItem(moduleNumber, displayText);
            })
            .ToList();

        items.Insert(0, new ModuleFilterItem(null, "All"));
        return items;
    }
}
