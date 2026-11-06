using CwcConfigurator.Domain;
using CwcConfigurator.Services.Simulation.Process;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class CubicleSignalsTests
{
    [Fact]
    public void Devices_GetNamesAndSignals_FromTheSignalTable()
    {
        var inputs = new[]
        {
            new DigitalSignalConfig { Id = 69, Config = SignalConfigState.Enabled, SignalName = "Gate 1 pushbutton open (-S210)" },
            new DigitalSignalConfig { Id = 364, Config = SignalConfigState.Enabled, SignalName = "Dam pump switch (-S3) - Local" },
            new DigitalSignalConfig { Id = 365, Config = SignalConfigState.Enabled, SignalName = "Dam pump switch (-S3) - Automatic" },
            new DigitalSignalConfig { Id = 72, Config = SignalConfigState.Enabled, SignalName = "Gate 1 motor protection MCB ON (-F51)" }
        };
        var model = new ProcessModel
        {
            RelaySwitches = [new RelaySwitch { Tag = "-S3", Positions = "LOCAL/OFF/AUTO" }],
            RelaySheets = [new RelaySheet { Circuits = [new RelayCircuit { Elements = [new RelayElement { Tag = "-F51" }], Coil = new RelayCoil { Tag = "-H104:LAMP" } }] }]
        };
        var button = new CubicleItem { Kind = CubicleItemKind.PushButton, Tag = "-S210" };
        var selector = new CubicleItem { Kind = CubicleItemKind.Switch, Tag = "-S3" };
        var lamp = new CubicleItem { Kind = CubicleItemKind.Lamp, Tag = "-H104", Source = "-H104:LAMP" };

        Assert.Equal(3, CubicleSignals.Link(model, [button, selector, lamp], inputs, []));
        Assert.Equal(("Gate 1 pushbutton open", "DI:69"), (button.Text, button.Outputs));
        Assert.Equal("Dam pump switch", selector.Text);
        Assert.Equal("DI:364", selector.OutputsByPosition()["LOCAL"]);
        Assert.Equal("DI:365", selector.OutputsByPosition()["AUTO"]);
        Assert.Equal("Gate 1 motor protection MCB ON", lamp.Text);
    }
}
