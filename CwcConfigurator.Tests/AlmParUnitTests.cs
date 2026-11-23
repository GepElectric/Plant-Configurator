using CwcConfigurator.App;
using CwcConfigurator.Domain;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class AlmParUnitTests
{
    [Fact]
    public void ParameterUnitCode_IsWrittenAsTheUnitText()
    {
        var catalog = new DbCatalog();   // default unit list: 0 - -, 15 - %, 16 - °C, 17 - bar …
        Assert.Equal("%", AlmParExportDialog.UnitText(catalog, "15"));
        Assert.Equal("bar", AlmParExportDialog.UnitText(catalog, "17"));
        Assert.Equal("-", AlmParExportDialog.UnitText(catalog, "0"));
        Assert.Equal("-", AlmParExportDialog.UnitText(catalog, ""));
        Assert.Equal("kW", AlmParExportDialog.UnitText(catalog, "kW"));   // already text

        // The project's own list applies (e.g. extended with times).
        catalog.ValidationLists.Single(list => list.Key == DbValidationDefaults.DbMeasUnit).ValuesText += "\n22 - ms\n23 - s";
        Assert.Equal("s", AlmParExportDialog.UnitText(catalog, "23"));
    }

    [Fact]
    public void TimeParameter_IsWrittenInSeconds()
    {
        Assert.Equal(30, AlmParExportDialog.ParseTiaTime("T#30S"));
        Assert.Equal(70, AlmParExportDialog.ParseTiaTime("T#1M_10S"));
        Assert.Equal(0.5, AlmParExportDialog.ParseTiaTime("T#500MS"));
        Assert.Equal(0, AlmParExportDialog.ParseTiaTime("T#0MS"));
        Assert.Equal(3723.5, AlmParExportDialog.ParseTiaTime("t#1h2m3s500ms"));
        Assert.Null(AlmParExportDialog.ParseTiaTime("12.5"));
        Assert.Equal(30, AlmParExportDialog.TimeSeconds(new DbStringRow { ParameterType = "2", TimeValue = "T#30S" }));
        Assert.Null(AlmParExportDialog.TimeSeconds(new DbStringRow { ParameterType = "0", RealValue = "1.0" }));
    }
}
