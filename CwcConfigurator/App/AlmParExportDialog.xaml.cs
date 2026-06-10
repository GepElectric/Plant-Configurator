using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using CwcConfigurator.Domain;
using CwcConfigurator.Services;

namespace CwcConfigurator.App;

public partial class AlmParExportDialog : Window
{
    private readonly CwcProject _project;
    private readonly AlmParExportConfig _config;
    private readonly ObservableCollection<AlmParExportPlcMapping> _mappings;
    private readonly DescriptionTemplateService _templateService = new();
    private CancellationTokenSource? _runCts;

    public AlmParExportDialog(CwcProject project)
    {
        InitializeComponent();
        _project = project;
        _config = project.AlmParExport ??= new AlmParExportConfig();
        _config.SyncWithDatabases(project.PlcDatabases);
        _mappings = new ObservableCollection<AlmParExportPlcMapping>(_config.PlcMappings);
        MappingGrid.ItemsSource = _mappings;
        LoadConfig();
    }

    public bool HasChanges { get; private set; }

    private void LoadConfig()
    {
        DocNumberText.Text = _config.DocNumber ?? "";
        AuthorText.Text = _config.Author ?? "";
        DateText.Text = string.IsNullOrWhiteSpace(_config.DocDate)
            ? DateTime.Now.ToString("d.M.yyyy", CultureInfo.InvariantCulture)
            : _config.DocDate;
        PlantNameText.Text = string.IsNullOrWhiteSpace(_config.PlantFullName) ? _project.ProjectName : _config.PlantFullName;
        FbNumberText.Text = string.IsNullOrWhiteSpace(_config.FbNumber) ? "01" : _config.FbNumber;
        CustomerBlockText.Text = _config.CustomerBlock ?? "";
        OutputPathText.Text = string.IsNullOrWhiteSpace(_config.LastOutputPath)
            ? GetDefaultOutputFolder()
            : _config.LastOutputPath;
        PdfCheck.IsChecked = _config.ExportPdf;
        DocxCheck.IsChecked = _config.ExportDocx;
        RefreshLanguageDropdown();
    }

    private void SaveConfig()
    {
        CommitGridEdit();
        _config.DocNumber = DocNumberText.Text.Trim();
        _config.Author = AuthorText.Text.Trim();
        _config.DocDate = DateText.Text.Trim();
        _config.PlantFullName = PlantNameText.Text.Trim();
        _config.FbNumber = string.IsNullOrWhiteSpace(FbNumberText.Text) ? "01" : FbNumberText.Text.Trim();
        _config.CustomerBlock = CustomerBlockText.Text ?? "";
        _config.LastOutputPath = OutputPathText.Text.Trim();
        _config.ExportPdf = PdfCheck.IsChecked == true;
        _config.ExportDocx = DocxCheck.IsChecked == true;
        _config.ExportLanguageCode = GetSelectedLanguageCode();
        _config.ExportLanguage = _config.ExportLanguageCode.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? "ENG" : "LANG2";
        _config.PlcMappings = _mappings.ToList();
        HasChanges = true;
    }

    private void RefreshLanguageDropdown()
    {
        var selected = string.IsNullOrWhiteSpace(_config.ExportLanguageCode) ? "en-US" : _config.ExportLanguageCode;
        var languages = new List<string> { "en-US" };
        languages.AddRange(_project.DescriptionTextLists.TextLists.Languages
            .Where(lang => !lang.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            .OrderBy(lang => lang, StringComparer.OrdinalIgnoreCase));
        LanguageCombo.ItemsSource = languages.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        LanguageCombo.SelectedItem = languages.Contains(selected, StringComparer.OrdinalIgnoreCase) ? selected : "en-US";
    }

    private string GetSelectedLanguageCode() => LanguageCombo.SelectedItem as string ?? "en-US";

    private string GetDefaultOutputFolder()
    {
        if (!string.IsNullOrWhiteSpace(_project.Paths.OutputFolder))
        {
            return _project.Paths.OutputFolder;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select output folder for generated documents",
            InitialDirectory = Directory.Exists(OutputPathText.Text) ? OutputPathText.Text : GetDefaultOutputFolder()
        };

        if (dialog.ShowDialog(this) == true)
        {
            OutputPathText.Text = dialog.FolderName;
        }
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        SaveConfig();

        if (_config.ExportPdf == false && _config.ExportDocx == false)
        {
            MessageBox.Show(this, "Select at least one output format (PDF or DOCX).", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var outputFolder = OutputPathText.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputFolder) || !Directory.Exists(outputFolder))
        {
            MessageBox.Show(this, "Select a valid output folder.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!_config.PlcMappings.Any(mapping => mapping.Include && (mapping.ExportAlm || mapping.ExportPar)))
        {
            MessageBox.Show(this, "Include at least one PLC and select ALM or PAR for export.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string tempJson;
        try
        {
            var payload = BuildPayload(outputFolder);
            tempJson = Path.Combine(Path.GetTempPath(), "alm_par_export_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(tempJson, JsonSerializer.Serialize(payload), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Error preparing export:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _runCts = new CancellationTokenSource();
        SetRunning(true, "Generating...");

        try
        {
            SetRunning(true, "Checking Python packages...");
            await EnsurePythonDependenciesAsync(_runCts.Token);
            SetRunning(true, "Generating...");
            var result = await RunPythonAsync(tempJson, _runCts.Token);
            if (result.ExitCode != 0)
            {
                MessageBox.Show(this, "Generation failed:\n" + result.Error, Title, MessageBoxButton.OK, MessageBoxImage.Error);
                SetRunning(false, "Generation failed.");
                return;
            }

            SetRunning(false, "Done! " + result.Output.Trim());
            TryOpenFolder(outputFolder);
        }
        catch (OperationCanceledException)
        {
            SetRunning(false, "Cancelled.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Error:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            SetRunning(false, "Error.");
        }
        finally
        {
            try { File.Delete(tempJson); } catch { }
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private Dictionary<string, object> BuildPayload(string outputFolder)
    {
        var languageCode = GetSelectedLanguageCode();
        var useEnglish = languageCode.Equals("en-US", StringComparison.OrdinalIgnoreCase);
        var priorityMap = BuildPriorityMap(languageCode);
        var plcSections = new List<Dictionary<string, object>>();

        foreach (var mapping in _config.PlcMappings.Where(item => item.Include && (item.ExportAlm || item.ExportPar)).OrderBy(item => item.PlcName, StringComparer.OrdinalIgnoreCase))
        {
            var database = _project.PlcDatabases.FirstOrDefault(item => item.Id == mapping.PlcDatabaseId);
            if (database is null)
            {
                continue;
            }

            var textMapping = _project.DescriptionTextLists.Mappings.FirstOrDefault(item => item.PlcDatabaseId == database.Id);
            var alarms = mapping.ExportAlm
                ? database.DbCatalog.AlarmRows
                    .Where(row => IsTruthy(row.Used))
                    .OrderBy(row => row.Id)
                    .Select(row =>
                    {
                        priorityMap.TryGetValue(ParseInt(row.AlarmClass), out var alarmType);
                        return new Dictionary<string, object>
                        {
                            ["id"] = row.Id,
                            ["system"] = useEnglish ? row.SystemNameEnglish : row.SystemNameOther,
                            ["name"] = useEnglish ? row.NameEnglish : row.NameOther,
                            ["description"] = ResolveDescription(textMapping?.AlmDescriptionsList, row.Id, languageCode, row, "ALM"),
                            ["type"] = alarmType ?? "Unknown (" + row.AlarmClass + ")"
                        };
                    })
                    .ToList()
                : [];

            var parameters = mapping.ExportPar
                ? database.DbCatalog.ParameterRows
                    .Where(row => IsTruthy(row.InUse))
                    .OrderBy(row => row.Id)
                    .Select(row => new Dictionary<string, object>
                    {
                        ["id"] = row.Id,
                        ["name"] = useEnglish ? row.NameEnglish : row.NameOther,
                        ["description"] = ResolveDescription(textMapping?.ParDescriptionsList, row.Id, languageCode, row, "PAR"),
                        ["value"] = GetParameterDisplayValue(row),
                        ["unit"] = string.IsNullOrWhiteSpace(row.Unit) ? "-" : row.Unit
                    })
                    .ToList()
                : [];

            plcSections.Add(new Dictionary<string, object>
            {
                ["plc_name"] = mapping.PlcName,
                ["alm_exclude_spec"] = mapping.AlmExcludeSpec ?? "",
                ["par_exclude_spec"] = mapping.ParExcludeSpec ?? "",
                ["alarms"] = alarms,
                ["parameters"] = parameters
            });
        }

        var baseDir = AppContext.BaseDirectory;
        var formats = new List<string>();
        if (_config.ExportPdf) formats.Add("pdf");
        if (_config.ExportDocx) formats.Add("docx");

        return new Dictionary<string, object>
        {
            ["formats"] = formats,
            ["output_folder"] = outputFolder,
            ["base_filename"] = BuildBaseFileName(languageCode),
            ["template_path"] = Path.Combine(baseDir, "Templates", GetTemplateFileName(languageCode)),
            ["doc_meta"] = new Dictionary<string, object>
            {
                ["doc_number"] = _config.DocNumber ?? "",
                ["author"] = _config.Author ?? "",
                ["date"] = _config.DocDate ?? "",
                ["plant_name"] = string.IsNullOrWhiteSpace(_config.PlantFullName) ? _project.ProjectName : _config.PlantFullName,
                ["title"] = BuildDocumentTitle(languageCode),
                ["customer_block"] = _config.CustomerBlock ?? "",
                ["fb_number"] = string.IsNullOrWhiteSpace(_config.FbNumber) ? "01" : _config.FbNumber.Trim(),
                ["language_code"] = languageCode
            },
            ["plcs"] = plcSections,
            ["fonts_dir"] = Path.Combine(baseDir, "plant_reports", "fonts")
        };
    }

    private string ResolveDescription(string? listName, int id, string languageCode, DbStringRow row, string scope)
    {
        var fromTextList = _project.DescriptionTextLists.TextLists.GetText(listName ?? "", id, languageCode);
        if (!string.IsNullOrWhiteSpace(fromTextList))
        {
            return _templateService.ResolveTemplateText(fromTextList, row.DescriptionTemplateKey, _project.DescriptionTemplates, scope, languageCode);
        }

        return _templateService.ResolveDescription(row, _project.DescriptionTemplates, scope, languageCode);
    }

    private static Dictionary<int, string> BuildPriorityMap(string languageCode)
    {
        if (!string.IsNullOrWhiteSpace(languageCode) && languageCode.StartsWith("nb", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<int, string>
            {
                [0] = "Alarm",
                [1] = "Startsperre",
                [2] = "Normal stopp",
                [3] = "Hurtigstopp",
                [4] = "Nodstopp"
            };
        }

        return new Dictionary<int, string>
        {
            [0] = "Alarm",
            [1] = "Start interlock",
            [2] = "Normal shutdown",
            [3] = "Quick shutdown",
            [4] = "Emergency shutdown"
        };
    }

    private string BuildBaseFileName(string languageCode)
    {
        var prefix = string.IsNullOrWhiteSpace(_config.DocNumber) ? (_project.ProjectName ?? "document").Trim() : _config.DocNumber.Trim();
        return prefix + "_" + GetLocalizedFileSuffix(languageCode);
    }

    private string BuildDocumentTitle(string languageCode)
    {
        var docNumber = (_config.DocNumber ?? "").Trim();
        var plantName = string.IsNullOrWhiteSpace(_config.PlantFullName) ? _project.ProjectName : _config.PlantFullName.Trim();
        var category = GetLocalizedDocumentCategory(languageCode);
        return string.IsNullOrWhiteSpace(docNumber) ? plantName + " " + category : docNumber + " - " + plantName + " " + category;
    }

    private static string GetTemplateFileName(string languageCode) => languageCode.StartsWith("nb", StringComparison.OrdinalIgnoreCase)
        ? "alm_par_template_nb.docx"
        : "alm_par_template.docx";

    private static string GetLocalizedDocumentCategory(string languageCode) => languageCode.StartsWith("nb", StringComparison.OrdinalIgnoreCase)
        ? "alarm og parameterliste"
        : "alarm and parameter list";

    private static string GetLocalizedFileSuffix(string languageCode) => languageCode.StartsWith("nb", StringComparison.OrdinalIgnoreCase)
        ? "Alarm_og_parameterliste"
        : "Alarm_and_parameter_list";

    private static string GetParameterDisplayValue(DbStringRow row)
    {
        return (row.ParameterType ?? "").Trim() switch
        {
            "1" => row.IntegerValue,
            "2" => row.TimeValue,
            _ => row.RealValue
        };
    }

    private static bool IsTruthy(string value)
    {
        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number != 0;
        }

        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("enabled", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseInt(string value) => int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : -1;

    private static string? _resolvedPython;

    private static string ResolvePythonExecutable()
    {
        if (_resolvedPython is not null)
        {
            return _resolvedPython;
        }

        // 0) Bundled Python in Tools/python/ next to the application. This is
        //    the canonical, version-pinned interpreter that ships with Plant
        //    Configurator and already has python-docx (+ pywin32) installed.
        // 1) Windows Python Launcher (py.exe) ships with the python.org installer.
        // 2) python.exe on PATH.
        // 3) Common per-user install locations from the python.org installer.
        // 4) Common system-wide install locations.
        var candidates = new List<string>();

        var bundled = Path.Combine(AppContext.BaseDirectory, "Tools", "python", "python.exe");
        if (File.Exists(bundled))
        {
            return _resolvedPython = bundled;
        }

        candidates.Add("py.exe");
        candidates.Add("python.exe");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            var pythonRoot = Path.Combine(localAppData, "Programs", "Python");
            if (Directory.Exists(pythonRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(pythonRoot, "Python3*"))
                {
                    var exe = Path.Combine(dir, "python.exe");
                    if (File.Exists(exe)) { candidates.Add(exe); }
                }
            }
        }

        foreach (var root in new[] { @"C:\Python313", @"C:\Python312", @"C:\Python311", @"C:\Python310", @"C:\Python39" })
        {
            var exe = Path.Combine(root, "python.exe");
            if (File.Exists(exe)) { candidates.Add(exe); }
        }

        foreach (var candidate in candidates)
        {
            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate))
                {
                    return _resolvedPython = candidate;
                }
                continue;
            }

            try
            {
                using var probe = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                probe.Start();
                probe.WaitForExit(3000);
                if (probe.ExitCode == 0)
                {
                    return _resolvedPython = candidate;
                }
            }
            catch
            {
                // candidate not on PATH; continue
            }
        }

        return _resolvedPython = "";
    }

    private static InvalidOperationException PythonNotFoundException() =>
        new("Python 3 was not found.\n\n"
            + "Plant Configurator normally ships its own bundled Python at Tools/python/python.exe -- if you see this message, the bundle is missing from your installation. "
            + "Reinstall Plant Configurator (or copy the Tools/python/ folder from a working installation).\n\n"
            + "Alternatively, install Python 3 (3.10 or newer) from https://www.python.org/downloads/ with 'Add python.exe to PATH' enabled, then restart Plant Configurator.");

    private async Task EnsurePythonDependenciesAsync(CancellationToken token)
    {
        var python = ResolvePythonExecutable();
        if (string.IsNullOrEmpty(python))
        {
            throw PythonNotFoundException();
        }

        var packages = new List<string> { "python-docx" };
        if (_config.ExportPdf)
        {
            packages.Add("pywin32");
        }

        var checkCode = _config.ExportPdf
            ? "import docx, win32com.client"
            : "import docx";
        var check = await RunProcessAsync(python, "-c " + QuoteArgument(checkCode), token);
        if (check.ExitCode == 0)
        {
            return;
        }

        SetRunning(true, "Installing Python packages...");
        var install = await RunProcessAsync(python, "-m pip install --user " + string.Join(" ", packages), token);
        if (install.ExitCode != 0)
        {
            throw new InvalidOperationException("Python packages are missing and automatic install failed:\n" + install.Error);
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> RunPythonAsync(string tempJson, CancellationToken token)
    {
        var python = ResolvePythonExecutable();
        if (string.IsNullOrEmpty(python))
        {
            throw PythonNotFoundException();
        }
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "alm_par_doc.py");
        return await RunProcessAsync(python, QuoteArgument(scriptPath) + " " + QuoteArgument(tempJson), token);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(string fileName, string arguments, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static string QuoteArgument(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private void CancelRun_Click(object sender, RoutedEventArgs e)
    {
        _runCts?.Cancel();
    }

    private void SetRunning(bool running, string status)
    {
        GenerateButton.IsEnabled = !running;
        CancelRunButton.IsEnabled = running;
        Progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status;
    }

    private void MappingGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        HasChanges = true;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveConfig();
    }

    private void CommitGridEdit()
    {
        MappingGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        MappingGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private static void TryOpenFolder(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", QuoteArgument(folder)) { UseShellExecute = true });
        }
        catch
        {
        }
    }
}
