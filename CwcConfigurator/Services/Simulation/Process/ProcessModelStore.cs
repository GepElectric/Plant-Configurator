using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// Reads and writes the plant model of a project: "&lt;project&gt;.simulator.yaml" next to the .plantcfg, one model per
/// PLC database. YAML so it stays readable and can be edited by hand (or in a chat with Claude).
/// </summary>
public static class ProcessModelStore
{
    private const string Header =
        "# Plant Configurator - Simulator plant model\n" +
        "# plcs: <PLC database>: objects: name, type, options, params, tables, ports, x/y (SCADA position)\n" +
        "# ports: DI:23 / DO:188 / AI:4 / RTD:27 / COUNTER:30 / AO:2 = signal ID, @Object.Port or @PLC/Object.Port = link, =value = constant\n";

    public static string PathFor(string projectPath) =>
        Path.Combine(Path.GetDirectoryName(projectPath) ?? "", Path.GetFileNameWithoutExtension(projectPath) + ".simulator.yaml");

    public static Dictionary<string, ProcessModel> Load(string path)
    {
        var models = new Dictionary<string, ProcessModel>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return models;
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var file = deserializer.Deserialize<ModelFile>(File.ReadAllText(path)) ?? new ModelFile();
        foreach (var (plc, model) in file.Plcs ?? [])
        {
            models[plc] = Normalize(model ?? new ProcessModel());
        }

        return models;
    }

    public static void Save(string path, IReadOnlyDictionary<string, ProcessModel> models)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitEmptyCollections | DefaultValuesHandling.OmitNull)
            .Build();
        var file = new ModelFile
        {
            Plcs = models.Where(pair => pair.Value.Objects.Count > 0).ToDictionary(pair => pair.Key, pair => pair.Value)
        };
        var text = Header + serializer.Serialize(file);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    private static ProcessModel Normalize(ProcessModel model)
    {
        foreach (var item in model.Objects)
        {
            item.Name ??= "";
            item.Type ??= "";
            item.Description ??= "";
            item.Options = new Dictionary<string, string>(item.Options ?? [], StringComparer.OrdinalIgnoreCase);
            item.Params = new Dictionary<string, double>(item.Params ?? [], StringComparer.OrdinalIgnoreCase);
            item.Tables = new Dictionary<string, List<double>>(item.Tables ?? [], StringComparer.OrdinalIgnoreCase);
            item.Ports = new Dictionary<string, string>((item.Ports ?? []).Where(pair => !string.IsNullOrWhiteSpace(pair.Value)), StringComparer.OrdinalIgnoreCase);
        }

        return model;
    }

    private sealed class ModelFile
    {
        public Dictionary<string, ProcessModel>? Plcs { get; set; } = [];
    }
}
