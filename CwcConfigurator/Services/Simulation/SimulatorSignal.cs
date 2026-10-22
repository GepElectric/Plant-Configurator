using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CwcConfigurator.Domain;

namespace CwcConfigurator.Services.Simulation;

public enum SimulatorSignalKind
{
    DI,
    AI,
    RTD,
    COUNTER,
    DO,
    AO
}

/// <summary>
/// One hardware channel in the simulator DB. DI, AI, RTD and COUNTER are written by the simulator (they are the
/// PLC program's inputs); DO and AO are written by the PLC program and only read by the simulator.
/// A COUNTER channel is one Real per counter module, written as the plain number.
/// </summary>
public sealed class SimulatorSignal : INotifyPropertyChanged
{
    private bool? _boolValue;
    private int? _rawValue;
    private double? _realValue;
    private string _setText = "";

    public required SimulatorSignalKind Kind { get; init; }
    public required int Module { get; init; }
    public required int Channel { get; init; }
    public required int Id { get; init; }
    public SignalConfigState Config { get; init; }
    public string System { get; init; } = "";
    public string Name { get; init; } = "";
    public string PlcName { get; init; } = "";
    public string Unit { get; init; } = "";
    public required int ByteOffset { get; init; }
    public int BitOffset { get; init; }
    public AnalogScaling? Scaling { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsDigital => Kind is SimulatorSignalKind.DI or SimulatorSignalKind.DO;
    public bool IsReal => Kind == SimulatorSignalKind.COUNTER;
    public bool IsEnabled => Config != SignalConfigState.Disabled;

    /// <summary>The simulator writes this channel (PLC input).</summary>
    public bool IsWritable => Kind is SimulatorSignalKind.DI or SimulatorSignalKind.AI or SimulatorSignalKind.RTD or SimulatorSignalKind.COUNTER;

    public bool CanSetBool => IsWritable && IsDigital;
    public bool CanSetAnalog => IsWritable && !IsDigital;
    public bool HasScaling => Scaling?.IsValid == true;

    public string KindText => Kind.ToString();
    public string ConfigText => Config switch { SignalConfigState.Inverted => "Inverted", SignalConfigState.Enabled => "", _ => "Disabled" };
    public string DirectionText => IsWritable ? "→ PLC" : "← PLC";

    public string Address => IsDigital
        ? $"DBX{ByteOffset}.{BitOffset}"
        : IsReal ? $"DBD{ByteOffset}" : $"DBW{ByteOffset}";

    public string ScalingText => IsDigital ? "" : IsReal ? "Real, written as entered" : Scaling?.Describe(Unit) ?? "";

    public string ScalingDetails => IsDigital ? "" : IsReal ? "The counter value is a Real in the simulator DB; the number is written unchanged."
        : Scaling?.Details() ?? "No scaling parameters in DB MEAS / DB PAR: enter raw values.";

    /// <summary>The PLC would report the current raw value as a measurement error (AI, RTD).</summary>
    public bool IsErrorValue => Kind is SimulatorSignalKind.AI or SimulatorSignalKind.RTD && _rawValue is { } raw && AnalogScaling.IsErrorRaw(raw);

    public bool? BoolValue
    {
        get => _boolValue;
        private set
        {
            if (Set(ref _boolValue, value))
            {
                OnPropertyChanged(nameof(ValueText));
                OnPropertyChanged(nameof(IsTrue));
            }
        }
    }

    public int? RawValue
    {
        get => _rawValue;
        private set
        {
            if (Set(ref _rawValue, value))
            {
                OnPropertyChanged(nameof(ValueText));
                OnPropertyChanged(nameof(RawText));
                OnPropertyChanged(nameof(IsErrorValue));
            }
        }
    }

    public bool IsTrue => _boolValue == true;

    public string RawText => IsDigital || _rawValue is null ? "" : _rawValue.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Live value: TRUE/FALSE, or the engineering value with unit (raw when there is no scaling).</summary>
    public string ValueText
    {
        get
        {
            if (IsDigital)
            {
                return _boolValue is null ? "" : _boolValue.Value ? "TRUE" : "FALSE";
            }

            if (IsReal)
            {
                if (_realValue is null)
                {
                    return "";
                }

                var real = _realValue.Value.ToString("0.###", CultureInfo.InvariantCulture);
                return string.IsNullOrWhiteSpace(Unit) ? real : $"{real} {Unit}";
            }

            if (_rawValue is null)
            {
                return "";
            }

            if (IsErrorValue)
            {
                return "ERROR";
            }

            if (!HasScaling)
            {
                return _rawValue.Value.ToString(CultureInfo.InvariantCulture);
            }

            var value = Scaling!.ToEngineering(_rawValue.Value).ToString("0.###", CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(Unit) ? value : $"{value} {Unit}";
        }
    }

    /// <summary>Text typed in the SET box of an analog channel.</summary>
    public string SetText
    {
        get => _setText;
        set => Set(ref _setText, value ?? "");
    }

    public string SearchText => $"{Kind} {Module} {Channel} {Id} {System} {Name} {PlcName} {Address}";

    /// <summary>Updates the live value from a read of the whole simulator DB.</summary>
    public void UpdateFrom(byte[] data)
    {
        if (IsDigital)
        {
            BoolValue = ByteOffset < data.Length ? (data[ByteOffset] & (1 << BitOffset)) != 0 : null;
            return;
        }

        if (IsReal)
        {
            SetReal(ByteOffset + 3 < data.Length
                ? BitConverter.Int32BitsToSingle((data[ByteOffset] << 24) | (data[ByteOffset + 1] << 16) | (data[ByteOffset + 2] << 8) | data[ByteOffset + 3])
                : null);
            return;
        }

        RawValue = ByteOffset + 1 < data.Length ? (short)((data[ByteOffset] << 8) | data[ByteOffset + 1]) : null;
    }

    public void ClearValue()
    {
        BoolValue = null;
        RawValue = null;
        SetReal(null);
    }

    private void SetReal(double? value)
    {
        if (Set(ref _realValue, value, nameof(ValueText)))
        {
            OnPropertyChanged(nameof(RawText));
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
