using System.IO;
using System.Linq;
using CwcConfigurator.Services.Eplan;
using Xunit;

namespace CwcConfigurator.Tests;

public class EplanProjectReaderTests
{
    // Minimal PXF export: title page, PLC CONFIGURATION (CPU + DI card + RTD card), one DI schematic page, one PLC overview page.
    private const string SampleEpj = """
        <?xml version="1.0" encoding="utf-8" ?>
        <EplanPxfRoot Name="Sample" Type="Project">
         <O14>
          <O4 A1="4/1"><P11 P11011="??_??@TITLE PAGE;"/><P49 P1100="1CJA01" P11012="1"/></O4>
          <O4 A1="4/2"><P11 P11011="??_??@PLC CONFIGURATION;"/><P49 P1100="1CJA01" P11012="12"/>
           <O26 A1="26/1">
            <O17 A1="17/1"><S40x1201 A762="62/232"/><P11 P20901_21="??_??@6ES7512-1DK01-0AB0;" P20901_22="??_??@CPU 1512SP-1 PN;"/><P150 P1100="1CJA01" P20013="A" P20014="101"/></O17>
           </O26>
           <O26 A1="26/2">
            <O17 A1="17/2"><S40x1201 A762="140/232"/><P11 P20901_21="??_??@6ES7131-6BH01-0BA0;" P20901_22="??_??@DI 16x24VDC ST;"/><P150 P1100="1CJA01" P20013="A" P20014="111"/></O17>
           </O26>
           <O26 A1="26/3">
            <O17 A1="17/3"><S40x1201 A762="164/232"/><P11 P20901_21="??_??@6ES7134-6JD00-0CA1;" P20901_22="??_??@AI 4xRTD/TC 2-/3-/4-wire HF;"/><P150 P1100="1CJA01" P20013="A" P20014="154"/></O17>
           </O26>
          </O4>
          <O4 A1="4/3"><P11 P11011="??_??@DIGITAL INPUTS 0.0 - 0.7;"/><P49 P1100="1CJA01" P11012="14"/>
           <O83 A1="83/1" A501="44/272" A511="??_??@REMOTE&#10;CONTROL;"/>
           <O83 A1="83/2" A501="96/272" A511="??_??@STEP-UP TRANSFORMER&#10;BUCHOLZ ALARM;"/>
           <O17 A1="17/10" A185="1" A186="DI0"><S40x1201 A762="44/64"/><P11 P20400="I0.0" P20407="IN0"/><P150 P1100="1CJA01" P20013="A" P20014="111"/></O17>
           <O17 A1="17/11" A185="2" A186="DI1"><S40x1201 A762="92/64"/><P11 P20400="I0.1" P20407="IN1"/><P150 P1100="1CJA01" P20013="A" P20014="111"/></O17>
           <O17 A1="17/12" A185="12;11;14"><S40x1201 A762="92/84"/><P150 P1100="1CJA01" P20012="1" P20013="K" P20014="12"/></O17>
           <O17 A1="17/13" A185="12;11;14"><S40x1201 A762="92/120"/><P150 P1100="1CJA01" P20012="1" P20013="K" P20014="99"/></O17>
           <O17 A1="17/14" A185="A;B;;"><S40x1201 A762="44/92"/><P11 P20030="17"/><P150 P1100="1CJA01" P20013="X" P20014="100"/></O17>
           <O17 A1="17/15" A185="L+" A186="24VDC"><S40x1201 A762="20/64"/><P11 P20407=""/><P150 P1100="1CJA01" P20013="A" P20014="111"/></O17>
          </O4>
          <O4 A1="4/4"><P11 P11011="??_??@PLC OVERVIEW (CENTRAL STATION DI);"/><P49 P1100="1CJA01" P11012="75"/>
           <O17 A1="17/20" A185="1" A186="DI0"><S40x1201 A762="116/188"/><P11 P20400="I0.0" P20407="0"/><P150 P1100="1CJA01" P20013="A" P20014="111"/></O17>
           <O17 A1="17/21" A185="2" A186="DI1"><S40x1201 A762="116/180"/><P11 P20400="I0.1" P20407="1"/><P150 P1100="1CJA01" P20013="A" P20014="111"/></O17>
          </O4>
         </O14>
        </EplanPxfRoot>
        """;

    private static EplanProject ReadSample()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eplan_sample_{Guid.NewGuid():N}.epj");
        File.WriteAllText(path, SampleEpj.TrimStart());
        try
        {
            return EplanProjectReader.Read(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_FindsCpuStationWithModulesInRackOrder()
    {
        var project = ReadSample();

        Assert.Equal("Sample", project.ProjectName);
        Assert.Equal(4, project.Pages.Count);
        var station = Assert.Single(project.Stations);
        Assert.True(station.IsCpu);
        Assert.Equal("A101", station.HeadDeviceTag);
        Assert.Equal(new[] { "A111", "A154" }, station.Modules.Select(m => m.DeviceTag));
        Assert.Equal(EplanModuleKind.DigitalInput, station.Modules[0].Kind);
        Assert.Equal(EplanModuleKind.RtdInput, station.Modules[1].Kind);
    }

    [Fact]
    public void Read_ChannelCarriesSignalConnectionAndPages()
    {
        var module = ReadSample().Stations[0].Modules[0];

        Assert.Equal(new int?[] { 0, 1 }, module.Channels.Select(c => c.Port));     // L+ supply is not a channel
        var di1 = module.Channels[1];
        Assert.Equal("2", di1.Terminal);
        Assert.Equal("DI1", di1.ConnectionDescription);
        Assert.Equal("I0.1", di1.Address);
        Assert.Equal("STEP-UP TRANSFORMER BUCHOLZ ALARM", di1.SignalName);
        Assert.Equal("-1K12:14", di1.ConnectedTo);                  // nearest device above, NO contact pin
        Assert.Equal("=1CJA01/14.2", di1.Schematic!.Display);
        Assert.Equal(3, di1.Schematic.GlobalPage);
        Assert.Equal("=1CJA01/75.3", di1.Overview!.Display);
        Assert.Equal(4, di1.Overview.GlobalPage);

        Assert.Equal("-X100:17", module.Channels[0].ConnectedTo);  // terminal -> terminal number
        Assert.Equal("REMOTE CONTROL", module.Channels[0].SignalName);
    }

    [Theory]
    [InlineData("DI 16x24VDC ST", EplanModuleKind.DigitalInput)]
    [InlineData("DQ 16x24 VDC/0,5A ST", EplanModuleKind.DigitalOutput)]
    [InlineData("AI 8xI 2-/4-wire BA", EplanModuleKind.AnalogInput)]
    [InlineData("AI 4xRTD/TC 2-/3-/4-wire HF", EplanModuleKind.RtdInput)]
    [InlineData("AQ 4xU/I ST", EplanModuleKind.AnalogOutput)]
    [InlineData("TM COUNT 1x24V", EplanModuleKind.Counter)]
    public void KindOf_MapsCardTypes(string type, EplanModuleKind expected)
    {
        Assert.Equal(expected, EplanProjectReader.KindOf(type));
    }

    [Theory]
    [InlineData("CPU 1512SP-1 PN")]
    [InlineData("IM 155-6PN BA")]
    public void KindOf_HeadModulesAreNotIo(string type)
    {
        Assert.Null(EplanProjectReader.KindOf(type));
    }

    [Theory]
    [InlineData("??_??@TITLE PAGE;", "TITLE PAGE")]
    [InlineData("en_US@Device tag List;hr_HR@Aparatna lista;", "Device tag List")]
    [InlineData("en_US@VOLTAGE\nLOWER;hr_HR@NAPON\nNIŽE;", "VOLTAGE LOWER")]
    [InlineData("??_??@HPU PUMP 1\nCB CLOSED;", "HPU PUMP 1 CB CLOSED")]
    [InlineData("plain", "plain")]
    public void Text_PicksSchematicLanguage(string raw, string expected)
    {
        Assert.Equal(expected, EplanProjectReader.Text(raw));
    }

    /// <summary>Real project from the field (not in the repo); runs only where the file is present.</summary>
    [Fact]
    public void Read_RealProject_MatchesSchematicExample()
    {
        const string path = @"C:\Users\Mazda\Desktop\Sheme\HPP Stori2.epj";
        if (!File.Exists(path))
        {
            return;
        }

        var project = EplanProjectReader.Read(path);

        Assert.Equal(new[] { "0BFA01", "0CJA01", "1CJA01", "2CJA01" }, project.Stations.Select(s => s.Function));
        Assert.True(project.Stations[0].IsDistributed);
        var unit2 = project.Stations.Single(s => s.Function == "2CJA01");
        var di1 = unit2.Modules.Single(m => m.DeviceTag == "A115").Channels.Single(c => c.Port == 1);
        Assert.Equal("DI1", di1.ConnectionDescription);
        Assert.Equal("I8.1", di1.Address);
        Assert.Equal("-1K12:14", di1.ConnectedTo);
        Assert.Equal("STEP-UP TRANSFORMER BUCHOLZ ALARM (2BAT10)", di1.SignalName);
        Assert.Equal("=2CJA01/22.2", di1.Schematic!.Display);
        Assert.Equal("=2CJA01/79.3", di1.Overview!.Display);
    }
}
