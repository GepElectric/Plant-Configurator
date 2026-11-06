namespace CwcConfigurator.Services.Simulation.Process;

/// <summary>
/// A PLC input drawn in the relay logic (a path ending in a PLC input object that drives DI:n) is the only place
/// that writes that DI. Its contacts are the signal coming into the cubicle: an imported contact without a source
/// ("SUPPLY ? (set the source)") is an external signal still to be connected. When something else is mapped to the
/// same DI — an object output, a cubicle push button or switch — it becomes the source of that contact (the DI keeps
/// following the relay logic); when the contact already has a source, the second mapping is removed.
/// </summary>
public static class PlcInputSources
{
    /// <returns>What was changed, one line per mapping (empty = nothing to do).</returns>
    public static List<string> Assign(ProcessModel model)
    {
        var messages = new List<string>();
        var inputs = PlcInputs(model);
        if (inputs.Count == 0)
        {
            return messages;
        }

        foreach (var item in model.Objects)
        {
            var info = ObjectCatalog.Find(item.Type);
            if (info is null)
            {
                continue;
            }

            foreach (var port in info.Ports.Where(port => port.Direction == PortDirection.Output && info.IsActive(port.ShownWhen, item)))
            {
                if (!item.Ports.TryGetValue(port.Name, out var text))
                {
                    continue;
                }

                var kept = Route(PortBinding.ParseList(text), $"@{item.Name}.{port.Name}", inputs, messages, contact => contact.Tag = $"@{item.Name}.{port.Name}",
                    $"@{item.Name}.{port.Name}");
                if (kept is not null)
                {
                    if (kept.Length == 0)
                    {
                        item.Ports.Remove(port.Name);
                    }
                    else
                    {
                        item.Ports[port.Name] = kept;
                    }
                }
            }
        }

        foreach (var device in model.Cubicles.SelectMany(cubicle => cubicle.Items).Where(device => device.Tag.Trim().Length > 0))
        {
            if (device.IsButton)
            {
                var kept = Route(PortBinding.ParseList(device.Outputs), $"cubicle {device.Tag.Trim()}", inputs, messages, contact =>
                {
                    // The push button's own contact: NO closes while pressed, NC opens (as on the cubicle).
                    contact.Tag = device.Tag.Trim();
                    contact.Contact = device.Contact.Equals("NC", StringComparison.OrdinalIgnoreCase) ? "NC" : "NO";
                }, device.Tag.Trim());
                if (kept is not null)
                {
                    device.Outputs = kept;
                }
            }
            else if (device.IsSwitch)
            {
                var outputs = device.OutputsByPosition();
                var changed = false;
                foreach (var (position, binding) in outputs.ToList())
                {
                    var kept = Route(PortBinding.ParseList(binding), $"cubicle {device.Tag.Trim()} {position}", inputs, messages, contact =>
                    {
                        contact.Tag = device.Tag.Trim();
                        contact.Position = position;
                    }, null);
                    if (kept is not null)
                    {
                        outputs[position] = kept;
                        changed = true;
                    }
                }

                if (changed)
                {
                    device.SetOutputsByPosition(outputs);
                }
            }
        }

        return messages;
    }

    /// <summary>
    /// The targets of one source that are PLC inputs of the relay logic are moved there. Returns the remaining
    /// targets as text, or null when nothing changed.
    /// </summary>
    private static string? Route(IReadOnlyList<PortBinding> targets, string source, Dictionary<(SimulatorSignalKind, int), PlcInput> inputs, List<string> messages,
        Action<RelayElement> connect, string? deviceInput)
    {
        var kept = new List<PortBinding>();
        var changed = false;
        foreach (var target in targets)
        {
            if (target.Kind != BindingKind.Signal || !inputs.TryGetValue((target.SignalKind, target.SignalId), out var input))
            {
                kept.Add(target);
                continue;
            }

            changed = true;
            if (input.Open is { } contact)
            {
                connect(contact);
                contact.Text = "";
                input.Open = null; // it has a source now
                input.Device = null;
                messages.Add($"{target}: {source} is now the source of the PLC input {input.Location}{input.Via} (external signal into the cubicle).");
            }
            else if (input.Device is { } device && deviceInput is not null)
            {
                device.Input = deviceInput;
                input.Device = null;
                messages.Add($"{target}: {source} is now the input of the device {device.Tag.Trim()} of the PLC input {input.Location}{input.Via}.");
            }
            else
            {
                messages.Add($"{target}: not connected to {source} — the PLC input {input.Location} drives it and its contact already has a source; change it there.");
            }
        }

        return changed ? string.Join(", ", kept.Select(binding => binding.ToString())) : null;
    }

    private sealed class PlcInput
    {
        public required string Location { get; init; }

        /// <summary>The contact still without a source (the path has no other contact), or null.</summary>
        public RelayElement? Open { get; set; }

        /// <summary>The device without an input the path depends on, or null.</summary>
        public RelayDevice? Device { get; set; }

        /// <summary>" through relay 1-K14 /27.8" when the open contact is on another path.</summary>
        public string Via { get; set; } = "";
    }

    /// <summary>The DI driven by a PLC input object of the relay logic, with its open contact.</summary>
    private static Dictionary<(SimulatorSignalKind, int), PlcInput> PlcInputs(ProcessModel model)
    {
        var inputs = new Dictionary<(SimulatorSignalKind, int), PlcInput>();
        var noSource = RelayLogicRunner.Unsourced(model);
        var places = new Dictionary<RelayCircuit, string>(ReferenceEqualityComparer.Instance);
        for (var sheetIndex = 0; sheetIndex < model.RelaySheets.Count; sheetIndex++)
        {
            for (var path = 0; path < model.RelaySheets[sheetIndex].Circuits.Count; path++)
            {
                places[model.RelaySheets[sheetIndex].Circuits[path]] = RelayLogicRunner.Location(model.RelaySheets[sheetIndex], sheetIndex, path);
            }
        }

        // From a path without a source back to where the external signal comes in: its only contact when it has no
        // tag; through a relay (its only path) or a device without an input.
        (RelayElement? Contact, RelayDevice? Device, string Via) Entry(RelayCircuit circuit)
        {
            var via = "";
            for (var depth = 0; depth < 8; depth++)
            {
                var contacts = RelayLogicRunner.AllElements(circuit.Elements).Where(element => !element.IsParallel && !element.IsWire).ToList();
                if (contacts.Count != 1)
                {
                    return (null, null, via);
                }

                var tag = contacts[0].Tag.Trim();
                if (tag.Length == 0)
                {
                    return (contacts[0], null, via);
                }

                var coils = model.RelaySheets.SelectMany(sheet => sheet.Circuits).Where(item => item.Coil.Tag.Trim().Equals(tag, StringComparison.OrdinalIgnoreCase)).ToList();
                if (coils.Count == 1 && noSource.Contains(coils[0]))
                {
                    circuit = coils[0];
                    via += $" through relay {tag} {places.GetValueOrDefault(circuit, "")}";
                    continue;
                }

                var device = coils.Count == 0 ? model.RelayDevices.FirstOrDefault(item => item.Tag.Trim().Equals(tag, StringComparison.OrdinalIgnoreCase) && item.Input.Trim().Length == 0) : null;
                return (null, device, via);
            }

            return (null, null, via);
        }

        for (var sheetIndex = 0; sheetIndex < model.RelaySheets.Count; sheetIndex++)
        {
            var sheet = model.RelaySheets[sheetIndex];
            for (var path = 0; path < sheet.Circuits.Count; path++)
            {
                var circuit = sheet.Circuits[path];
                if (circuit.Coil.HasInput)
                {
                    continue;
                }

                var entry = noSource.Contains(circuit) ? Entry(circuit) : (null, null, "");
                var location = RelayLogicRunner.Location(sheet, sheetIndex, path) + (sheet.Name.Trim().Length > 0 ? $" ({sheet.Name.Trim()})" : "");
                foreach (var target in PortBinding.ParseList(circuit.Coil.Outputs).Where(target => target.Kind == BindingKind.Signal))
                {
                    inputs.TryAdd((target.SignalKind, target.SignalId), new PlcInput { Location = location, Open = entry.Contact, Device = entry.Device, Via = entry.Via });
                }
            }
        }

        return inputs;
    }
}
