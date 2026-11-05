namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// One place where the simulator uses a signal. <see cref="Writes"/>: the place drives the signal (an object output,
/// a PLC input object of the relay logic, a cubicle button / switch); otherwise it only reads it.
/// </summary>
/// <param name="Area">"Object" (<see cref="Name"/> = object), "Relay" (<see cref="Index"/> = sheet, <see cref="Item"/> =
/// coil or contact), "Device" (<see cref="Name"/> = device tag) or "Cubicle" (<see cref="Index"/> = cubicle, <see cref="Item"/> = device).</param>
public sealed record SignalLink(string Text, bool Writes, string Area, string Name = "", int Index = -1, object? Item = null);

/// <summary>
/// Where the simulator uses each signal of a PLC: object ports ("@Gate1.Open"), relay logic (relay input, PLC input
/// object, contacts, devices) and cubicle devices — shown next to the signal in the signal list.
/// </summary>
public static class SignalLinks
{
    public static Dictionary<(SimulatorSignalKind Kind, int Id), List<string>> Of(ProcessModel model) =>
        Detailed(model).ToDictionary(pair => pair.Key, pair => pair.Value.Select(link => link.Text).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

    public static Dictionary<(SimulatorSignalKind Kind, int Id), List<SignalLink>> Detailed(ProcessModel model)
    {
        var links = new Dictionary<(SimulatorSignalKind, int), List<SignalLink>>();
        void Add(string binding, SignalLink link)
        {
            foreach (var target in PortBinding.ParseList(binding).Where(target => target.Kind == BindingKind.Signal))
            {
                var key = (target.SignalKind, target.SignalId);
                if (!links.TryGetValue(key, out var list))
                {
                    links[key] = list = [];
                }

                if (!list.Any(existing => existing.Text.Equals(link.Text, StringComparison.OrdinalIgnoreCase) && existing.Writes == link.Writes))
                {
                    list.Add(link);
                }
            }
        }

        foreach (var item in model.Objects)
        {
            var info = ObjectCatalog.Find(item.Type);
            foreach (var (port, binding) in item.Ports)
            {
                // Only outputs the chosen variant has drive their signals (the engine ignores the others).
                var def = info?.Port(port);
                var writes = def is not null && def.Direction == PortDirection.Output && info!.IsActive(def.ShownWhen, item);
                Add(binding, new SignalLink($"@{item.Name}.{port}", writes, "Object", item.Name));
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
                var where = sheet.Name.Trim().Length > 0 ? $" ({sheet.Name.Trim()})" : "";
                Add(circuit.Coil.Input, new SignalLink($"relay {coil} {location}", false, "Relay", Index: sheetIndex, Item: circuit.Coil));
                // A PLC input without a source does not write its DI (an external signal still to be connected).
                var unsourced = RelayLogicRunner.IsUnsourced(circuit);
                Add(circuit.Coil.Outputs, new SignalLink((coil.Length > 0 ? $"relay {coil} {location}" : $"PLC input {location}") + where + (unsourced ? " — no source" : ""),
                    !unsourced, "Relay", Index: sheetIndex, Item: circuit.Coil));
                foreach (var contact in RelayLogicRunner.AllElements(circuit.Elements).Where(element => !element.IsParallel))
                {
                    Add(contact.Tag, new SignalLink($"contact {location}", false, "Relay", Index: sheetIndex, Item: contact));
                }
            }
        }

        foreach (var device in model.RelayDevices)
        {
            Add(device.Input, new SignalLink($"device {device.Tag.Trim()}", false, "Device", device.Tag.Trim()));
        }

        for (var cubicleIndex = 0; cubicleIndex < model.Cubicles.Count; cubicleIndex++)
        {
            foreach (var item in model.Cubicles[cubicleIndex].Items)
            {
                if (item.IsSwitch)
                {
                    foreach (var (position, binding) in item.OutputsByPosition())
                    {
                        Add(binding, new SignalLink($"cubicle {item.Tag.Trim()} {position}", true, "Cubicle", Index: cubicleIndex, Item: item));
                    }
                }
                else
                {
                    Add(item.Outputs, new SignalLink($"cubicle {item.Tag.Trim()}", item.IsButton, "Cubicle", Index: cubicleIndex, Item: item));
                }

                Add(item.Source, new SignalLink($"lamp {item.Tag.Trim()}", false, "Cubicle", Index: cubicleIndex, Item: item));
            }
        }

        return links;
    }

    /// <summary>
    /// The places that write the same signal, when more than one does (they overwrite each other every step: the
    /// relay logic runs after the cubicle, the cubicle after the objects, so the last of them wins).
    /// </summary>
    public static List<SignalLink> Conflicting(IReadOnlyList<SignalLink> links)
    {
        var writers = links.Where(link => link.Writes).ToList();
        if (writers.Count <= 1)
        {
            return [];
        }

        // A switch writes each of its positions to another signal; the same switch twice is not a conflict.
        return writers.Select(link => link.Item ?? link.Name).Distinct().Count() > 1 ? writers : [];
    }

    /// <summary>Sets <see cref="SimulatorSignal.Links"/> (and the conflict warning) of every signal of the session.</summary>
    /// <returns>Number of signals written by more than one place.</returns>
    public static int Apply(IEnumerable<SimulatorSignal> signals, ProcessModel? model)
    {
        var links = model is null ? [] : Detailed(model);
        var conflicts = 0;
        foreach (var signal in signals)
        {
            var list = links.TryGetValue((signal.Kind, signal.Id), out var found) ? found : [];
            var writers = Conflicting(list);
            var text = string.Join(", ", list.Select(link => link.Text).Distinct(StringComparer.OrdinalIgnoreCase));
            signal.LinkTargets = list;
            signal.LinkWarning = writers.Count > 0
                ? $"{writers.Count} places write this input and overwrite each other every step — only the last one counts " +
                  $"(relay logic after cubicle after objects): {string.Join(", ", writers.Select(link => link.Text))}"
                : "";
            signal.Links = writers.Count > 0 ? "⚠ " + text : text;
            if (writers.Count > 0)
            {
                conflicts++;
            }
        }

        return conflicts;
    }
}
