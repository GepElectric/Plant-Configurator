using System.Linq;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.Eplan;
using Xunit;

namespace CwcConfigurator.Tests;

public class EplanHardwareMapperTests
{
    private static EplanModule Module(string tag, EplanModuleKind kind, int channels) => new()
    {
        DeviceTag = tag,
        Kind = kind,
        Type = kind.ToString(),
        Channels = Enumerable.Range(0, channels).Select(p => new EplanChannel
        {
            Port = kind == EplanModuleKind.Counter ? null : p,
            Terminal = (p + 1).ToString(),
            SignalName = $"{tag} SIGNAL {p}",
            ConnectedTo = $"-K{p}:14",
            Schematic = new EplanPageRef { Function = "X", Page = "10", Column = p % 8 + 1, GlobalPage = 100 + p / 8 }
        }).ToList()
    };

    private static EplanStation Station(string fn, bool cpu, params EplanModule[] modules) => new()
    {
        Function = fn,
        HeadDeviceTag = cpu ? "A101" : "A301",
        HeadType = cpu ? "CPU 1512SP-1 PN" : "IM 155-6PN BA",
        IsCpu = cpu,
        Modules = modules
    };

    // Unit 1 CPU with 2 DI cards, unit 1 distributed station with 1 DI card, common distributed station for unit 0.
    private static EplanProject Sample() => new()
    {
        Stations =
        [
            Station("0CJA01", true, Module("A111", EplanModuleKind.DigitalInput, 16)),
            Station("1CJA01", true,
                Module("A111", EplanModuleKind.DigitalInput, 16),
                Module("A112", EplanModuleKind.DigitalInput, 16),
                Module("A154", EplanModuleKind.RtdInput, 4),
                Module("A181", EplanModuleKind.Counter, 3)),
            Station("1MKV10", false, Module("A111", EplanModuleKind.DigitalInput, 16)),
            Station("0BFA01", false, Module("A311", EplanModuleKind.DigitalInput, 16))
        ]
    };

    [Fact]
    public void AutoMap_AssignsDistributedStationsAndDatabasesByUnitNumber()
    {
        var unit0 = PlcDatabase.CreateDefault("Central");
        var unit1 = PlcDatabase.CreateDefault("Unit 1");
        var map = EplanHardwareMapper.AutoMap(Sample(), [unit0, unit1]);

        var cpu0 = map.Single(a => a.CpuFunction == "0CJA01");
        var cpu1 = map.Single(a => a.CpuFunction == "1CJA01");
        Assert.Equal(["0BFA01"], cpu0.DistributedFunctions);
        Assert.Equal(["1MKV10"], cpu1.DistributedFunctions);
        Assert.Equal(unit1.Id, cpu1.PlcDatabaseId);
        Assert.Equal("", cpu0.PlcDatabaseId);       // no database name carries unit 0 -> left for the user
    }

    [Fact]
    public void AutoMap_KeepsPreviousManualDecisions()
    {
        var unit1 = PlcDatabase.CreateDefault("Unit 1");
        var previous = new[] { new EplanCpuAssignment { CpuFunction = "0CJA01", DistributedFunctions = ["1MKV10"] } };

        var map = EplanHardwareMapper.AutoMap(Sample(), [unit1], previous);

        Assert.Equal(["1MKV10", "0BFA01"], map.Single(a => a.CpuFunction == "0CJA01").DistributedFunctions);
        Assert.Empty(map.Single(a => a.CpuFunction == "1CJA01").DistributedFunctions);
    }

    [Fact]
    public void BuildPlan_NumbersModulesPerKindCpuFirstThenStationsInOrder()
    {
        var plan = EplanHardwareMapper.BuildPlan(Sample(), new EplanCpuAssignment { CpuFunction = "1CJA01", DistributedFunctions = ["1MKV10"] });

        var di = plan.Modules.Where(m => m.Kind == EplanModuleKind.DigitalInput).ToList();
        Assert.Equal([0, 1, 2], di.Select(m => m.ModuleNumber));
        // A111 exists in the CPU and in the station -> names are made unique with the function
        Assert.Equal(["1CJA01-A111", "A112", "1MKV10-A111"], di.Select(m => m.ModuleName));
        Assert.Equal(0, plan.Modules.Single(m => m.Kind == EplanModuleKind.RtdInput).ModuleNumber);
        Assert.Equal(16 * 3 + 4 + 1, plan.Channels.Count);              // counter = one row
        Assert.Null(plan.Channels.Single(c => c.Kind == EplanModuleKind.Counter).Port);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void BuildPlan_WarnsWhenTableIsFull()
    {
        var modules = Enumerable.Range(0, 7).Select(i => Module($"A15{i}", EplanModuleKind.AnalogInput, 8)).ToArray();
        var project = new EplanProject { Stations = [Station("1CJA01", true, modules)] };

        var plan = EplanHardwareMapper.BuildPlan(project, new EplanCpuAssignment { CpuFunction = "1CJA01" });

        Assert.Equal(6, plan.Modules.Count);
        Assert.Contains(plan.Warnings, w => w.Contains("A156") && w.Contains("6 modules"));
    }

    [Fact]
    public void Apply_WritesEplanColumnsAndModuleNamesButKeepsProgramData()
    {
        var database = PlcDatabase.CreateDefault("Unit 1");
        var row = database.DigitalInputs.Single(r => r.ModuleNumber == 1 && r.Port == 5);
        row.Id = 42;
        row.Config = SignalConfigState.Enabled;
        var stale = database.DigitalInputs.Single(r => r.ModuleNumber == 15 && r.Port == 0);
        stale.EplanSignalName = "OLD";

        var plan = EplanHardwareMapper.BuildPlan(Sample(), new EplanCpuAssignment { CpuFunction = "1CJA01" });
        var result = EplanHardwareMapper.Apply(plan, database);

        Assert.Equal("A112 SIGNAL 5", row.EplanSignalName);
        Assert.Equal("-K5:14", row.EplanConnectedTo);
        Assert.Equal(100, row.EplanPdfPage);
        Assert.Equal("=X/10.6", row.EplanSchematicPage);
        Assert.Equal(42, row.Id);                                    // program data untouched
        Assert.Equal(SignalConfigState.Enabled, row.Config);
        Assert.Equal("", stale.EplanSignalName);                     // re-import clears rows EPLAN no longer has
        Assert.Equal("A112", database.ModuleCatalog.DigitalInputs.Single(m => m.ModuleNumber == 1).ModuleName);
        Assert.Equal("A154", database.ModuleCatalog.RtdInputs.Single(m => m.ModuleNumber == 0).ModuleName);
        Assert.Equal(1, result.RowsCleared);
        Assert.Equal(plan.Channels.Count, result.ChannelsWritten);
    }
}
