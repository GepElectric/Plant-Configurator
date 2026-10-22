using System.IO;
using System.Linq;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.Eplan;
using Xunit;

namespace CwcConfigurator.Tests;

public class EplanIdWriterTests
{
    [Theory]
    [InlineData("I0.0", 123, "I0.0 - ID 123")]
    [InlineData("I0.0 - ID 5", 123, "I0.0 - ID 123")]      // written again: the ID is replaced, not appended
    [InlineData("IW256 -ID7", 8, "IW256 - ID 8")]
    [InlineData("", 9, "ID 9")]
    public void WithId_FormatsAddress(string address, int id, string expected)
    {
        Assert.Equal(expected, EplanIdWriter.WithId(address, id));
    }

    [Fact]
    public void Write_PutsIdOnSchematicAndOverviewAndKeepsSource()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"eplan_ids_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "plant.epj");
            var target = Path.Combine(folder, "plant - IDs.epj");
            File.WriteAllText(source, EplanProjectReaderTests.SampleEpj.TrimStart());
            var before = File.ReadAllText(source);

            var result = EplanIdWriter.Write(source, target, new Dictionary<string, int>
            {
                ["1CJA01|A111|2"] = 77,
                ["9XXX99|A1|1"] = 5          // not in the project
            });

            Assert.Equal(before, File.ReadAllText(source));
            Assert.Equal(2, result.TerminalsWritten);        // schematic + PLC overview
            Assert.Equal(2, result.PagesChanged);
            Assert.Equal(["9XXX99|A1|1"], result.NotFound);

            var channel = EplanProjectReader.Read(target).Stations[0].Modules[0].Channels.Single(c => c.Port == 1);
            Assert.Equal("I0.1 - ID 77", channel.Address);
            Assert.Equal("STEP-UP TRANSFORMER BUCHOLZ ALARM", channel.SignalName);   // everything else as before
            Assert.Equal("-1K12:14", channel.ConnectedTo);

            Assert.Throws<InvalidOperationException>(() => EplanIdWriter.Write(source, source, new Dictionary<string, int>()));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void CollectIds_TakesEveryRowInUse_IdZeroIncluded()
    {
        var db = PlcDatabase.CreateDefault("Unit 1");
        db.DigitalInputs[0].EplanTerminalRef = "1CJA01|A111|1";      // I0.0 with the real signal ID 0
        db.DigitalInputs[0].Id = 0;
        db.DigitalInputs[0].Config = SignalConfigState.Inverted;
        db.DigitalInputs[1].EplanTerminalRef = "1CJA01|A111|2";
        db.DigitalInputs[1].Id = 11;
        db.DigitalInputs[1].Config = SignalConfigState.Disabled;      // not in use
        db.DigitalInputs[2].EplanTerminalRef = "1CJA01|A111|3";      // default row: disabled, ID 0

        var ids = EplanIdWriter.CollectIds([db]);

        Assert.Equal(new Dictionary<string, int> { ["1CJA01|A111|1"] = 0 }, ids);
    }

    /// <summary>Real project (not in the repo): every mapped terminal gets its ID and the file still reads the same.</summary>
    [Fact]
    public void Write_RealProject_RoundTrip()
    {
        const string source = @"C:\Users\Mazda\Desktop\Sheme\HPP Stori2.epj";
        if (!File.Exists(source))
        {
            return;
        }

        var project = EplanProjectReader.Read(source);
        var db = PlcDatabase.CreateDefault("Unit 2");
        EplanHardwareMapper.Apply(EplanHardwareMapper.BuildPlan(project, new EplanCpuAssignment { CpuFunction = "2CJA01" }), db);
        var id = 1;
        foreach (var row in db.DigitalInputs.Where(r => r.EplanTerminalRef.Length > 0))
        {
            row.Id = id++;
            row.Config = SignalConfigState.Enabled;
        }

        var target = Path.Combine(Path.GetTempPath(), $"stori_ids_{Guid.NewGuid():N}.epj");
        try
        {
            var result = EplanIdWriter.Write(source, target, EplanIdWriter.CollectIds([db]));

            Assert.Empty(result.NotFound);
            // nothing but the addresses may change: line breaks / tabs of texts (&#10; &#9;) stay as EPLAN wrote them
            var sourceText = File.ReadAllText(source);
            var targetText = File.ReadAllText(target);
            Assert.Equal(sourceText, System.Text.RegularExpressions.Regex.Replace(targetText, " - ID \\d+\"", "\""));

            var written = EplanProjectReader.Read(target);
            Assert.Equal(project.Pages.Count, written.Pages.Count);
            var a115 = written.Stations.Single(s => s.Function == "2CJA01").Modules.Single(m => m.DeviceTag == "A115");
            var di1 = a115.Channels.Single(c => c.Port == 1);
            var expectedId = db.DigitalInputs.Single(r => r.EplanTerminalRef == "2CJA01|A115|2").Id;
            Assert.Equal($"I8.1 - ID {expectedId}", di1.Address);
            Assert.Equal("-1K12:14", di1.ConnectedTo);
        }
        finally
        {
            File.Delete(target);
        }
    }
}
