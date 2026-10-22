using System.IO;
using System.Linq;
using ClosedXML.Excel;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;
using Xunit;

namespace CwcConfigurator.Tests;

public class DbTextWorkbookUsedTests
{
    [Fact]
    public void ExportAll_WritesUsedColumnOnEveryDbSheet()
    {
        var project = CwcProject.CreateEmpty();
        project.DbCatalog.AlarmRows.Add(new DbStringRow { Id = 1, PlcName = "ALM_1", Used = "1" });
        project.DbCatalog.ParameterRows.Add(new DbStringRow { Id = 1, PlcName = "PAR_1", InUse = "In use" });
        var path = Path.Combine(Path.GetTempPath(), $"db_text_{System.Guid.NewGuid():N}.xlsx");
        try
        {
            new DbTextWorkbookService().ExportAll(project, path);

            using var workbook = new XLWorkbook(path);
            foreach (var sheetName in new[] { "DB DI", "DB DO", "DB MEAS", "DB AO", "DB ALM", "DB PAR" })
            {
                Assert.Equal("USED", workbook.Worksheet(sheetName).Cell(1, 2).GetString());
            }

            Assert.Equal("True", workbook.Worksheet("DB ALM").Cell(2, 2).GetString());
            Assert.Equal("In use", workbook.Worksheet("DB PAR").Cell(2, 2).GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
