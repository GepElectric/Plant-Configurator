using System.Globalization;
using System.Windows.Data;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public sealed class ModuleNameDisplayConverter : IValueConverter
{
    private static readonly Dictionary<string, Dictionary<int, string>> NamesByDb = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<int, string> MeasurementTypesById = new();

    public static void SetModuleNames(ModuleCatalog catalog)
    {
        NamesByDb["DI"] = BuildMap(catalog.DigitalInputs);
        NamesByDb["DO"] = BuildMap(catalog.DigitalOutputs);
        NamesByDb["AI"] = BuildMap(catalog.AnalogInputs);
        NamesByDb["RTD"] = BuildMap(catalog.RtdInputs);
        NamesByDb["COUNTER"] = BuildMap(catalog.Counters);
        NamesByDb["AO"] = BuildMap(catalog.AnalogOutputs);
    }

    public static void SetMeasurementTypes(
        IEnumerable<AnalogSignalConfig> analogInputs,
        IEnumerable<AnalogSignalConfig> rtdInputs,
        IEnumerable<CounterConfig> counters,
        IEnumerable<DeviceConfig> devices)
    {
        var types = new Dictionary<int, string>();
        AddMeasurementTypes(types, analogInputs.Select(row => row.Id), "AI");
        AddMeasurementTypes(types, rtdInputs.Select(row => row.Id), "RTD");
        AddMeasurementTypes(types, counters.Select(row => row.Id), "COUNTER");
        foreach (var device in devices.Where(device => device.Config == SignalConfigState.Enabled))
        {
            AddMeasurementTypes(types, Enumerable.Range(device.FirstValueId, 24), "DEVICE");
        }

        MeasurementTypesById = types;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var dbName = parameter?.ToString() ?? "";
        if (dbName.Equals("MEAS", StringComparison.OrdinalIgnoreCase) && value is DbStringRow row)
        {
            return FormatMeasurementModule(row);
        }

        var rawValue = value?.ToString()?.Trim() ?? "";
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var moduleNumber)
            || !NamesByDb.TryGetValue(dbName, out var names)
            || !names.TryGetValue(moduleNumber, out var moduleName)
            || string.IsNullOrWhiteSpace(moduleName))
        {
            return rawValue;
        }

        return moduleName;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }

    private static Dictionary<int, string> BuildMap(IEnumerable<ModuleNameRow> rows)
    {
        return rows
            .GroupBy(row => row.ModuleNumber)
            .ToDictionary(group => group.Key, group => group.First().ModuleName);
    }

    private static void AddMeasurementTypes(Dictionary<int, string> target, IEnumerable<int> ids, string type)
    {
        foreach (var id in ids)
        {
            target.TryAdd(id, type);
        }
    }

    private static string FormatMeasurementModule(DbStringRow row)
    {
        var rawValue = (row.ModuleName ?? "").Trim();
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var moduleNumber))
        {
            return rawValue;
        }

        var type = (row.Type ?? "").Trim();
        if (string.IsNullOrWhiteSpace(type) && MeasurementTypesById.TryGetValue(row.Id, out var configuredType))
        {
            type = configuredType;
        }

        type = type.ToUpperInvariant();
        var dbName = type switch
        {
            "AI" or "ANALOG" or "ANALOG INPUT" => "AI",
            "RTD" => "RTD",
            "COUNTER" => "COUNTER",
            _ when type.Contains("COUNTER", StringComparison.OrdinalIgnoreCase) => "COUNTER",
            _ when type.Contains("RTD", StringComparison.OrdinalIgnoreCase) => "RTD",
            _ when type.Contains("AI", StringComparison.OrdinalIgnoreCase) || type.Contains("ANALOG", StringComparison.OrdinalIgnoreCase) => "AI",
            _ => ""
        };

        if (string.IsNullOrWhiteSpace(dbName)
            || !NamesByDb.TryGetValue(dbName, out var names)
            || !names.TryGetValue(moduleNumber, out var moduleName)
            || string.IsNullOrWhiteSpace(moduleName))
        {
            return rawValue;
        }

        return $"{rawValue} - {moduleName}";
    }
}

public sealed class AlarmReferenceDisplayConverter : IMultiValueConverter
{
    private static Dictionary<int, DbStringRow> AlarmRowsById = [];

    public static void SetAlarmRows(IEnumerable<DbStringRow> alarmRows)
    {
        AlarmRowsById = alarmRows
            .GroupBy(row => row.Id)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var alarmId = values.Length > 0 ? values[0]?.ToString()?.Trim() ?? "" : "";
        var alarmConfig = values.Length > 1 ? values[1]?.ToString()?.Trim() ?? "" : "";
        if (!IsEnabled(alarmConfig) || !int.TryParse(alarmId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id < 0)
        {
            return "";
        }

        var field = parameter?.ToString() ?? "";
        if (field.Equals("Id", StringComparison.OrdinalIgnoreCase))
        {
            return alarmId;
        }

        if (!AlarmRowsById.TryGetValue(id, out var alarmRow))
        {
            return "";
        }

        return field.Equals("PlcStructure", StringComparison.OrdinalIgnoreCase)
            ? alarmRow.PlcStructure
            : field.Equals("PlcName", StringComparison.OrdinalIgnoreCase)
                ? alarmRow.PlcName
                : "";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        return targetTypes.Select(_ => Binding.DoNothing).ToArray();
    }

    private static bool IsEnabled(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number != 0;
        }

        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("enabled", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class AlarmIdReferenceDisplayConverter : IValueConverter
{
    private static Dictionary<int, DbStringRow> AlarmRowsById = [];

    public static void SetAlarmRows(IEnumerable<DbStringRow> alarmRows)
    {
        AlarmRowsById = alarmRows
            .GroupBy(row => row.Id)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var rawId = value?.ToString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(rawId) || !int.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return "";
        }

        var field = parameter?.ToString() ?? "";
        if (field.Equals("Id", StringComparison.OrdinalIgnoreCase))
        {
            return rawId;
        }

        if (!AlarmRowsById.TryGetValue(id, out var row))
        {
            return "";
        }

        return field.Equals("PlcStructure", StringComparison.OrdinalIgnoreCase)
            ? row.PlcStructure
            : field.Equals("PlcName", StringComparison.OrdinalIgnoreCase)
                ? row.PlcName
                : "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

public sealed class BooleanNumberDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var rawValue = value?.ToString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return "";
        }

        if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number == 0 ? "False" : "True";
        }

        if (rawValue.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return "True";
        }

        if (rawValue.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return "False";
        }

        return rawValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}

public sealed class ParameterReferenceDisplayConverter : IValueConverter
{
    private static Dictionary<int, DbStringRow> ParameterRowsById = [];

    public static void SetParameterRows(IEnumerable<DbStringRow> parameterRows)
    {
        ParameterRowsById = parameterRows
            .GroupBy(row => row.Id)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var rawId = value?.ToString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(rawId) || !int.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return "";
        }

        var field = parameter?.ToString() ?? "";
        if (field.Equals("Id", StringComparison.OrdinalIgnoreCase))
        {
            return rawId;
        }

        if (!ParameterRowsById.TryGetValue(id, out var row))
        {
            return "";
        }

        if (field.Equals("PlcStructure", StringComparison.OrdinalIgnoreCase))
        {
            return row.PlcStructure;
        }

        if (field.Equals("PlcName", StringComparison.OrdinalIgnoreCase))
        {
            return row.PlcName;
        }

        if (field.Equals("Value", StringComparison.OrdinalIgnoreCase))
        {
            return (row.ParameterType ?? "").Trim() switch
            {
                "1" => row.IntegerValue,
                "2" => row.TimeValue,
                _ => row.RealValue
            };
        }

        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
