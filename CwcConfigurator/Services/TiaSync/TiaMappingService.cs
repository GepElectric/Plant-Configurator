using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.TiaSync;

public sealed class TiaMappingService
{
    public void StoreProjectBinding(CwcProject project, TiaAttachedSession session)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(session);

        project.TiaSync ??= new TiaSyncConfig();
        project.TiaSync.SyncWithDatabases(project.PlcDatabases, project.DbCatalog);
        project.TiaSync.ProjectBinding.PortalVersion = session.PortalVersion;
        project.TiaSync.ProjectBinding.ProjectName = session.ProjectName;
        project.TiaSync.ProjectBinding.ProjectPath = session.ProjectPath;
        project.TiaSync.ProjectBinding.ProcessId = session.ProcessId;
        project.TiaSync.ProjectBinding.PortalPath = session.PortalPath;
        project.TiaSync.ProjectBinding.LastAttachedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    public void AutoMap(CwcProject project, TiaAttachedSession session)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(session);

        StoreProjectBinding(project, session);

        foreach (var database in project.PlcDatabases.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var binding = project.TiaSync.GetOrCreate(database.Id, database.Name);
            var discovered = FindBestMatch(database.Name, session.Plcs);
            if (discovered is null)
            {
                binding.SyncState = "Mapping incomplete";
                continue;
            }

            binding.DeviceName = discovered.DeviceName;
            binding.SoftwareName = discovered.SoftwareName;
            binding.SyncState = "Mapped";
        }
    }

    /// <summary>
    /// Fills in bindings whose device/software pair does not exist in the
    /// attached session, matching by PLC database name. Bindings that already
    /// point at a real TIA PLC are left untouched. Returns how many bindings
    /// were (re)mapped.
    /// </summary>
    public int AutoMapUnresolved(CwcProject project, TiaAttachedSession session)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(session);

        project.TiaSync ??= new TiaSyncConfig();
        project.TiaSync.SyncWithDatabases(project.PlcDatabases, project.DbCatalog);

        var mapped = 0;
        foreach (var database in project.PlcDatabases.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var binding = project.TiaSync.GetOrCreate(database.Id, database.Name);
            if (PairExists(session, binding.DeviceName, binding.SoftwareName))
            {
                continue;
            }

            var discovered = FindBestMatch(database.Name, session.Plcs);
            if (discovered is null)
            {
                continue;
            }

            binding.DeviceName = discovered.DeviceName;
            binding.SoftwareName = discovered.SoftwareName;
            binding.SyncState = "Mapped";
            mapped++;
        }

        return mapped;
    }

    public static bool PairExists(TiaAttachedSession? session, string deviceName, string softwareName)
    {
        return session is not null
               && session.Plcs.Any(plc =>
                   plc.DeviceName.Equals(deviceName ?? "", StringComparison.OrdinalIgnoreCase)
                   && plc.SoftwareName.Equals(softwareName ?? "", StringComparison.OrdinalIgnoreCase));
    }

    public IList<string> Validate(CwcProject project, TiaAttachedSession? session)
    {
        var issues = new List<string>();
        if (session is null)
        {
            issues.Add("TIA Portal is not attached.");
            return issues;
        }

        project.TiaSync ??= new TiaSyncConfig();
        project.TiaSync.SyncWithDatabases(project.PlcDatabases, project.DbCatalog);

        foreach (var database in project.PlcDatabases)
        {
            var binding = project.TiaSync.GetOrCreate(database.Id, database.Name);
            if (string.IsNullOrWhiteSpace(binding.DeviceName))
            {
                issues.Add($"PLC database '{database.Name}' is missing a TIA device mapping.");
            }

            if (string.IsNullOrWhiteSpace(binding.SoftwareName))
            {
                issues.Add($"PLC database '{database.Name}' is missing a TIA software mapping.");
            }
            else if (!session.Plcs.Any(plc =>
                         plc.DeviceName.Equals(binding.DeviceName, StringComparison.OrdinalIgnoreCase)
                         && plc.SoftwareName.Equals(binding.SoftwareName, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add($"PLC database '{database.Name}' maps to '{binding.DeviceName} / {binding.SoftwareName}', which was not found in the attached TIA project.");
            }
        }

        foreach (var duplicate in project.TiaSync.PlcBindings
                     .Where(binding => !string.IsNullOrWhiteSpace(binding.SoftwareName))
                     .GroupBy(binding => binding.SoftwareName.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var databases = duplicate
                .Select(binding => project.PlcDatabases.FirstOrDefault(database => database.Id == binding.PlcDatabaseId)?.Name ?? binding.PlcName)
                .Where(name => !string.IsNullOrWhiteSpace(name));
            issues.Add($"TIA software '{duplicate.Key}' is mapped to more than one PLC database: {string.Join(", ", databases)}.");
        }

        foreach (var duplicate in project.TiaSync.PlcBindings
                     .Where(binding => !string.IsNullOrWhiteSpace(binding.DeviceName))
                     .GroupBy(binding => binding.DeviceName.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var databases = duplicate
                .Select(binding => project.PlcDatabases.FirstOrDefault(database => database.Id == binding.PlcDatabaseId)?.Name ?? binding.PlcName)
                .Where(name => !string.IsNullOrWhiteSpace(name));
            issues.Add($"TIA device '{duplicate.Key}' is mapped to more than one PLC database: {string.Join(", ", databases)}.");
        }

        return issues;
    }

    private static TiaDiscoveredPlc? FindBestMatch(string plcName, IEnumerable<TiaDiscoveredPlc> plcs)
    {
        var list = plcs.ToList();
        var exact = list.FirstOrDefault(plc =>
            plc.DeviceName.Equals(plcName, StringComparison.OrdinalIgnoreCase)
            || plc.SoftwareName.Equals(plcName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        return list
            .Where(plc =>
                plc.DeviceName.Contains(plcName, StringComparison.OrdinalIgnoreCase)
                || plc.SoftwareName.Contains(plcName, StringComparison.OrdinalIgnoreCase)
                || plcName.Contains(plc.DeviceName, StringComparison.OrdinalIgnoreCase)
                || plcName.Contains(plc.SoftwareName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(plc => plc.DeviceName.Length)
            .ThenBy(plc => plc.SoftwareName.Length)
            .FirstOrDefault();
    }
}
