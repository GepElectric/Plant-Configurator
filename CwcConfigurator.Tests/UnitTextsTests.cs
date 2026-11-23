using CwcConfigurator.App;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class UnitTextsTests
{
    [Fact]
    public void Migration_TurnsCodesIntoThePlcText_Once()
    {
        var catalog = new DbCatalog();   // default list: 0 - -, 15 - %, 17 - bar, 20 - m …
        catalog.MeasRows.Add(new DbStringRow { Unit = "20" });
        catalog.MeasRows.Add(new DbStringRow { Unit = "0" });
        catalog.MeasRows.Add(new DbStringRow { Unit = "17 - bar" });
        catalog.AoRows.Add(new DbAoRow { Unit = "15" });
        catalog.ParameterRows.Add(new DbStringRow { Unit = "0" });
        catalog.ParameterRows.Add(new DbStringRow { Unit = "20" });

        // What the generator wrote with codes is what the text must be afterwards.
        var before = catalog.MeasRows.Select(row => UnitTexts.Text(catalog, row.Unit))
            .Concat(catalog.AoRows.Select(row => UnitTexts.Text(catalog, row.Unit)))
            .Concat(catalog.ParameterRows.Select(row => UnitTexts.Text(catalog, row.Unit, parameter: true))).ToList();
        Assert.Equal(["m", "-", "bar", "%", "", "m"], before);

        Assert.True(UnitTexts.Migrate(catalog));
        Assert.True(catalog.UnitsAreText);
        Assert.Equal(["m", "-", "bar"], catalog.MeasRows.Select(row => row.Unit));
        Assert.Equal("%", catalog.AoRows[0].Unit);
        Assert.Equal(["", "m"], catalog.ParameterRows.Select(row => row.Unit));

        // From now on the unit is free text: typed digits stay digits, nothing is looked up again.
        catalog.ParameterRows[0].Unit = "20";
        Assert.False(UnitTexts.Migrate(catalog));
        Assert.Equal("20", UnitTexts.Text(catalog, catalog.ParameterRows[0].Unit, parameter: true));
        Assert.Equal("kg/h", UnitTexts.Text(catalog, " kg/h "));
        Assert.Equal("-", AlmParExportDialog.UnitText(catalog, ""));
        Assert.Equal("m", AlmParExportDialog.UnitText(catalog, "m"));
    }
}
