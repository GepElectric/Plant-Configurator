using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace CwcConfigurator.Services;

public sealed record UnifiedJsConnectorImportResult(
    string InstallFolder,
    string ServerExecutablePath,
    string PackageVersion,
    string SourcePackagePath);

public sealed class UnifiedJsConnectorImporter
{
    private const string ServerExecutableName = "Siemens.Simatic.Hmi.Openness.RestService.exe";

    public UnifiedJsConnectorImportResult Import(string sourceFolder)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException("WinCC Unified JS Connector folder was not found.");
        }

        var extractedPackageJson = Directory
            .EnumerateFiles(sourceFolder, "package.json", SearchOption.AllDirectories)
            .FirstOrDefault(IsConnectorPackageJson);
        if (extractedPackageJson is not null)
        {
            var installFolder = Path.GetDirectoryName(extractedPackageJson)
                ?? throw new InvalidOperationException("Connector package folder could not be resolved.");
            var serverPath = FindServerExecutable(installFolder);
            return new UnifiedJsConnectorImportResult(
                installFolder,
                serverPath,
                ReadPackageVersion(extractedPackageJson),
                extractedPackageJson);
        }

        var sourcePackage = Directory
            .EnumerateFiles(sourceFolder, "WinCC_Unified_JS_Connector*.zip", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? Directory
                .EnumerateFiles(sourceFolder, "*.vsix", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            ?? throw new FileNotFoundException("WinCC Unified JS Connector ZIP/VSIX package was not found in the selected folder.");

        var installRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlantConfigurator",
            "WinCCUnifiedJsConnector");
        ResetInstallFolder(installRoot);

        if (Path.GetExtension(sourcePackage).Equals(".vsix", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(sourcePackage, installRoot);
        }
        else
        {
            using var temp = new TemporaryDirectory();
            ZipFile.ExtractToDirectory(sourcePackage, temp.Path);
            var vsix = Directory
                .EnumerateFiles(temp.Path, "*.vsix", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (vsix is null)
            {
                ZipFile.ExtractToDirectory(sourcePackage, installRoot, overwriteFiles: true);
            }
            else
            {
                ZipFile.ExtractToDirectory(vsix, installRoot, overwriteFiles: true);
            }
        }

        var packageJson = Directory
            .EnumerateFiles(installRoot, "package.json", SearchOption.AllDirectories)
            .FirstOrDefault(IsConnectorPackageJson)
            ?? throw new FileNotFoundException("Connector package.json was not found after import.");

        var packageFolder = Path.GetDirectoryName(packageJson)
            ?? throw new InvalidOperationException("Connector package folder could not be resolved.");
        var importedServerPath = FindServerExecutable(packageFolder);

        return new UnifiedJsConnectorImportResult(
            packageFolder,
            importedServerPath,
            ReadPackageVersion(packageJson),
            sourcePackage);
    }

    private static string FindServerExecutable(string packageFolder)
    {
        return Directory
            .EnumerateFiles(packageFolder, ServerExecutableName, SearchOption.AllDirectories)
            .FirstOrDefault()
            ?? throw new FileNotFoundException($"{ServerExecutableName} was not found in the connector package.");
    }

    private static bool IsConnectorPackageJson(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return root.TryGetProperty("displayName", out var displayName)
                   && displayName.GetString()?.Contains("WinCC Unified JS Connector", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadPackageVersion(string packageJsonPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static void ResetInstallFolder(string installRoot)
    {
        var expectedRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlantConfigurator"));
        var target = Path.GetFullPath(installRoot);
        if (!target.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Connector install folder is outside the Plant Configurator app data folder.");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.CreateDirectory(target);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "PlantConfigurator_UnifiedJs_" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }
}
