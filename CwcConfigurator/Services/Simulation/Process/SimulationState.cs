using System.Globalization;
using System.Reflection;

namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// The running state saved with the model ("Save" keeps the last state of everything): the inner state of every
/// object (positions, levels, latches, timers…), forced object outputs, picked-up relays (self-holding included) and
/// latched emergency stops. Loading the simulation continues from it; Reset goes back to the initial state.
/// </summary>
public sealed class SavedState
{
    /// <summary>Object name → state value (field name, "field[i]" for arrays, "!Port" for a forced output).</summary>
    public Dictionary<string, Dictionary<string, double>> Objects { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Picked-up relays: "tag|on|elapsed" ("@/37.4" for a coil without a tag).</summary>
    public List<string> Relays { get; set; } = [];

    /// <summary>Latched emergency stops: "cubicle index/item index".</summary>
    public List<string> Latched { get; set; } = [];
}

public static class SimulationStateIo
{
    private const string ForcePrefix = "!";

    /// <summary>Writes the present state of the engine into its models (<see cref="ProcessModel.State"/>).</summary>
    public static void Capture(ProcessEngine engine)
    {
        foreach (var (plc, model) in engine.Models)
        {
            var state = new SavedState();
            foreach (var instance in engine.Objects.Where(item => item.Plc.Equals(plc, StringComparison.OrdinalIgnoreCase)))
            {
                var values = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var field in Fields(instance.GetType()))
                {
                    switch (field.GetValue(instance))
                    {
                        case double number when double.IsFinite(number):
                            values[field.Name] = number;
                            break;
                        case bool flag:
                            values[field.Name] = flag ? 1 : 0;
                            break;
                        case int count:
                            values[field.Name] = count;
                            break;
                        case double[] numbers:
                            for (var i = 0; i < numbers.Length; i++)
                            {
                                if (double.IsFinite(numbers[i]))
                                {
                                    values[$"{field.Name}[{i}]"] = numbers[i];
                                }
                            }

                            break;
                        case bool[] flags:
                            for (var i = 0; i < flags.Length; i++)
                            {
                                values[$"{field.Name}[{i}]"] = flags[i] ? 1 : 0;
                            }

                            break;
                    }
                }

                foreach (var (port, value) in instance.Forces)
                {
                    values[ForcePrefix + port] = value;
                }

                if (values.Count > 0)
                {
                    state.Objects[instance.Name] = values;
                }
            }

            state.Relays = engine.Relays.CaptureState(plc);
            for (var c = 0; c < model.Cubicles.Count; c++)
            {
                for (var i = 0; i < model.Cubicles[c].Items.Count; i++)
                {
                    if (model.Cubicles[c].Items[i] is { IsEmergencyStop: true, Pressed: true })
                    {
                        state.Latched.Add($"{c}/{i}");
                    }
                }
            }

            model.State = state.Objects.Count > 0 || state.Relays.Count > 0 || state.Latched.Count > 0 ? state : null;
        }
    }

    /// <summary>Continues from the saved state of the models (after loading).</summary>
    public static void Restore(ProcessEngine engine)
    {
        foreach (var (plc, model) in engine.Models)
        {
            if (model.State is not { } state)
            {
                continue;
            }

            foreach (var instance in engine.Objects.Where(item => item.Plc.Equals(plc, StringComparison.OrdinalIgnoreCase)))
            {
                if (!state.Objects.TryGetValue(instance.Name, out var values))
                {
                    continue;
                }

                foreach (var field in Fields(instance.GetType()))
                {
                    try
                    {
                        if (field.FieldType == typeof(double) && values.TryGetValue(field.Name, out var number))
                        {
                            field.SetValue(instance, number);
                        }
                        else if (field.FieldType == typeof(bool) && values.TryGetValue(field.Name, out var flag))
                        {
                            field.SetValue(instance, flag >= 0.5);
                        }
                        else if (field.FieldType == typeof(int) && values.TryGetValue(field.Name, out var count))
                        {
                            field.SetValue(instance, (int)Math.Round(count));
                        }
                        else if (field.GetValue(instance) is double[] numbers)
                        {
                            for (var i = 0; i < numbers.Length; i++)
                            {
                                if (values.TryGetValue($"{field.Name}[{i}]", out var item))
                                {
                                    numbers[i] = item;
                                }
                            }
                        }
                        else if (field.GetValue(instance) is bool[] flags)
                        {
                            for (var i = 0; i < flags.Length; i++)
                            {
                                if (values.TryGetValue($"{field.Name}[{i}]", out var item))
                                {
                                    flags[i] = item >= 0.5;
                                }
                            }
                        }
                    }
                    catch (ArgumentException)
                    {
                        // A field whose type changed since the state was saved: it keeps its reset value.
                    }
                }

                instance.Forces.Clear();
                foreach (var (key, value) in values.Where(pair => pair.Key.StartsWith(ForcePrefix, StringComparison.Ordinal)))
                {
                    instance.Forces[key[ForcePrefix.Length..]] = value;
                }
            }

            engine.Relays.RestoreState(plc, state.Relays);
            foreach (var entry in state.Latched)
            {
                var parts = entry.Split('/');
                if (parts.Length == 2 && int.TryParse(parts[0], out var c) && int.TryParse(parts[1], out var i)
                    && c >= 0 && c < model.Cubicles.Count && i >= 0 && i < model.Cubicles[c].Items.Count && model.Cubicles[c].Items[i].IsEmergencyStop)
                {
                    model.Cubicles[c].Items[i].Pressed = true;
                }
            }
        }
    }

    /// <summary>The state fields of an object type and its base types: numbers, flags, counters and their arrays.</summary>
    private static List<FieldInfo> Fields(Type type)
    {
        var fields = new List<FieldInfo>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var scalar = field.FieldType == typeof(double) || field.FieldType == typeof(bool) || field.FieldType == typeof(int);
                var array = field.FieldType == typeof(double[]) || field.FieldType == typeof(bool[]);
                if ((scalar && !field.IsInitOnly) || array)
                {
                    if (names.Add(field.Name))
                    {
                        fields.Add(field);
                    }
                }
            }
        }

        return fields;
    }

    internal static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
