using System.IO;
using System.Linq;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;
using Xunit;

namespace CwcConfigurator.Tests;

public class TextListYamlWriterTests
{
    private const string ListsYaml =
        "#Version: 2.0\n" +
        "TextListContainers:\n" +
        "  C1:\n" +
        "    ResourceLists:\n" +
        "      Common_ALARM_DESCRIPTIONS:\n" +
        "        Entries:\n" +
        "          E1:\n" +
        "            Value: 1\n" +
        "            Text: Lib.Text_1\n" +
        "          E2:\n" +
        "            Value: 2\n" +
        "            Text: Lib.Text_2\n" +
        "      Other_LIST:\n" +
        "        Entries:\n" +
        "          E3:\n" +
        "            Value: 1\n" +
        "            Text: Lib.Text_3\n";

    private const string LibraryYaml =
        "#Version: 2.0\n" +
        "TextLibraries:\n" +
        "  Lib:\n" +
        "    Languages:\n" +
        "    - de-DE\n" +
        "    - en-US\n" +
        "    Entries:\n" +
        "      Text_1:\n" +
        "        Text:\n" +
        "        - alt1 de\n" +
        "        - alt1 en\n" +
        "      Text_2:\n" +
        "        Text:\n" +
        "        - alt2 de\n" +
        "        - alt2 en\n" +
        "      Text_3:\n" +
        "        Text:\n" +
        "        - other de\n" +
        "        - other en\n";

    [Fact]
    public void UpdateLibrary_TouchesOnlyManagedListAndLanguage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cwc_writer_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var listsPath = Path.Combine(dir, "TextLists.hmi.yml");
        var libPath = Path.Combine(dir, "TextLists.TextLibrary.hmi.yml");
        File.WriteAllText(listsPath, ListsYaml);
        File.WriteAllText(libPath, LibraryYaml);

        var app = new DescriptionTextListData();
        app.SetText("Common_ALARM_DESCRIPTIONS", 1, "en-US", "NEW EN 1"); // only this should change
        app.SetText("Other_LIST", 1, "en-US", "SHOULD NOT APPLY");        // not in managed set

        var outLib = Path.Combine(dir, "out.TextLibrary.hmi.yml");
        var result = new TextListYamlWriter().UpdateLibrary(
            listsPath, libPath, app, new[] { "Common_ALARM_DESCRIPTIONS" }, outLib);

        Assert.Equal(1, result.UpdatedEntries);
        Assert.Contains("Common_ALARM_DESCRIPTIONS", result.UpdatedLists);
        Assert.StartsWith("#Version: 2.0", File.ReadAllText(outLib));

        var roundTrip = new TextListYamlImporter().Import(listsPath, outLib);
        Assert.Equal("NEW EN 1", roundTrip.GetText("Common_ALARM_DESCRIPTIONS", 1, "en-US")); // changed
        Assert.Equal("alt1 de", roundTrip.GetText("Common_ALARM_DESCRIPTIONS", 1, "de-DE"));  // app had no de -> kept
        Assert.Equal("alt2 en", roundTrip.GetText("Common_ALARM_DESCRIPTIONS", 2, "en-US"));  // app had no id 2 -> kept
        Assert.Equal("other en", roundTrip.GetText("Other_LIST", 1, "en-US"));                 // not managed -> kept

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ListNamesPresent_ReturnsAllLists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cwc_writer_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var listsPath = Path.Combine(dir, "TextLists.hmi.yml");
        File.WriteAllText(listsPath, ListsYaml);

        var names = TextListYamlWriter.ListNamesPresent(listsPath);

        Assert.Contains("Common_ALARM_DESCRIPTIONS", names);
        Assert.Contains("Other_LIST", names);
        Directory.Delete(dir, recursive: true);
    }
}
