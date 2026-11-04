namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// Where the simulator uses each signal of a PLC: object ports ("@Gate1.Open"), relay logic (relay input, PLC input
/// object, contacts, devices) and cubicle devices — shown next to the signal in the signal list.
/// </summary>
public static class SignalLinks
{
    public static Dictionary<(SimulatorSignalKind Kind, int Id), List<string>> Of(ProcessModel model)
    {
        var links = new Dictionary<(SimulatorSignalKind, int), List<string>>();
        void Add(string binding, string text)
        {
            foreach (var target in PortBinding.ParseList(binding).Where(target => target.Kind == BindingKind.Signal))
            {
                var key = (target.SignalKind, target.SignalId);
                if (!links.TryGetValue(key, out var list))
                {
                    links[key] = list = [];
                }

                if (!list.Contains(text, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(text);
                }
            }
        }

        foreach (var item in model.Objects)
        {
            foreach (var (port, binding) in item.Ports)
            {
                Add(binding, $"@{item.Name}.{port}");
            }
        }

        for (var sheetIndex = 0; sheetIndex < model.RelaySheets.Count; sheetIndex++)
        {
            var sheet = model.RelaySheets[sheetIndex];
            for (var path = 0; path < sheet.Circuits.Count; path++)
            {
                var circuit = sheet.Circuits[path];
                var location = RelayLogicRunner.Location(sheet, sheetIndex, path);
                var coil = circuit.Coil.Tag.Trim();
                Add(circuit.Coil.Input, $"relay {coil} {location}");
                Add(circuit.Coil.Outputs, coil.Length > 0 ? $"relay {coil} {location}" : $"PLC input {location}");
                foreach (var contact in RelayLogicRunner.AllElements(circuit.Elements).Where(element => !element.IsParallel))
                {
                    Add(contact.Tag, $"contact {location}");
                }
            }
        }

        foreach (var device in model.RelayDevices)
        {
            Add(device.Input, $"device {device.Tag.Trim()}");
        }

        foreach (var item in model.Cubicles.SelectMany(cubicle => cubicle.Items))
        {
            if (item.IsSwitch)
            {
                foreach (var (position, binding) in item.OutputsByPosition())
                {
                    Add(binding, $"cubicle {item.Tag.Trim()} {position}");
                }
            }
            else
            {
                Add(item.Outputs, $"cubicle {item.Tag.Trim()}");
            }

            Add(item.Source, $"lamp {item.Tag.Trim()}");
        }

        return links;
    }

    /// <summary>Sets <see cref="SimulatorSignal.Links"/> of every signal of the session.</summary>
    public static void Apply(IEnumerable<SimulatorSignal> signals, ProcessModel? model)
    {
        var links = model is null ? [] : Of(model);
        foreach (var signal in signals)
        {
            signal.Links = links.TryGetValue((signal.Kind, signal.Id), out var list) ? string.Join(", ", list) : "";
        }
    }
}
