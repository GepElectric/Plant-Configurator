using CwcConfigurator.Domain;
using CwcConfigurator.Services;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class AlarmSourceOverviewTests
{
    [Fact]
    public void Build_GroupsUsedAlarmsBySourceAndOrdersByModulePortAndId()
    {
        var alarms = new[]
        {
            Alarm(10, "1", dig: "12"),             // DI module 1 port 0
            Alarm(11, "1", dig: "7"),              // DI module 0 port 1
            Alarm(12, "1", dig: "5"),              // DI module 0 port 3
            Alarm(13, "0"),                        // program
            Alarm(3, "0"),                         // program, lower ID first
            Alarm(20, "2", ana: "4"),              // AI module 0 port 2
            Alarm(21, "2", ana: "4"),              // same AI port: by ID
            Alarm(22, "2", ana: "30"),             // RTD module 3 port 0
            Alarm(23, "3", dig: "7", ana: "30"),   // both: DI and RTD
            Alarm(24, "1", dig: "7", used: "0")    // not used
        };
        var hwDi = new[]
        {
            new DigitalSignalConfig { Id = 12, ModuleNumber = 1, Port = 0, Config = SignalConfigState.Enabled },
            new DigitalSignalConfig { Id = 5, ModuleNumber = 0, Port = 3, Config = SignalConfigState.Enabled },
            new DigitalSignalConfig { Id = 7, ModuleNumber = 0, Port = 1, Config = SignalConfigState.Enabled }
        };
        var hwAi = new[] { new AnalogSignalConfig { Id = 4, ModuleNumber = 0, Port = 2, Config = SignalConfigState.Enabled } };
        var hwRtd = new[] { new AnalogSignalConfig { Id = 30, ModuleNumber = 3, Port = 0, Config = SignalConfigState.Enabled } };

        var links = AlarmSourceOverview.Build(alarms, [new DbStringRow { Id = 5, PlcName = "BREAKER_ON" }], [], hwDi, hwAi, hwRtd);

        Assert.Equal(new[] { 11, 23, 12, 10 }, Ids(links, AlarmSourceCategory.DigitalInput));
        Assert.Equal(new[] { 20, 21 }, Ids(links, AlarmSourceCategory.AnalogInput));
        Assert.Equal(new[] { 22, 23 }, Ids(links, AlarmSourceCategory.Rtd));
        Assert.Equal(new[] { 3, 13 }, Ids(links, AlarmSourceCategory.Program));
        Assert.Equal("BREAKER_ON", links.Single(link => link.Alarm.Id == 12).Source?.PlcName);
    }

    [Fact]
    public void Build_WithoutSourceType_UsesSourceIdsAndDbAssignment()
    {
        var alarms = new[] { Alarm(1, "", dig: "40"), Alarm(2, "") };
        var inputs = new[] { new DbStringRow { Id = 40, PlcName = "DOOR", ModuleName = "2", ModulePort = 5 } };

        var links = AlarmSourceOverview.Build(alarms, inputs, [], [], [], []);

        var door = links.Single(link => link.Category == AlarmSourceCategory.DigitalInput);
        Assert.Equal(("2", (int?)5), (door.Module, door.Port));
        Assert.Equal(2, links.Single(link => link.Category == AlarmSourceCategory.Program).Alarm.Id);
    }

    [Fact]
    public void Build_DigitalInputZeroIsAnInput_AndInputWithoutHardwareIsMarked()
    {
        var alarms = new[] { Alarm(392, "1", dig: "0", ana: "0"), Alarm(104, "1", dig: "1", ana: "0") };
        var inputs = new[] { new DbStringRow { Id = 1, PlcName = "CTRL_LOCAL" } };
        var hwDi = new[] { new DigitalSignalConfig { Id = 0, ModuleNumber = 0, Port = 0, Config = SignalConfigState.Enabled } };

        var links = AlarmSourceOverview.Build(alarms, inputs, [], hwDi, [], []);

        Assert.Equal(new[] { 392, 104 }, Ids(links, AlarmSourceCategory.DigitalInput));
        Assert.Equal(("0", (int?)0, (int?)0), (links[0].Module, links[0].Port, links[0].SourceId));
        Assert.Equal(AlarmSourceOverview.NoHardware, links[1].Module);
        Assert.Empty(Ids(links, AlarmSourceCategory.AnalogInput));
    }

    private static DbStringRow Alarm(int id, string type, string dig = "", string ana = "", string used = "1")
    {
        return new DbStringRow { Id = id, Used = used, SourceType = type, SourceDigId = dig, SourceAnaId = ana };
    }

    private static int[] Ids(IEnumerable<AlarmSourceLink> links, AlarmSourceCategory category)
    {
        return links.Where(link => link.Category == category).Select(link => link.Alarm.Id).ToArray();
    }
}
