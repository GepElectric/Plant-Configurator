using System.IO;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace CwcConfigurator.Services;

/// <summary>
/// Application updates from GitHub Releases (Velopack). A release is mandatory when its notes
/// contain <see cref="MandatoryMarker"/>; Release.ps1 -Mandatory writes it.
/// </summary>
public sealed class AppUpdateService
{
    public const string RepositoryUrl = "https://github.com/GepElectric/Plant-Configurator";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    public const string MandatoryMarker = "[mandatory]";
    /// <summary>Testing only: a local Releases folder used instead of GitHub.</summary>
    public const string SourceOverrideVariable = "PLANTCONFIGURATOR_UPDATE_SOURCE";

    private readonly UpdateManager _manager = new(CreateSource());

    /// <summary>False when the app runs from a build folder or a copied folder instead of a Setup.exe install.</summary>
    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? AssemblyVersion;

    public static string AssemblyVersion
    {
        get
        {
            var version = typeof(AppUpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? typeof(AppUpdateService).Assembly.GetName().Version?.ToString()
                          ?? "0.0.0";
            var plus = version.IndexOf('+');
            return plus >= 0 ? version[..plus] : version;
        }
    }

    public async Task<AppUpdate?> CheckAsync()
    {
        if (!IsInstalled)
        {
            return null;
        }

        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        return info is null ? null : new AppUpdate(info);
    }

    public Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken cancellationToken = default)
    {
        return _manager.DownloadUpdatesAsync(update.Info, progress, cancellationToken);
    }

    /// <summary>
    /// Starts the updater, which waits until this process exits, installs the update and starts the
    /// new version. The caller closes the app normally afterwards (so unsaved work is still offered for saving).
    /// </summary>
    public void ApplyWhenClosed(AppUpdate update)
    {
        _manager.WaitExitThenApplyUpdates(update.Info.TargetFullRelease, silent: false, restart: true);
    }

    private static IUpdateSource CreateSource()
    {
        var folder = Environment.GetEnvironmentVariable(SourceOverrideVariable);
        return string.IsNullOrWhiteSpace(folder)
            ? new GithubSource(RepositoryUrl, null, false)
            : new SimpleFileSource(new DirectoryInfo(folder));
    }

    public static bool IsMandatory(string? notes)
    {
        return (notes ?? "").Contains(MandatoryMarker, StringComparison.OrdinalIgnoreCase);
    }

    public static string CleanNotes(string? notes)
    {
        var lines = (notes ?? "")
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => !line.Trim().Equals(MandatoryMarker, StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Replace(MandatoryMarker, "", StringComparison.OrdinalIgnoreCase));
        return string.Join(Environment.NewLine, lines).Trim();
    }
}

public sealed class AppUpdate
{
    public AppUpdate(UpdateInfo info)
    {
        Info = info;
    }

    public UpdateInfo Info { get; }

    public string Version => Info.TargetFullRelease.Version.ToString();

    public bool IsMandatory => AppUpdateService.IsMandatory(Info.TargetFullRelease.NotesMarkdown);

    public string Notes => AppUpdateService.CleanNotes(Info.TargetFullRelease.NotesMarkdown);
}
