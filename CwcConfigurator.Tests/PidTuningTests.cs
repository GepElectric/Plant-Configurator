using CwcConfigurator.Domain;
using CwcConfigurator.Services.PidTuning;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class PidTuningTests
{
    /// <summary>Output step of 5 % at 10 s, sampled every 100 ms, through the given process.</summary>
    private static (List<double> T, List<double> U, List<double> Y) Record(Func<double, double, double, double> step, double duration, double noise = 0.02)
    {
        var random = new Random(7);
        var dt = 0.1;
        var t = new List<double>();
        var u = new List<double>();
        var y = new List<double>();
        var state = 50.0;
        var history = new List<double>();
        for (var k = 0; k * dt <= duration; k++)
        {
            var time = k * dt;
            var output = time < 10 ? 40 : 45;
            history.Add(output);
            state = step(state, time, dt);
            t.Add(time);
            u.Add(output);
            y.Add(state + (random.NextDouble() - 0.5) * noise);
        }

        return (t, u, y);
    }

    [Fact]
    public void FirstOrder_IdentifiedAndTuned()
    {
        // K = -1.5, T = 12 s, L = 2 s (a reverse-acting process).
        double K = -1.5, T = 12, L = 2;
        var (t, u, y) = Record((state, time, dt) =>
        {
            var applied = time - L < 10 ? 40 : 45;
            return state + (50 + K * (applied - 40) - state) * (1 - Math.Exp(-dt / T));
        }, 90);

        var model = PidTuner.Identify(t, u, y, ProcessKind.FirstOrder);
        Assert.InRange(model.Gain, -1.6, -1.4);
        Assert.InRange(model.TimeConstant, 10, 14);
        Assert.InRange(model.DeadTime, 1.5, 2.6);
        Assert.True(model.Fit > 0.98);

        var profile = PidTuner.Profiles.First(item => item.Key == "PRESSURE");
        var proposal = PidTuner.Tune(model, profile, 2);
        Assert.True(proposal.Gain < 0 && proposal.IAction && !proposal.DAction);   // reverse acting: negative GAIN
        Assert.InRange(proposal.Ti, 10, 14);                                       // TI = min(T, 4(τc+L)) = T
        var (_, response, _) = PidTuner.ClosedLoopStep(model, proposal.Gain, proposal.Ti, true, 120);
        Assert.InRange(response[^1], 0.97, 1.03);                                  // settles on the reference
        Assert.True(response.Max() < 1.2);                                         // little overshoot
    }

    [Fact]
    public void Integrating_Level_IdentifiedAndTunedPi_ServoGetsP()
    {
        // Level falls 0.004 %/s per % output after 3 s.
        double Ki = -0.004, L = 3;
        var (t, u, y) = Record((state, time, dt) => state + Ki * ((time - L < 10 ? 40 : 45) - 40) * dt, 600, 0.005);

        var model = PidTuner.Identify(t, u, y, ProcessKind.Integrating);
        Assert.InRange(model.Gain, -0.0045, -0.0035);
        Assert.InRange(model.DeadTime, 2, 4.5);

        var level = PidTuner.ProfileFor("PID_WATER_LEVEL");
        Assert.Equal("LEVEL", level.Key);
        var proposal = PidTuner.Tune(model, level, 2);
        Assert.True(proposal.Gain < 0 && proposal.IAction);
        Assert.True(proposal.Ti > 600);                                            // slow: minutes

        var servo = PidTuner.ProfileFor("PID_PELTON_NOZZLE_1");
        Assert.Equal("SERVO", servo.Key);
        Assert.False(PidTuner.Tune(model, servo, 2).IAction);                      // P controller
    }

    [Fact]
    public void Recording_WithoutOutputChange_IsRejected()
    {
        var t = Enumerable.Range(0, 100).Select(i => i * 0.1).ToList();
        var flat = t.Select(_ => 40.0).ToList();
        Assert.Throws<InvalidOperationException>(() => PidTuner.Identify(t, flat, flat, ProcessKind.FirstOrder));
    }

    [Fact]
    public void Parameters_ReadAndWrittenInTheFormOfTheProject()
    {
        Assert.Equal(600, PidTuner.Seconds("T#10M"));
        Assert.Equal(3723.5, PidTuner.Seconds("T#1H_2M_3S_500MS"));
        Assert.Equal("T#2M_30S", PidTuner.TimeText(150));
        Assert.Equal("PELTON_NOZZLE[1]", PidTuner.RegElementFor("PID_PELTON_NOZZLE_1"));
        Assert.Equal("WATER_LEVEL", PidTuner.RegElementFor("PID_WATER_LEVEL"));

        var catalog = new DbCatalog();
        catalog.ParameterRows.AddRange([
            new DbStringRow { Id = 620, PlcStructure = "PID_WATER_LEVEL", PlcName = "GAIN", RealValue = "-2.0", NameEnglish = "Water level PID proportional gain" },
            new DbStringRow { Id = 621, PlcStructure = "PID_WATER_LEVEL", PlcName = "TI", TimeValue = "T#10M" },
            new DbStringRow { Id = 622, PlcStructure = "PID_WATER_LEVEL", PlcName = "TD", TimeValue = "T#4S" },
            new DbStringRow { Id = 616, PlcStructure = "PID_WATER_LEVEL", PlcName = "I_SEL", IntegerValue = "1" },
            new DbStringRow { Id = 618, PlcStructure = "PID_WATER_LEVEL", PlcName = "D_SEL", IntegerValue = "0" }
        ]);
        var controller = Assert.Single(PidTuner.Controllers(catalog));
        Assert.Equal(("Water level", -2.0, 600.0, true), (controller.Description, controller.Gain, controller.Ti, controller.IAction));

        var changed = PidTuner.Apply(controller, new PidProposal(-3.25, 900, 0, true, true, false, 300));
        Assert.Equal("-3.25", catalog.ParameterRows[0].RealValue);
        Assert.Equal("T#15M", catalog.ParameterRows[1].TimeValue);
        Assert.Equal("T#0MS", catalog.ParameterRows[2].TimeValue);
        Assert.Contains("GAIN: -2.0 → -3.25", changed);
    }

    [Fact]
    public void Resample_BridgesACommunicationGap()
    {
        var t = new List<double> { 0, 0.1, 0.2, 0.3, 0.4, 0.5 };
        var u = new List<double> { 1, 2, double.NaN, double.NaN, 5, 6 };
        var (time, output, _) = PidTuner.Resample(t, u, u.Select(value => value).ToList());
        Assert.Equal(6, time.Count);
        Assert.Equal(3, output[2], 6);
        Assert.Equal(4, output[3], 6);
    }
}
