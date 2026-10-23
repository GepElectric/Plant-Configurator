using System;
using System.IO;
using System.Linq;
using CwcConfigurator.Services;
using Xunit;

namespace CwcConfigurator.Tests;

public class ScadaScriptRoundTripTests
{
    // Start of AUX_FUNCTIONS._SET_INTERNAL_TAG as WinCC Unified exports it: the general variables have no section header.
    private const string Module =
        "export let NUM_OF_FUNCTIONS_CALLED = 16;\r\n" +
        "export function _SET_INTERNAL_TAG(parameter1, parameter2) {\r\n" +
        "let RUNTIME_NAME = \"SCADA_PC\";  //  Runtime name\r\n" +
        "let LOCATION_NUM = 6;  //  Number of locations\r\n" +
        "let LOCATION_0_ACTIVE = 1;  //  Active\r\n" +
        "let LOCATION_0_ENTRY = \"Common\";  //  Location entry\r\n" +
        "///////////////////////////////////////// TABS /////////////////////////////////////////\r\n" +
        "let HEADER_TABS_ENTRY_1 = \"Overview\";  //  Tab\r\n" +
        "///////////////////////////////////////// TAG WRITES /////////////////////////////////////////\r\n" +
        "Tags.SysFct.SetTagValue(\"RUNTIME_NAME\",RUNTIME_NAME);\r\n" +
        "Tags.SysFct.SetTagValue(\"LOCATION_NUM\",LOCATION_NUM);\r\n" +
        "Tags.SysFct.SetTagValue(\"LOCATION_0.LOCATION_ACTIVE\",LOCATION_0_ACTIVE);\r\n" +
        "Tags.SysFct.SetTagValue(\"LOCATION_0.ENTRY\",LOCATION_0_ENTRY);\r\n" +
        "Tags.SysFct.SetTagValue(\"HEADER_TABS.ENTRY_1\",HEADER_TABS_ENTRY_1);\r\n" +
        "}\r\n" +
        "export function _OTHER() {\r\n}\r\n";

    [Fact]
    public void Pull_ThenPush_KeepsTheGeneralVariablesAndTexts()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"scada_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var module = ScadaInternalTagModule.Split(Module);
            var body = Path.Combine(folder, "body.js");
            File.WriteAllText(body, module.Body);
            var service = new ScadaConfigFileService();
            var database = service.Import(body);

            var output = Path.Combine(folder, "out.js");
            service.Export(database, output);
            var text = File.ReadAllText(output);
            var pushed = module.Rewrap(text);

            Assert.Contains("let RUNTIME_NAME = \"SCADA_PC\";", text);
            Assert.Contains("let LOCATION_NUM = 6;", text);
            Assert.Contains("let LOCATION_0_ENTRY = \"Common\";", text);   // a location name stays a text
            Assert.Contains("Tags.SysFct.SetTagValue(\"LOCATION_0.ENTRY\",LOCATION_0_ENTRY);", text);
            Assert.Contains("let HEADER_TABS_ENTRY_1 = \"Overview\";", text);
            Assert.Single(database.Variables, variable => variable.Name == "LOCATION_0_ENTRY");
            Assert.StartsWith("export let NUM_OF_FUNCTIONS_CALLED = 16;", pushed);   // the rest of the module stays
            Assert.EndsWith("export function _OTHER() {\r\n}\r\n", pushed);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
