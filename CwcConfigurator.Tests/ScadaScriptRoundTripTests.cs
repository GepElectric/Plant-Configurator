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

    [Fact]
    public void Navigation_FollowsTheScreenCountOfEachButton()
    {
        var database = new CwcConfigurator.Domain.ScadaDatabase();
        void Add(string name, string value, string type = "STRING") =>
            database.Variables.Add(new CwcConfigurator.Domain.ScadaConfigVariable { Id = database.Variables.Count + 1, Name = name, Value = value, Type = type, Section = "MAIN" });
        Add("MAIN_NAV_BUTTONS_NUM", "1", "INT");
        Add("MAIN_NAV_BUTTONS_ENTRY_1", "General");
        Add("BUTTON_1_SCREEN_NUM", "3", "INT");
        Add("SCREENS_Button_1_ENTRY_1", "General_Overview");
        Add("SCREENS_Button_1_ENTRY_2", "General_SLD");
        Add("SCREENS_Button_1_ENTRY_5", "General_Old");          // beyond the count: not a card
        Add("MAIN_NAV_BUTTONS_ENTRY_2", "Unit 1");

        var model = ScadaNavigationModel.FromDatabase(database);
        Assert.Equal(1, model.ButtonCount);
        Assert.Equal(3, model.Buttons[0].Cards.Count);              // 3 screens set on MAINNAV, the third still empty
        Assert.Equal("", model.Buttons[0].Cards[2].Screen);
        Assert.False(model.Buttons[1].Visible);

        model.Buttons[0].Cards.RemoveAt(2);
        model.ButtonCount = 2;
        model.ApplyTo(database);
        Assert.Equal("2", database.Variables.First(variable => variable.Name == "BUTTON_1_SCREEN_NUM").Value);
        Assert.Equal("2", database.Variables.First(variable => variable.Name == "MAIN_NAV_BUTTONS_NUM").Value);
    }
}
