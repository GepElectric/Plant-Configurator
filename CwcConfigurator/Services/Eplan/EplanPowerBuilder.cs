using System.Globalization;
using CwcConfigurator.Services.Simulation.Process;

namespace CwcConfigurator.Services.Eplan;

public enum PowerDeviceKind
{
    /// <summary>Main switch / circuit breaker / MCB / RCBO (EPLAN categories 501, 504, 506).</summary>
    Breaker,

    /// <summary>Motor protection switch (category 502).</summary>
    MotorProtection,

    /// <summary>Main poles of a contactor; its coil is a relay of the relay logic.</summary>
    Contactor,

    Motor
}

/// <summary>
/// A device of the power circuits and what feeds it: another device's tag, or "" for the busbar. An incomer (main
/// switch) is fed from outside and supplies the busbar.
/// </summary>
/// <param name="Source">Supply of a device fed straight from a busbar: "" = the main (L1) busbar, otherwise the
/// potential ("L+110VDC", "L2230VAC") or "?" when no supply reaches it.</param>
public sealed record PowerDevice(string Tag, PowerDeviceKind Kind, string Text, string Upstream, string Page = "", bool Incomer = false, string Key = "", string Source = "")
{
    /// <summary>Identity of the device (its EPLAN function + location + tag when known; two -Q1 can be different devices).</summary>
    public string Id => Key.Length > 0 ? Key : Tag.Trim();
}

/// <summary>
/// Simulator objects for the power part of an EPLAN project: a busbar, breakers / motor protection switches (Mcb),
/// contactors (Contactor, commanded by the relay coil with the same tag — "@Relays.-K53") and motors (Motor), each
/// fed by the Live output of the device above it. Placed on an SLD screen: the busbar on top, one column per feeder.
/// </summary>
public static class EplanPowerBuilder
{
    private const int FeedersPerBusbar = 12;
    private const double ColumnWidth = 130;
    private const double RowHeight = 120;
    private const double Left = 80;
    private const double BusbarY = 170;
    private const double IncomerY = 60;

    public static List<ObjectConfig> Build(IReadOnlyList<PowerDevice> devices, string screen, ISet<string> relayCoils, IReadOnlySet<string> existingNames) =>
        Build(devices, screen, relayCoils, existingNames, out _);

    /// <param name="objectNames">Device id (<see cref="PowerDevice.Id"/>) → name of its simulator object.</param>
    public static List<ObjectConfig> Build(IReadOnlyList<PowerDevice> devices, string screen, ISet<string> relayCoils, IReadOnlySet<string> existingNames,
        out Dictionary<string, string> objectNames, IReadOnlyList<PowerUnit>? units = null)
    {
        var objects = new List<ObjectConfig>();
        var names = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        string Unique(string name)
        {
            var candidate = name;
            for (var i = 2; !names.Add(candidate); i++)
            {
                candidate = $"{name}_{i}";
            }

            return candidate;
        }

        var byTag = new Dictionary<string, ObjectConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            var config = new ObjectConfig
            {
                Name = Unique(ObjectName(device.Tag)),
                Type = device.Kind switch
                {
                    PowerDeviceKind.Contactor => "Contactor",
                    PowerDeviceKind.Motor => "Motor",
                    _ => "Mcb"
                },
                Description = device.Text.Trim(),
                Screen = screen
            };
            if (device.Kind is PowerDeviceKind.Breaker or PowerDeviceKind.MotorProtection)
            {
                config.Options["Position"] = "On";
            }

            // Rated current from the EPLAN text, e.g. "Pn=11 kW In=22,5A".
            if (device.Kind == PowerDeviceKind.Motor
                && System.Text.RegularExpressions.Regex.Match(device.Text, @"In\s*=\s*([0-9]+(?:[.,][0-9]+)?)") is { Success: true } current
                && double.TryParse(current.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var amps))
            {
                config.Params["RatedCurrent"] = amps;
            }

            if (device.Kind == PowerDeviceKind.Contactor && relayCoils.Contains(device.Tag.Trim()))
            {
                config.Ports["Command"] = $"@{ProcessEngine.RelaysLink}.{device.Tag.Trim()}";
            }

            byTag.TryAdd(device.Id, config);
            objects.Add(config);
        }

        // Feeders of the busbars: devices fed from them. Every EPLAN page is a level of its own (a busbar row with its
        // feeders below), the supplies one after the other — L1 first, then L+110VDC, 24 VDC…, last what has no supply.
        var incomers = devices.Where(device => device.Incomer).ToList();
        var feeders = devices.Where(device => !device.Incomer && (device.Upstream.Length == 0 || !byTag.ContainsKey(device.Upstream))).ToList();
        // 11 feeders per busbar; its 12th feeder supplies the next bar when there are more.
        const int perBusbar = FeedersPerBusbar - 1;
        var busbars = new List<ObjectConfig>();
        static int SupplyOrder(string supply) => supply.Length == 0 ? 0 : supply == "?" ? 2 : 1;
        var levels = feeders.GroupBy(device => (device.Source, device.Page))
            .OrderBy(group => SupplyOrder(group.Key.Source)).ThenBy(group => group.Key.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => int.TryParse(group.Key.Page, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : int.MaxValue)
            .ThenBy(group => group.Key.Page, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Supply: group.Key.Source, Page: group.Key.Page, Members: group.ToList()))
            .ToList();
        if (levels.Count == 0)
        {
            levels.Add(("", "", []));
        }

        // Power supply units (rectifiers, DC/DC converters): fed by their breaker, supplying the busbar of their output.
        var unitByOutput = new Dictionary<string, ObjectConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in units ?? [])
        {
            var config = new ObjectConfig { Name = Unique(ObjectName(unit.Tag)), Type = "PowerSupply", Description = unit.Text.Trim(), Screen = screen };
            var input = devices.FirstOrDefault(device => device.Id.Equals(unit.InputKey, StringComparison.OrdinalIgnoreCase));
            if (input is not null)
            {
                config.Ports["Input"] = $"@{byTag[input.Id].Name}.Live";
                config.Params["InputVoltage"] = Volts(RootSource(input)) ?? 230;
            }

            config.Params["OutputVoltage"] = Volts(unit.Output) ?? 24;
            if (unit.Output.Length > 0)
            {
                unitByOutput.TryAdd(unit.Output, config);
            }

            objects.Add(config);
            byTag.TryAdd(unit.Tag.Trim(), config);
        }

        string RootSource(PowerDevice device)
        {
            for (var guard = 0; device.Upstream.Length > 0 && guard < 50; guard++)
            {
                var upstream = devices.FirstOrDefault(item => item.Id.Equals(device.Upstream, StringComparison.OrdinalIgnoreCase));
                if (upstream is null)
                {
                    break;
                }

                device = upstream;
            }

            return device.Source;
        }

        var nextColumn = Left;
        var deepest = 0;
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Place(PowerDevice device, int row, double busY)
        {
            if (!placed.Add(device.Id))
            {
                return;
            }

            var config = byTag[device.Id];
            config.Y = busY + 70 + row * RowHeight;
            deepest = Math.Max(deepest, row);
            var children = devices.Where(child => child.Upstream.Equals(device.Id, StringComparison.OrdinalIgnoreCase) && !placed.Contains(child.Id)).ToList();
            config.X = nextColumn;
            if (children.Count == 0)
            {
                nextColumn += ColumnWidth;
                return;
            }

            foreach (var child in children)
            {
                Place(child, row + 1, busY);
            }
        }

        var lastBar = new Dictionary<string, ObjectConfig>(StringComparer.OrdinalIgnoreCase);
        var top = 0.0;
        foreach (var (supply, page, members) in levels)
        {
            var busY = BusbarY + top;
            var label = supply.Length == 0 ? "" : supply == "?" ? "NoSupply" : ObjectName(supply);
            var levelBars = new List<ObjectConfig>();
            var count = Math.Max(1, (members.Count + perBusbar - 1) / perBusbar);
            for (var b = 0; b < count; b++)
            {
                var busbar = new ObjectConfig
                {
                    Name = Unique("Busbar" + label + (page.Length > 0 ? "_P" + ObjectName(page) : "") + (b > 0 ? $"_{b + 1}" : "")),
                    Type = "Busbar",
                    Description = (supply.Length == 0 ? "Busbar L1/L2/L3" : supply == "?" ? "Devices without a readable supply" : $"Busbar {supply}")
                                  + (page.Length > 0 ? $" — EPLAN page {page}" : ""),
                    Screen = screen,
                    Y = busY
                };
                busbar.Options["Feeders"] = FeedersPerBusbar.ToString(CultureInfo.InvariantCulture);
                if (b > 0)
                {
                    busbar.Ports["Supply1"] = $"@{levelBars[^1].Name}.Feeder{FeedersPerBusbar}";
                }
                else if (lastBar.TryGetValue(supply, out var previous))
                {
                    // The same supply on the next page: fed from the bar of the page before.
                    busbar.Ports["Supply1"] = $"@{previous.Name}.Feeder{FeedersPerBusbar}";
                }
                else if (supply.Length == 0 && incomers.Count > 0)
                {
                    // The main switch supplies the busbar; drawn above it.
                    busbar.Ports["Supply1"] = $"@{byTag[incomers[0].Id].Name}.Live";
                    byTag[incomers[0].Id].X = Left;
                    byTag[incomers[0].Id].Y = busY - (BusbarY - IncomerY);
                }
                else if (supply.Length > 0 && unitByOutput.TryGetValue(supply, out var unit))
                {
                    // Supplied by its power supply unit (e.g. -U101 → 24 VDC), drawn above the bar.
                    busbar.Ports["Supply1"] = $"@{unit.Name}.Output";
                    unit.X = Left;
                    unit.Y = busY - 115;
                }
                else if (supply.Length > 0 && unitByOutput.Values.FirstOrDefault(other => other.Params.TryGetValue("OutputVoltage", out var volts) && volts == Volts(supply)) is { } sameVoltage)
                {
                    // Another potential of the same voltage (e.g. 30-L+24VDC): from the unit that makes that voltage.
                    busbar.Ports["Supply1"] = $"@{sameVoltage.Name}.Output";
                }
                else if (supply.Length > 0)
                {
                    // Its source (battery, power supply on a page not imported) is not simulated: always at its voltage.
                    busbar.Ports["Supply1"] = "=" + (Volts(supply) ?? 230).ToString(CultureInfo.InvariantCulture);
                }

                levelBars.Add(busbar);
            }

            for (var i = 0; i < members.Count; i++)
            {
                byTag[members[i].Id].Ports["Supply"] = $"@{levelBars[i / perBusbar].Name}.Feeder{i % perBusbar + 1}";
            }

            // Layout of the level: each busbar spans the columns of its feeders.
            nextColumn = Left;
            deepest = 0;
            var feederColumns = new List<double>();
            foreach (var feeder in members)
            {
                feederColumns.Add(nextColumn);
                Place(feeder, 0, busY);
            }

            for (var b = 0; b < levelBars.Count; b++)
            {
                var columns = feederColumns.Skip(b * perBusbar).Take(perBusbar).ToList();
                var first = columns.Count > 0 ? columns[0] : nextColumn;
                var end = b + 1 < levelBars.Count ? feederColumns[(b + 1) * perBusbar] : nextColumn;
                levelBars[b].X = first - 40;
                levelBars[b].Params["Length"] = Math.Max(300, end - first + 20);
            }

            busbars.AddRange(levelBars);
            lastBar[supply] = levelBars[^1];
            top += 70 + (deepest + 1) * RowHeight + 190;
        }

        foreach (var device in devices.Where(device => device.Upstream.Length > 0 && byTag.ContainsKey(device.Upstream)))
        {
            byTag[device.Id].Ports["Supply"] = $"@{byTag[device.Upstream].Name}.Live";
        }

        objects.InsertRange(0, busbars);
        objectNames = byTag.ToDictionary(pair => pair.Key, pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase);
        return objects;
    }

    /// <summary>Screens made by the power import: "Power (EPLAN)", "Power (EPLAN) 2"…</summary>
    public static bool IsImportScreen(string screen) => screen.StartsWith("Power (EPLAN)", StringComparison.OrdinalIgnoreCase);

    public const string OverviewSheet = "Power distribution (EPLAN)";

    /// <summary>
    /// Removes what an earlier power import made: its SLD screens with their objects, the overview sheet, and the
    /// relay-logic devices that followed those objects.
    /// </summary>
    /// <returns>The positions (On / Off / Tripped) of the removed breakers by object name, to keep them on the new import.</returns>
    public static Dictionary<string, string> RemovePrevious(ProcessModel model)
    {
        var screens = model.AllScreens().Where(IsImportScreen).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var objects = model.Objects.Where(item => item.Screen is { } screen && screens.Contains(screen)).ToList();
        var positions = objects.Where(item => item.Options.TryGetValue("Position", out _))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Options["Position"], StringComparer.OrdinalIgnoreCase);
        var names = objects.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        model.Objects.RemoveAll(objects.Contains);
        model.Screens.RemoveAll(screens.Contains);
        model.SldScreens.RemoveAll(screens.Contains);
        model.RelaySheets.RemoveAll(sheet => sheet.Name.Equals(OverviewSheet, StringComparison.OrdinalIgnoreCase));
        model.RelayDevices.RemoveAll(device => device.Input.Trim().StartsWith('@')
                                              && names.Contains(device.Input.Trim()[1..].Split('.')[0]));
        return positions;
    }

    /// <summary>Voltage in a potential name: "24-VDC L+" → 24, "11-L+110VDC" → 110, "L2230VAC" → 230; null when none.</summary>
    public static double? Volts(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name ?? "", @"(\d{2,3})\s*-?\s*V");
        return match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Object name of a device tag: "-K53" → "K53", "2-F3" → "2F3".</summary>
    public static string ObjectName(string tag) => new string((tag ?? "").Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } name ? name : "Device";
}
