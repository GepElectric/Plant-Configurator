using System.IO;
using System.Linq;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;
using Xunit;

namespace CwcConfigurator.Tests;

public class HwAoSyncTests
{
    [Fact]
    public void ImportHwIdsSection_Ao_UpdatesOnlyAnalogOutputs()
    {
        var project = CwcProject.CreateEmpty();
        var path = Path.Combine(Path.GetTempPath(), $"hw_ao_{System.Guid.NewGuid():N}.db");
        File.WriteAllLines(path,
        [
            "DATA_BLOCK \"HW_ID_s\"",
            "BEGIN",
            "   AI.ID[1,0] := 77;",
            "   AO.ID[1,2] := 15;",
            "   AO.CONFIG[1,2] := 1;",
            "END_DATA_BLOCK"
        ]);
        try
        {
            var imported = new TiaPortalExportImporter().ImportHwIdsSection(project, path, "AO");

            Assert.Equal(2, imported);
            var row = project.AnalogOutputs.Single(item => item.ModuleNumber == 1 && item.Port == 2);
            Assert.Equal(15, row.Id);
            Assert.Equal(1, (int)row.Config);
            Assert.DoesNotContain(project.AnalogInputs, item => item.Id == 77);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
