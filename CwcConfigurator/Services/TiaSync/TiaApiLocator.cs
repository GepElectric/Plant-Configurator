using System.IO;

namespace CwcConfigurator.Services.TiaSync;

public static class TiaApiLocator
{
    public static string GetPreferredVersionLabel()
    {
        if (IsV18Installed())
        {
            return "V18";
        }

        return IsV21Installed() ? "V21" : "";
    }

    /// <summary>
    /// All TIA Openness versions whose PublicAPI is installed. The app launches a
    /// matching helper build per version; process listing queries each of these.
    /// </summary>
    public static IReadOnlyList<string> GetInstalledVersions()
    {
        var versions = new List<string>();
        if (IsV18Installed())
        {
            versions.Add("V18");
        }

        if (IsV21Installed())
        {
            versions.Add("V21");
        }

        return versions;
    }

    private static bool IsV18Installed()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            @"Siemens\Automation\Portal V18\PublicAPI\V18");
        return File.Exists(Path.Combine(dir, "Siemens.Engineering.dll"));
    }

    private static bool IsV21Installed()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            @"Siemens\Automation\Portal V21\PublicAPI\V21\net48");
        return File.Exists(Path.Combine(dir, "Siemens.Engineering.Base.dll"));
    }
}
