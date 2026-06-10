using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CwcConfigurator.Domain;

namespace CwcConfigurator.App;

public sealed class DbConditionalCellBrushConverter : IMultiValueConverter
{
    private static DbConditionalColorSettings _settings = DbConditionalColorSettings.CreateDefaults();
    private static bool _enabled = true;
    private static Brush _rowBrush = Brushes.White;
    private static Brush _alternateRowBrush = new SolidColorBrush(Color.FromRgb(246, 248, 250));

    public static void SetSettings(DbConditionalColorSettings settings, bool enabled = true)
    {
        _settings = settings.Clone();
        _enabled = enabled;
    }

    public static void SetTableBrushes(Brush rowBrush, Brush alternateRowBrush)
    {
        _rowBrush = rowBrush;
        _alternateRowBrush = alternateRowBrush;
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var column = parameter?.ToString() ?? "";
        if (column.Equals("TABLE", StringComparison.OrdinalIgnoreCase))
        {
            return GetTableBrush(values);
        }

        if (column.Equals("SIGNAL_ROW", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveSignalRowBrush(values);
        }

        var isRowBackground = column.Equals("DB_ALM_ROW", StringComparison.OrdinalIgnoreCase)
                              || column.Equals("DB_PAR_ROW", StringComparison.OrdinalIgnoreCase);
        if (!_enabled)
        {
            return Brushes.Transparent;
        }

        var row = values.OfType<DbStringRow>().FirstOrDefault();
        if (row is null)
        {
            return Brushes.Transparent;
        }

        if (column.Equals("DB_ALM_ROW", StringComparison.OrdinalIgnoreCase))
        {
            if (_settings.DbAlarmColorRequiresUsed && !IsTrue(row.Used))
            {
                return Brushes.Transparent;
            }

            return CreateBrush(GetAlarmClassColor(row.AlarmClass));
        }

        if (column.Equals("DB_PAR_ROW", StringComparison.OrdinalIgnoreCase))
        {
            return IsTrue(row.InUse) ? CreateBrush(_settings.DbParameterUsedColor) : Brushes.Transparent;
        }

        if (column.Equals("I_VAL", StringComparison.OrdinalIgnoreCase) && IsParameterType(row.ParameterType, "1", "INT")
            || column.Equals("T_VAL", StringComparison.OrdinalIgnoreCase) && IsParameterType(row.ParameterType, "2", "TIME")
            || column.Equals("R_VAL", StringComparison.OrdinalIgnoreCase) && IsParameterType(row.ParameterType, "0", "REAL"))
        {
            return CreateBrush(_settings.DbParameterActiveValueColor);
        }

        return Brushes.Transparent;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        return targetTypes.Select(_ => Binding.DoNothing).ToArray();
    }

    private static bool IsTrue(string value)
    {
        var text = (value ?? "").Trim();
        return text.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
               || text.Equals("1", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("1 -", StringComparison.OrdinalIgnoreCase)
               || text.Equals("USED", StringComparison.OrdinalIgnoreCase)
               || text.Equals("YES", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsParameterType(string value, string code, string text)
    {
        var raw = (value ?? "").Trim();
        return raw.Equals(code, StringComparison.OrdinalIgnoreCase)
               || raw.StartsWith(code + " -", StringComparison.OrdinalIgnoreCase)
               || raw.Equals(text, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetAlarmClassColor(string alarmClass)
    {
        var value = (alarmClass ?? "").Trim().ToUpperInvariant();
        if (value.StartsWith("0 -", StringComparison.Ordinal)) return _settings.DbAlarmAlarmColor;
        if (value.StartsWith("1 -", StringComparison.Ordinal)) return _settings.DbAlarmStartInterlockColor;
        if (value.StartsWith("2 -", StringComparison.Ordinal)) return _settings.DbAlarmNormalShutdownColor;
        if (value.StartsWith("3 -", StringComparison.Ordinal)) return _settings.DbAlarmQuickShutdownColor;
        if (value.StartsWith("4 -", StringComparison.Ordinal)) return _settings.DbAlarmEmergencyShutdownColor;

        return value switch
        {
            "0" => _settings.DbAlarmAlarmColor,
            "ALARM" => _settings.DbAlarmAlarmColor,
            "1" => _settings.DbAlarmStartInterlockColor,
            "START INTERLOCK" => _settings.DbAlarmStartInterlockColor,
            "2" => _settings.DbAlarmNormalShutdownColor,
            "NORMAL SHUTDOWN" => _settings.DbAlarmNormalShutdownColor,
            "3" => _settings.DbAlarmQuickShutdownColor,
            "QUICK SHUTDOWN" => _settings.DbAlarmQuickShutdownColor,
            "4" => _settings.DbAlarmEmergencyShutdownColor,
            "EMERGENCY SHUTDOWN" => _settings.DbAlarmEmergencyShutdownColor,
            _ => _settings.DbAlarmUsedColor
        };
    }

    private static Brush CreateBrush(string color)
    {
        try
        {
            return (Brush)new BrushConverter().ConvertFromString(color)!;
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    private static Brush GetTableBrush(object[] values)
    {
        var alternationIndex = values.OfType<int>().FirstOrDefault();
        return alternationIndex % 2 == 1 ? _alternateRowBrush : _rowBrush;
    }

    private static Brush ResolveSignalRowBrush(object[] values)
    {
        if (!_enabled)
        {
            return Brushes.Transparent;
        }

        var row = values.FirstOrDefault(value => value is not null);
        if (row is null)
        {
            return Brushes.Transparent;
        }

        var hasValidationError = row switch
        {
            DigitalSignalConfig signal => signal.HasValidationError,
            AnalogSignalConfig signal => signal.HasValidationError,
            CounterConfig signal => signal.HasValidationError,
            AnalogOutputConfig signal => signal.HasValidationError,
            _ => false
        };
        if (hasValidationError)
        {
            return CreateBrush("#FF4D4D");
        }

        var config = row switch
        {
            DigitalSignalConfig signal => signal.Config,
            AnalogSignalConfig signal => signal.Config,
            CounterConfig signal => signal.Config,
            AnalogOutputConfig signal => signal.Config,
            DeviceConfig signal => signal.Config,
            _ => SignalConfigState.Disabled
        };

        return config switch
        {
            SignalConfigState.Enabled => CreateBrush("#50A050"),
            SignalConfigState.Inverted => CreateBrush("#E0B0F0"),
            _ => Brushes.Transparent
        };
    }
}
