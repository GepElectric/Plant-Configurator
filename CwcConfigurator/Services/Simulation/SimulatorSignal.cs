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
/// One hardware channel in the simulator DB, as a structure of
/// <list type="bullet">
/// <item><b>Comm</b> – the communication variable, the value in the PLC DB;</item>
/// <item><b>Sim</b> – the simulator variable, the only value the simulator (objects, relay logic, cubicle) reads and writes;</item>
/// <item><b>Set true / Set false</b> (digital), <b>Set</b> + <b>Wire break</b> (analog).</item>
/// </list>
/// PLC inputs (DI, AI, RTD, COUNTER, device values): Comm = Set ?? Sim (wire break: raw 32767 / NaN).
/// PLC outputs (DO, AO): Sim = Set ?? Comm.
/// A COUNTER channel is one Real per counter module, written as the plain number.
/// </summary>
public sealed class SimulatorSignal : INotifyPropertyChanged
{
    private double? _read;
    private int? _readRaw;
    private double? _simStored;
    private bool _simWritten;
    private double? _sim;
    private double? _comm;
    private int? _commRaw;
    private string _setText = "";
    private string _links = "";
    private string _linkWarning = "";
    private bool _forceTrue;
    private bool _forceFalse;
    private bool _forceValueOn;
    private double _forceValue;
    private bool _wireBreak;

    public required SimulatorSignalKind Kind { get; init; }
    public required int Module { get; init; }

    /// <summary>Name from the module table; the number when the module has no name.</summary>
    public string ModuleName { get; init; } = "";

    public string ModuleText => ModuleName.Length > 0 ? ModuleName : Module.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
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

    public bool IsForced => IsDigital ? _forceTrue || _forceFalse : (_wireBreak && CanWireBreak) || _forceValueOn;

    /// <summary>The forced value (DO/DI/bits 0/1, analog in engineering units, NaN for wire break), or null.</summary>
    public double? ForcedValue => IsDigital
        ? _forceTrue ? 1 : _forceFalse ? 0 : null
        : _wireBreak && CanWireBreak ? double.NaN : _forceValueOn ? _forceValue : null;

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
    public bool IsErrorValue => Kind is SimulatorSignalKind.AI or SimulatorSignalKind.RTD && _commRaw is { } raw && AnalogScaling.IsErrorRaw(raw);

    /// <summary>Communication variable: the value in the PLC DB (0/1, engineering units, NaN = wire break), null = unknown.</summary>
    public double? CommValue => _comm;

    /// <summary>Simulator variable: what the simulator reads and writes (0/1, engineering units), null = unknown.</summary>
    public double? SimValue => _sim;

    /// <summary>Raw Int of the communication variable (AI, RTD, AO).</summary>
    public int? RawValue => _commRaw;

    /// <summary>The simulator wrote this input (or it is set): its communication variable goes to the PLC.</summary>
    public bool NeedsWrite => IsWritable && (_simWritten || IsForced);

    public bool? BoolValue => _comm is { } comm ? comm >= 0.5 : null;
    public bool IsTrue => _comm >= 0.5;
    public bool IsSimTrue => _sim >= 0.5;

    public string RawText => IsDigital || _commRaw is null ? "" : _commRaw.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Communication variable as text: TRUE/FALSE, or the engineering value with unit (raw when there is no scaling).</summary>
    public string ValueText => Format(_comm, IsErrorValue);

    /// <summary>Simulator variable as text.</summary>
    public string SimText => Format(_sim, false);

    private string Format(double? value, bool error)
    {
        if (value is not { } number)
        {
            return "";
        }

        if (IsDigital)
        {
            return number >= 0.5 ? "TRUE" : "FALSE";
        }

        if (error || double.IsNaN(number))
        {
            return "ERROR";
        }

        var text = number.ToString("0.###", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(Unit) || (!IsReal && !HasScaling) ? text : $"{text} {Unit}";
    }

    /// <summary>Where the simulator uses this signal (object ports, relay logic, cubicle), e.g. "@Gate1.Open, contact /37.2".</summary>
    public string Links
    {
        get => _links;
        set
        {
            if (Set(ref _links, value ?? ""))
            {
                OnPropertyChanged(nameof(LinkToolTip));
            }
        }
    }

    /// <summary>The places where the simulator uses this signal (to open them from the signal list).</summary>
    public IReadOnlyList<Process.SignalLink> LinkTargets { get; set; } = [];

    /// <summary>Not empty when more than one place writes this input (they overwrite each other).</summary>
    public string LinkWarning
    {
        get => _linkWarning;
        set
        {
            if (Set(ref _linkWarning, value ?? ""))
            {
                OnPropertyChanged(nameof(HasLinkConflict));
                OnPropertyChanged(nameof(LinkToolTip));
            }
        }
    }

    public bool HasLinkConflict => _linkWarning.Length > 0;

    public string LinkToolTip => (_linkWarning.Length > 0 ? "⚠ " + _linkWarning + "\n\n" : "") +
                                 (_links.Length > 0 ? _links.TrimStart('⚠', ' ') + "\nClick to open where it is mapped." : "Not used by the simulator.");

    /// <summary>Text typed in the SET box of an analog channel.</summary>
    public string SetText
    {
        get => _setText;
        set => Set(ref _setText, value ?? "");
    }

    public string SearchText => $"{Kind} {Module} {ModuleName} {Channel} {Id} {System} {Name} {PlcName} {Address} {Links}";

    /// <summary>Updates the value read from the PLC (simulator DB / devices DB).</summary>
    public void UpdateFrom(byte[] simulatorDb, byte[] devicesDb) => UpdateFrom(InDeviceDb ? devicesDb : simulatorDb);

    /// <summary>Updates the value read from the DB this signal lives in.</summary>
    public void UpdateFrom(byte[] data)
    {
        if (IsDigital)
        {
            _read = ByteOffset < data.Length ? (data[ByteOffset] & (1 << BitOffset)) != 0 ? 1 : 0 : null;
            _readRaw = null;
        }
        else if (IsReal)
        {
            _read = ByteOffset + 3 < data.Length
                ? BitConverter.Int32BitsToSingle((data[ByteOffset] << 24) | (data[ByteOffset + 1] << 16) | (data[ByteOffset + 2] << 8) | data[ByteOffset + 3])
                : null;
            _readRaw = null;
        }
        else
        {
            _readRaw = ByteOffset + 1 < data.Length ? (short)((data[ByteOffset] << 8) | data[ByteOffset + 1]) : null;
            _read = _readRaw is { } raw ? HasScaling ? Scaling!.ToEngineering(raw) : raw : null;
        }

        Recompute();
    }

    /// <summary>Not connected: nothing read from the PLC.</summary>
    public void ClearValue()
    {
        _read = null;
        _readRaw = null;
        Recompute();
    }

    /// <summary>The simulator writes its variable (PLC inputs only; PLC outputs come from the PLC).</summary>
    public void WriteSim(double value)
    {
        if (!IsWritable || (_simWritten && _simStored == value))
        {
            return;
        }

        _simWritten = true;
        _simStored = value;
        Recompute();
    }

    /// <summary>Applies the structure rules: inputs Comm = Set ?? Sim, outputs Sim = Set ?? Comm.</summary>
    private void Recompute()
    {
        double? sim, comm;
        int? raw = null;
        if (IsWritable)
        {
            // Until the simulator writes it, the simulator variable follows what is in the PLC.
            sim = _simWritten ? _simStored : _read;
            comm = ForcedValue ?? sim;
            if (!IsDigital && !IsReal)
            {
                raw = _wireBreak && CanWireBreak ? short.MaxValue
                    : comm is { } value && (_simWritten || IsForced) ? ToRaw(value)
                    : _readRaw;
            }
        }
        else
        {
            comm = _read;
            raw = _readRaw;
            sim = ForcedValue ?? comm;
        }

        var changed = Set(ref _sim, sim, nameof(SimValue)) | Set(ref _comm, comm, nameof(CommValue)) | Set(ref _commRaw, raw, nameof(RawValue));
        if (changed)
        {
            OnPropertyChanged(nameof(ValueText));
            OnPropertyChanged(nameof(SimText));
            OnPropertyChanged(nameof(RawText));
            OnPropertyChanged(nameof(BoolValue));
            OnPropertyChanged(nameof(IsTrue));
            OnPropertyChanged(nameof(IsSimTrue));
            OnPropertyChanged(nameof(IsErrorValue));
        }
    }

    /// <summary>Raw Int of an engineering value (NaN = wire break → 32767).</summary>
    public short ToRaw(double value) => double.IsNaN(value) ? short.MaxValue
        : HasScaling ? Scaling!.ToRaw(value)
        : (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);

    private void SetForce<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Set(ref field, value, propertyName))
        {
            OnPropertyChanged(nameof(IsForced));
            Recompute();
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
