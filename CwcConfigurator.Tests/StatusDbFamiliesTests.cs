using System.IO;
using System.Text;
using ClosedXML.Excel;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class StatusDbFamiliesTests
{
    private static DbCatalog NewCatalog()
    {
        var catalog = new DbCatalog { Sequence = SequenceCatalog.CreateDefault(), DataBlockNames = [] };
        StatusDbFamilies.EnsureDefaults(catalog);
        return catalog;
    }

    [Fact]
    public void PlcWithoutSequence_GetsTheElementsAndTextsOfTheUnit()
    {
        var catalog = NewCatalog();   // e.g. intake: placeholder rows CTRL_STS_00…

        var status = catalog.Sequence.ControlStatusRows;
        Assert.Equal(("HPP_STARTED", "HPP Started"), (status[2].PlcName, status[2].StringNameEnglish));
        Assert.Equal(("SYNC_ENB", "Synchronization Enabled"), (status[36].PlcName, status[36].StringNameEnglish));
        Assert.Equal("SPARE_37", status[37].PlcName);
        Assert.Equal("BRAKE", catalog.Sequence.CommandRows[13].PlcName);
        Assert.Contains(catalog.DataBlockNames, row => row.DbName == "DB CTRL_STS" && row.DataBlockName == "CTRL_STATUS" && row.DataTypeName == "DT_CONTROL_STATUS_ALL");
        Assert.Contains(catalog.DataBlockNames, row => row.DbName == "DB SEQ_CMD" && row.DataBlockName == "SEQ_CMDs_STRING_NAMES_ENG");

        // Named rows (from the user or TIA) are not replaced again.
        status[2].PlcName = "MY_STATUS";
        StatusDbFamilies.EnsureDefaults(catalog);
        Assert.Equal("MY_STATUS", status[2].PlcName);
    }

    [Fact]
    public void Generate_ThenImport_RoundTripsNamesAndBothStringBlocks()
    {
        var catalog = NewCatalog();
        var family = StatusDbFamilies.ControlStatus;
        catalog.Sequence.ControlStatusRows[5].StringNameOther = "Keine Startsperre";
        catalog.Sequence.ControlStatusRows[6].PlcName = "STANDSTILL";
        var folder = Path.Combine(Path.GetTempPath(), "statusdb_" + Guid.NewGuid().ToString("N"));
        var files = StatusDbFamilies.Generate(catalog, family, folder, folder, Encoding.UTF8);

        var type = File.ReadAllText(Path.Combine(folder, "CTRL_STATUS.udt"));
        Assert.Contains("TYPE \"DT_CONTROL_STATUS_ALL\"", type);
        Assert.Contains("\"HPP_STARTED\" { S7_SetPoint := 'False'} : \"DT_STATUS_SOLO\" := ((FALSE, 1, 2),", type);
        Assert.Contains("\"DT_CONTROL_STATUS_ALL\"", File.ReadAllText(Path.Combine(folder, "CTRL_STATUS.db")));
        Assert.Contains("NUM : Array[0..63] of String[72];", File.ReadAllText(Path.Combine(folder, "STRINGS_CTRL_STATUS.udt")));

        var copy = NewCatalog();
        foreach (var row in copy.Sequence.ControlStatusRows) row.PlcName = row.StringNameEnglish = row.StringNameOther = "";
        StatusDbFamilies.Import(copy, family, files);
        Assert.Equal("HPP_STARTED", copy.Sequence.ControlStatusRows[2].PlcName);
        Assert.Equal("HPP Started", copy.Sequence.ControlStatusRows[2].StringNameEnglish);
        Assert.Equal("Keine Startsperre", copy.Sequence.ControlStatusRows[5].StringNameOther);
        Directory.Delete(folder, true);
    }

    [Fact]
    public void Import_SourcesOfTheUnitPlc()
    {
        var catalog = NewCatalog();
        foreach (var row in catalog.Sequence.ControlStatusRows.Concat(catalog.Sequence.CommandRows)) row.PlcName = "";
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TestData", "StatusDbUnit"));
        Assert.Equal("DT_CONTROL_STATUS_ALL", StatusDbFamilies.TypeOfBlock(File.ReadAllText(files.Single(f => f.EndsWith("CTRL_STATUS.db")))));

        StatusDbFamilies.Import(catalog, StatusDbFamilies.ControlStatus, files);
        StatusDbFamilies.Import(catalog, StatusDbFamilies.SequenceCommand, files);

        // Element order is the ID (TIA has some IDs duplicated as 0); quoted names such as "FALSE" are read too.
        Assert.Equal(new[] { "FALSE", "TRUE", "HPP_STARTED" }, catalog.Sequence.ControlStatusRows.Take(3).Select(r => r.PlcName));
        Assert.Equal("HPU_ON", catalog.Sequence.ControlStatusRows[7].PlcName);
        Assert.Equal("HPU ON", catalog.Sequence.ControlStatusRows[7].StringNameEnglish);
        Assert.Equal("BYPASS", catalog.Sequence.CommandRows[5].PlcName);
        Assert.Equal("SPARE_64", catalog.Sequence.ControlStatusRows[63].PlcName);
    }

    [Fact]
    public void TextExport_ThenImport_ChangesNamesAndTextListNames()
    {
        var project = CwcProject.CreateEmpty();
        StatusDbFamilies.EnsureDefaults(project.DbCatalog);
        var path = Path.Combine(Path.GetTempPath(), $"status_text_{Guid.NewGuid():N}.xlsx");
        try
        {
            var service = new DbTextWorkbookService();
            service.Export(project, "DB CTRL_STS", path);
            using (var workbook = new XLWorkbook(path))
            {
                var sheet = workbook.Worksheet("DB CTRL_STS");
                Assert.Equal(new[] { "ID", "PLC NAME", "NAME ENG", "NAME OTHER", "TEXT LIST NAME ENG", "TEXT LIST NAME OTHER" },
                    Enumerable.Range(1, 6).Select(column => sheet.Cell(1, column).GetString()));
                Assert.Equal("HPP_STARTED", sheet.Cell(4, 2).GetString());
                sheet.Cell(4, 4).Value = "HPP gestartet";
                sheet.Cell(4, 6).Value = "Kraftwerk gestartet";
                workbook.Save();
            }

            Assert.Equal(2, service.Import(project, "DB CTRL_STS", path));
            var row = project.DbCatalog.Sequence.ControlStatusRows[2];
            Assert.Equal(("HPP gestartet", "Kraftwerk gestartet", "HPP_STARTED"), (row.StringNameOther, row.NameOther, row.PlcName));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
