using System.IO;
using CwcConfigurator.Services.Eplan;
using CwcConfigurator.Services.Simulation.Process;
using Xunit;

namespace CwcConfigurator.Tests;

public sealed class EplanRelayImporterTests
{
    private static string Device(string tag, string symbol, string category, string points, double x, double y, string text = "", bool main = false)
    {
        var parts = symbol.Split('/');
        var letters = tag.TrimStart('-');
        return $"""
            <O17 A1="17/{tag}{x}{y}" A182="{(main ? 1 : 0)}" A185="{points}" A1261="{parts[0]}" A1262="{parts[1]}" A1263="{parts[2]}" A1381="{category}">
              <P11 P20011="en_US@{text};" />
              <P150 P1100="F" P1200="L" P20013="{letters[..1]}" P20014="{letters[1..]}" />
              <S40x1201 A762="{x}/{y}" />
            </O17>
            """;
    }

    private static string Junction(string number, string variant, double x, double y) =>
        $"""<O42 A1261="0" A1262="{number}" A1263="{variant}"><S40x1201 A762="{x}/{y}" /></O42>""";

    private static string Potential(string letters, double x, double y) =>
        $"""<O70><P150 P20013="{letters}" P20014="230VAC" /><S40x1201 A762="{x}/{y}" /></O70>""";

    [Fact]
    public void Import_RebuildsWiresByAutoconnect_AndBranchesSharedStarts()
    {
        // L rail (y 100) with a T down at x 50: -K1 NO, then a T at y 70 branching right to x 90.
        //   x 50: -K2 NO → coil -K10;   x 90: -K3 NC (its NC pin at x 94, back to x 90 by two corners) → coil -K11.
        //   Both coils return to the N rail (y 0) through T's up.
        var page = string.Join("\n",
            Potential("L", 10, 100), Junction("64", "0", 50, 100), Potential("L", 130, 100),
            Device("-K1", "1/1125/2", "203", "12;11;14", 50, 90, "ESD"),
            Junction("66", "0", 50, 70), Junction("70", "3", 90, 70),
            Device("-K2", "1/1125/2", "203", "12;11;14", 50, 60, "OPEN"),
            Device("-K3", "1/1125/2", "203", "22;21;24", 90, 60, "CLOSE"),
            Junction("70", "2", 94, 44), Junction("70", "0", 90, 44),
            Device("-K10", "5/27/0", "200", "A1;A2", 50, 30, main: true),
            Device("-K11", "5/27/0", "200", "A1;A2", 90, 30, main: true),
            Potential("N", 10, 0), Junction("72", "0", 50, 0), Junction("72", "0", 90, 0), Potential("N", 130, 0));
        var xml = $"""
            <EplanPxfRoot Name="Test">
              <O4><P49 P1100="F" P1200="L" P11012="5" /><P11 P11011="en_US@GATE CONTROL;" />{page}</O4>
            </EplanPxfRoot>
            """;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".epj");
        File.WriteAllText(path, xml);
        try
        {
            var pages = EplanRelayImporter.ListPages(path);
            var import = EplanRelayImporter.Import(path, pages.Select(item => item.Index).ToHashSet());

            var sheet = Assert.Single(import.Sheets);
            Assert.Equal(5, sheet.Page);
            Assert.Equal("GATE CONTROL", sheet.Name);
            var first = sheet.Circuits.Single(circuit => circuit.Coil.Tag == "-K10");
            var second = sheet.Circuits.Single(circuit => circuit.Coil.Tag == "-K11");
            Assert.Equal(new[] { "-K1", "-K2" }, first.Elements.Select(element => element.Tag));
            Assert.All(first.Elements, element => Assert.Equal("NO", element.Contact));
            Assert.Equal("11/12/14", first.Elements[0].Pins);
            Assert.Equal(first.Elements[0].Id, second.Feed); // branches off below -K1
            var close = Assert.Single(second.Elements);
            Assert.Equal(("-K3", "NC", "21/22/24", "CLOSE"), (close.Tag, close.Contact, close.Pins, close.Text));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
