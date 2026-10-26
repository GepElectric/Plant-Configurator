using System.Globalization;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

/// <summary>Module names from the module table (Module catalog) per signal type; the number when a module has no name.</summary>
public static class ModuleNames
{
    public static string Resolve(IEnumerable<ModuleNameRow>? modules, int moduleNumber)
    {
        var name = modules?.FirstOrDefault(row => row.ModuleNumber == moduleNumber)?.ModuleName?.Trim();
        return string.IsNullOrEmpty(name) ? moduleNumber.ToString(CultureInfo.InvariantCulture) : name;
    }
}
