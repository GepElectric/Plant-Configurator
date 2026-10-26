using CwcConfigurator.Domain;
using CwcConfigurator.Services;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class DiAlarmOverviewTests
{
    [Fact]
    public void Build_KeepsDigitalSourcesOrderedByModuleAndPort()
    {
        var alarms = new[]
        {
            new DbStringRow { Id = 1, Used = "1", SourceDigId = "12" },  // module 1 port 0
            new DbStringRow { Id = 2, Used = "1", SourceDigId = "0" },   // software
            new DbStringRow { Id = 3, Used = "1", SourceDigId = "5" },   // module 0 port 3
            new DbStringRow { Id = 4, Used = "TRUE", SourceDigId = "7" },   // module 0 port 1
            new DbStringRow { Id = 5, Used = "1", SourceDigId = "", SourceAnaId = "4" }, // analog
            new DbStringRow { Id = 7, Used = "0", SourceDigId = "7" },   // not used
            new DbStringRow { Id = 6, Used = "1", SourceDigId = "40" }   // no HW: DB DI assignment module 2 port 5
        };
        var inputs = new[]
        {
            new DbStringRow { Id = 5, PlcName = "BREAKER_ON" },
            new DbStringRow { Id = 40, PlcName = "DOOR", ModuleName = "2", ModulePort = 5 }
        };
        var hw = new[]
        {
            new DigitalSignalConfig { Id = 12, ModuleNumber = 1, Port = 0, Config = SignalConfigState.Enabled },
            new DigitalSignalConfig { Id = 5, ModuleNumber = 0, Port = 3, Config = SignalConfigState.Enabled },
            new DigitalSignalConfig { Id = 7, ModuleNumber = 0, Port = 1, Config = SignalConfigState.Enabled }
        };

        var links = DiAlarmOverview.Build(alarms, inputs, hw);

        Assert.Equal(new[] { 4, 3, 1, 6 }, links.Select(link => link.Alarm.Id));
        Assert.Equal("BREAKER_ON", links[1].Input?.PlcName);
        Assert.Equal((2, 5), (links[3].Module, links[3].Port));
    }
}
