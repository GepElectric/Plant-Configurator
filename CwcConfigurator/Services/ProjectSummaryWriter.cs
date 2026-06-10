using System.Text;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services;

public static class ProjectSummaryWriter
{
    public static string Write(CwcProject project)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Project: {project.ProjectName}");
        builder.AppendLine($"Schema: {project.SchemaVersion}");
        builder.AppendLine();
        builder.AppendLine("Paths");
        builder.AppendLine($"PLC source: {project.Paths.PlcToConfigSourceFolder}");
        builder.AppendLine($"R source: {project.Paths.RSourceFolder}");
        builder.AppendLine($"Output: {project.Paths.OutputFolder}");
        builder.AppendLine();
        builder.AppendLine("Tables");
        builder.AppendLine($"DI: {project.DigitalInputs.Count}");
        builder.AppendLine($"DO: {project.DigitalOutputs.Count}");
        builder.AppendLine($"AI: {project.AnalogInputs.Count}");
        builder.AppendLine($"RTD: {project.RtdInputs.Count}");
        builder.AppendLine($"COUNTER: {project.Counters.Count}");
        builder.AppendLine($"AO: {project.AnalogOutputs.Count}");
        return builder.ToString();
    }
}
