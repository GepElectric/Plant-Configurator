using System.IO;
using CwcConfigurator.Services.TiaSync;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class ControlBlockLayoutTests
{
    /// <summary>Sources exported from the Intake PLC of HPP Vihtakoski (TIA V21, REFERENCES DB9 and REG_INTF DB31).</summary>
    private static ControlBlockLayout.Source Intake() => Read("ControlBlocks");

    private static ControlBlockLayout.Source Read(string folder)
    {
        var source = new ControlBlockLayout.Source();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "TestData", folder)))
        {
            source.Read(File.ReadAllText(file));
        }

        return source;
    }

    [Fact]
    public void UnitPlc_MatchesTheRegulatorInterfaceLayoutVerifiedOnThePlc()
    {
        // Unit PLC of the same project: arrays of DT_REGULATOR_INTERFACE (PELTON_NOZZLE[1..6]); addresses verified on PLCSIM.
        var source = Read("ControlBlocksUnit");
        var layout = ControlBlockLayout.Layout(source, ControlBlockLayout.RegulatorInterface);
        int At(string element, string member) => layout.Single(v => v.Element == element && v.Path == member).ByteOffset;
        Assert.Equal(430, At("PELTON_NOZZLE[1]", "VALUES.SCALED_REFERENCE"));
        Assert.Equal(204, At("FRANCIS_DISTRIBUTOR", "VALUES.SCALED_MEAS"));
        Assert.Equal(572 + 18, At("EXC_VOLTAGE", "VALUES.OUT"));

        // Signals: this Pelton unit has nozzle 1 configured (in REFERENCES), Francis not; unnamed elements are left out.
        var signals = ControlBlockLayout.Signals(source).Where(s => s.Block == "REG_INTF").ToList();
        Assert.Contains(signals, s => s.Element == "PELTON_NOZZLE[1]" && s.Member == "VALUES.OUT" && s.ByteOffset == 434);
        Assert.DoesNotContain(signals, s => s.Element.StartsWith("Element_") || s.Element == "FRANCIS_DISTRIBUTOR");
    }

    [Fact]
    public void Layout_MatchesTheStandardMemoryLayoutOfTia()
    {
        var source = Intake();
        Assert.Empty(source.MissingTypes());

        // DT_REFERENCE_SOLO: ID 0, DATA 2 (3 bools, FINAL 4 / ACTUAL 8 / MEAS 12, CMD_INCREMENT 20), NAME 22, S_UNIT 56 → 62 B per element.
        var references = ControlBlockLayout.Layout(source, ControlBlockLayout.References);
        Assert.Equal(4, references.Single(v => v.Element == "WATER_LEVEL" && v.Path == "DATA.FINAL").ByteOffset);
        Assert.Equal(62 + 4, references.Single(v => v.Element == "GATE_1" && v.Path == "DATA.FINAL").ByteOffset);
        Assert.Equal(62 + 8, references.Single(v => v.Element == "GATE_1" && v.Path == "DATA.ACTUAL").ByteOffset);
        Assert.Equal((2, 1), references.Where(v => v.Element == "WATER_LEVEL" && v.Path == "DATA.CONFIGURED").Select(v => (v.ByteOffset, v.Bit)).Single());

        // DT_REGULATOR_INTERFACE: 26 B, VALUES at +14 (as in the fixed REG_INTF layout of the unit PLC).
        var regulators = ControlBlockLayout.Layout(source, ControlBlockLayout.RegulatorInterface);
        Assert.Equal(14, regulators.Single(v => v.Element == "WATER_LEVEL" && v.Path == "VALUES.SCALED_REFERENCE").ByteOffset);
        Assert.Equal(26 * 2 + 18, regulators.Single(v => v.Element == "GATE_2" && v.Path == "VALUES.OUT").ByteOffset);
    }

    [Fact]
    public void Signals_OnlyConfiguredElementsWithTheirNamesAndUnits()
    {
        var signals = ControlBlockLayout.Signals(Intake());

        Assert.DoesNotContain(signals, s => s.Element.StartsWith("SPARE") || s.Element == "GATE_3");   // CONFIGURED := FALSE
        var final = signals.Single(s => s.Block == "REFERENCES" && s.Element == "GATE_1" && s.Member == "DATA.FINAL");
        Assert.Equal((9, 66, "Gate 1 regulation", "%"), (final.Db, final.ByteOffset, final.Description, final.Unit));
        var output = signals.Single(s => s.Block == "REG_INTF" && s.Element == "GATE_1" && s.Member == "VALUES.OUT");
        Assert.Equal((31, 44, "Gate 1 regulation"), (output.Db, output.ByteOffset, output.Description));
        Assert.Contains(signals, s => s.Block == "REFERENCES" && s.Element == "WATER_LEVEL" && s.Unit == "m");
    }
}
