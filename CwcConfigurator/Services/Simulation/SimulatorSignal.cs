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
    AO,

    /// <summary>Device measurement (Real) in the devices DB; the ID is the MEAS ID the PLC copies it to.</summary>
    DEV,

    /// <summary>Device bit (protection alarm, communication alarm) in the devices DB.</summary>
    DEVBIT
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
    private bool _forceTrue;
    private bool _forceFalse;
    private bool _forceValueOn;
    private double _forceValue;
    private bool _wireBreak;

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

    /// <summary>The signal lives in the devices DB (DB27) instead of the simulator DB.</summary>
    public bool InDeviceDb { get; init; }
    public int BitOffset { get; init; }
    public AnalogScaling? Scaling { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsDigital => Kind is SimulatorSignalKind.DI or SimulatorSignalKind.DO or SimulatorSignalKind.DEVBIT;
    public bool IsReal => Kind is SimulatorSignalKind.COUNTER or SimulatorSignalKind.DEV;
    public bool IsEnabled => Config != SignalConfigState.Disabled;

    /// <summary>The simulator writes this channel (PLC input).</summary>
    public bool IsWritable => Kind is SimulatorSignalKind.DI or SimulatorSignalKind.AI or SimulatorSignalKind.RTD or SimulatorSignalKind.COUNTER
        or SimulatorSignalKind.DEV or SimulatorSignalKind.DEVBIT;

    public bool CanSetBool => IsDigital;
    public bool CanSetAnalog => !IsDigital;
    public bool CanWireBreak => IsWritable && !IsDigital && Kind != SimulatorSignalKind.COUNTER;

    /// <summary>SET TRUE pressed: the signal is TRUE whatever the PLC or the model says (wins over SET FALSE).</summary>
    public bool ForceTrue
    {
        get => _forceTrue;
        set => SetForce(ref _forceTrue, value);
    }

    /// <summary>SET FALSE pressed: the signal is FALSE whatever the PLC or the model says.</summary>
    public bool ForceFalse
    {
        get => _forceFalse;
        set => SetForce(ref _forceFalse, value);
    }

    /// <summary>SET pressed on an analog signal: <see cref="ForceValue"/> (engineering units) is held.</summary>
    public bool ForceValueOn
    {
        get => _forceValueOn;
        set => SetForce(ref _forceValueOn, value);
    }

    public double ForceValue
    {
        get => _forceValue;
        set => SetForce(ref _forceValue, value);
    }

    /// <summary>WIRE BREAK pressed: AI/RTD get 32767, Real values NaN. Wins over SET.</summary>
    public bool WireBreak
    {
        get => _wireBreak;
        set => SetForce(ref _wireBreak, value);
    }

    public bool IsForced => IsDigital ? _forceTrue || _forceFalse : _wireBreak || _forceValueOn;

    /// <summary>The forced value (DO/DI/bits 0/1, analog in engineering units, NaN for wire break), or null.</summary>
    public double? ForcedValue => IsDigital
        ? _forceTrue ? 1 : _forceFalse ? 0 : null
        : _wireBreak ? double.NaN : _forceValueOn ? _forceValue : null;

    public void ClearForces()
    {
        ForceTrue = false;
        ForceFalse = false;
        ForceValueOn = false;
        WireBreak = false;
    }

    /// <summary>Key used to save forces: kind, ID and address (IDs of disabled channels can repeat).</summary>
    public string ForceKey => $"{Kind}:{Id}:{Address}";
    public bool HasScaling => Scaling?.IsValid == true;

    public string KindText => Kind.ToString();
    public string ConfigText => Config switch { SignalConfigState.Inverted => "Inverted", SignalConfigState.Enabled => "", _ => "Disabled" };
    public string DirectionText => IsWritable ? "→ PLC" : "← PLC";

    public string Address => (InDeviceDb ? "DEV " : "") + (IsDigital
        ? $"DBX{ByteOffset}.{BitOffset}"
        : IsReal ? $"DBD{ByteOffset}" : $"DBW{ByteOffset}");

    public string ScalingText => IsDigital ? "" : IsReal ? (InDeviceDb ? "Real (device value, before the PLC scale factor)" : "Real, written as entered") : Scaling?.Describe(Unit) ?? "";

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

    /// <summary>Updates the live value from the reads of the simulator DB and the devices DB.</summary>
    public void UpdateFrom(byte[] simulatorDb, byte[] devicesDb) => UpdateFrom(InDeviceDb ? devicesDb : simulatorDb);

    /// <summary>Updates the live value from a read of the DB this signal lives in.</summary>
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

    private void SetForce<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Set(ref field, value, propertyName))
        {
            OnPropertyChanged(nameof(IsForced));
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
