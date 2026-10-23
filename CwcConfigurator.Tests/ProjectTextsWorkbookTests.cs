using System.IO;
using ClosedXML.Excel;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class ProjectTextsWorkbookTests
{
    [Fact]
    public void Apply_WritesAppTextsByEntryNumberMinusOne()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xlsx");
        const string prefix = @"Project library\Types\SCADA_TEXT_LIST\Intake_ALARM_DESCRIPTIONS\V 0.0.4\Text_list\";
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("User Texts");
            sheet.Cell(1, 1).Value = "Category";
            sheet.Cell(1, 2).Value = "ViewPath";
            sheet.Cell(1, 5).Value = "en-US*";
            sheet.Cell(1, 6).Value = "nb-NO";
            sheet.Cell(2, 2).Value = prefix + @"Text_list_entry_440\Text";
            sheet.Cell(2, 5).Value = "Spare";
            sheet.Cell(3, 2).Value = prefix + @"Text_list_entry_1\Text";
            sheet.Cell(3, 5).Value = "First";
            sheet.Cell(3, 6).Value = "Første";
            workbook.SaveAs(path);
        }

        var data = new DescriptionTextListData();
        data.SetText("Intake_ALARM_DESCRIPTIONS", 439, "en-US", "test");
        data.SetText("Intake_ALARM_DESCRIPTIONS", 0, "en-US", "First");

        data.SetText("Intake_ALARM_DESCRIPTIONS", 0, "nb-NO", "Annen");
        var englishOnly = new Dictionary<string, string> { ["en-US"] = "en-US" };

        try
        {
            Assert.Equal(new[] { "en-US", "nb-NO" }, ProjectTextsWorkbook.Languages(path));
            var read = ProjectTextsWorkbook.Read(path);
            Assert.Equal(new[] { "Intake_ALARM_DESCRIPTIONS" }, read.AllListNames);
            Assert.Equal("Spare", read.GetText("Intake_ALARM_DESCRIPTIONS", 439, "en-US"));
            Assert.Equal("Første", read.GetText("Intake_ALARM_DESCRIPTIONS", 0, "nb-NO"));
            Assert.Equal(1, ProjectTextsWorkbook.Apply(path, data, englishOnly, save: false)["Intake_ALARM_DESCRIPTIONS"]);
            Assert.Equal(1, ProjectTextsWorkbook.Apply(path, data, englishOnly, save: true)["Intake_ALARM_DESCRIPTIONS"]);
            Assert.Equal(0, ProjectTextsWorkbook.Apply(path, data, englishOnly, save: false)["Intake_ALARM_DESCRIPTIONS"]);
            Assert.Equal(1, ProjectTextsWorkbook.Apply(path, data, new Dictionary<string, string> { ["en-US"] = "en-US", ["nb-NO"] = "nb-NO" }, save: false)["Intake_ALARM_DESCRIPTIONS"]);

            using var saved = new XLWorkbook(path);
            Assert.Equal("test", saved.Worksheet(1).Cell(2, 5).GetString());
            Assert.Equal("Første", saved.Worksheet(1).Cell(3, 6).GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
