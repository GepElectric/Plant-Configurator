using System.Linq;
using CwcConfigurator.Domain;
using CwcConfigurator.Services.Simulation;
using Xunit;

namespace CwcConfigurator.Tests;

public class SimulatorTests
{
    [Fact]
    public void DefaultLayout_MatchesTiaStandardDb()
    {
        // DB30 "SIMULATOR" with DI 16×16, AI 6×8, COUNTER 4, RTD 6×8, DO 16×16, AO 3×4 (HPP Vihtakoski constants).
        var layout = SimulatorLayout.Default;

        Assert.Equal(32, layout.AiStart);
        Assert.Equal(128, layout.CounterStart);
        Assert.Equal(144, layout.RtdStart);
        Assert.Equal(240, layout.DoStart);
        Assert.Equal(272, layout.AoStart);
        Assert.Equal(296, layout.TotalBytes);
        Assert.Equal((21, 1), layout.DiBit(10, 9));
        Assert.Equal((240 + 3, 7), layout.DoBit(1, 15));
        Assert.Equal(32 + (2 * 8 + 3) * 2, layout.AiWord(2, 3));
        Assert.Equal(272 + (1 * 4 + 2) * 2, layout.AoWord(1, 2));
    }

    [Fact]
    public void LayoutFromPlcConstants_RoundTrips()
    {
        // TIA constants of HPP Vihtakoski (Common/Intake/Unit): DI 15/15, AI 5/7, COUNTER 3, RTD 5/7, DO 15, AO 2/3.
        var layout = SimulatorLayout.FromConstants([15, 15, 5, 7, 3, 5, 7, 15, 2, 3]);

        Assert.Equal(SimulatorLayout.Default, layout);
        Assert.Equal(new[] { 15, 15, 5, 7, 3, 5, 7, 15, 2, 3 }, layout.ToConstants());
    }

    [Fact]
    public void LayoutFromProject_NeverSmallerThanTemplate()
    {
        var project = new CwcProject();
        project.AnalogOutputs.Add(new AnalogOutputConfig { ModuleNumber = 1, Port = 3 });

        Assert.Equal(3, SimulatorLayout.FromProject(project).AoModules);
    }

    [Fact]
    public void BoolModules_StartOnEvenBytes()
    {
        // 8 channels = 1 byte of Bools, padded to 2 bytes per module in a standard DB.
        var layout = new SimulatorLayout(4, 8, 1, 2, 1, 1, 2, 4, 8, 1, 2);

        Assert.Equal((2, 3), layout.DiBit(1, 3));
        Assert.Equal(8, layout.AiStart);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(100.0, 27648)]
    [InlineData(50.0, 13824)]
    public void AiScaling_LinearToRaw(double value, int raw)
    {
        var scaling = new AnalogScaling(0, 27648, 0, 100);

        Assert.Equal(raw, scaling.ToRaw(value));
        Assert.Equal(value, scaling.ToEngineering(raw), 3);
    }

    [Fact]
    public void AiScaling_OffsetAndScaleFactorAreInverted()
    {
        // VAL = (VAL_1 + lin + OFFSET) * SF  =>  raw for VAL 12 with offset 2 and SF 2 is the raw of lin value 4.
        var scaling = new AnalogScaling(0, 27648, 0, 10, Offset: 2, ScaleFactor: 2);

        var raw = scaling.ToRaw(12);

        Assert.Equal(11059, raw);
        Assert.Equal(12, scaling.ToEngineering(raw), 2);
    }

    [Fact]
    public void AiScaling_OffsetShiftsTheMeasuringRange()
    {
        // Intake water level: 0…20 m with OFFSET 69.69 → 69.69…89.69 m; 0 m cannot be written.
        var scaling = new AnalogScaling(0, 27648, 0, 20, Offset: 69.69);

        Assert.Equal((69.69, 89.69), (System.Math.Round(scaling.EngineeringRange.Low, 2), System.Math.Round(scaling.EngineeringRange.High, 2)));
        Assert.Equal(13824, scaling.ToRaw(79.69));
        Assert.True(scaling.ToRawExact(0) < short.MinValue);
        Assert.Equal("69.69…89.69 m = raw 0…27648", scaling.Describe("m"));
    }

    [Theory]
    [InlineData(-32768, true)]
    [InlineData(32000, true)]
    [InlineData(31999, false)]
    [InlineData(-4864, false)]
    public void ErrorRawValues_MatchFbAi(int raw, bool error)
    {
        Assert.Equal(error, AnalogScaling.IsErrorRaw(raw));
    }

    [Fact]
    public void RtdScaling_TemperatureIsTenths()
    {
        var scaling = new AnalogScaling(0, 0, 0, 0, TenthsOfValue: true);

        Assert.Equal(556, scaling.ToRaw(55.6));
        Assert.Equal(55.6, scaling.ToEngineering(556), 3);
    }

    [Fact]
    public void PlanBuilder_UsesDbNamesAndParameterScaling()
    {
        var project = new CwcProject();
        project.DigitalInputs.Add(new DigitalSignalConfig { ModuleNumber = 1, Port = 2, Id = 7, Config = SignalConfigState.Enabled });
        project.AnalogInputs.Add(new AnalogSignalConfig { ModuleNumber = 0, Port = 1, Id = 4, Config = SignalConfigState.Enabled });
        project.RtdInputs.Add(new AnalogSignalConfig { ModuleNumber = 0, Port = 0, Id = 5, Config = SignalConfigState.Enabled });
        project.DigitalOutputs.Add(new DigitalSignalConfig { ModuleNumber = 0, Port = 0, Id = 3, Config = SignalConfigState.Disabled });
        project.DbCatalog.DiRows.Add(new DbStringRow { Id = 7, SystemNameEnglish = "Unit 1", NameEnglish = "Breaker closed", PlcName = "GCB_ON" });
        project.DbCatalog.MeasRows.Add(new DbStringRow { Id = 4, NameEnglish = "Nozzle 1", Unit = "%", ParVal1Id = "10", ParVal2Id = "11 - MAX" });
        project.DbCatalog.ParameterRows.Add(new DbStringRow { Id = 10, RealValue = "0.0" });
        project.DbCatalog.ParameterRows.Add(new DbStringRow { Id = 11, RealValue = "100,0" });

        var signals = SimulatorPlanBuilder.Build(project, SimulatorLayout.Default);

        var di = signals.Single(signal => signal.Kind == SimulatorSignalKind.DI);
        Assert.Equal("Breaker closed", di.Name);
        Assert.Equal("GCB_ON", di.PlcName);
        Assert.Equal("DBX2.2", di.Address);
        Assert.True(di.CanSetBool);

        var ai = signals.Single(signal => signal.Kind == SimulatorSignalKind.AI);
        Assert.Equal("DBW34", ai.Address);
        Assert.True(ai.HasScaling);
        Assert.Equal(27648, ai.Scaling!.ToRaw(100));

        var rtd = signals.Single(signal => signal.Kind == SimulatorSignalKind.RTD);
        Assert.Equal(556, rtd.Scaling!.ToRaw(55.6));

        var output = signals.Single(signal => signal.Kind == SimulatorSignalKind.DO);
        Assert.False(output.IsEnabled);
        Assert.False(output.IsWritable);
    }

    [Fact]
    public void Signal_ReadsBigEndianIntAndBits()
    {
        var ai = new SimulatorSignal { Kind = SimulatorSignalKind.AI, Module = 0, Channel = 0, Id = 1, ByteOffset = 2, Scaling = new AnalogScaling(0, 27648, 0, 100), Unit = "%" };
        var di = new SimulatorSignal { Kind = SimulatorSignalKind.DI, Module = 0, Channel = 3, Id = 2, ByteOffset = 0, BitOffset = 3 };
        var data = new byte[] { 0b0000_1000, 0, 0x6C, 0x00 };

        ai.UpdateFrom(data);
        di.UpdateFrom(data);

        Assert.Equal(27648, ai.RawValue);
        Assert.Equal("100 %", ai.ValueText);
        Assert.Equal("TRUE", di.ValueText);
    }
}
