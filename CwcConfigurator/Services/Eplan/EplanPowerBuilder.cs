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
        out Dictionary<string, string> objectNames)
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
                Description = $"{device.Tag} {device.Text}".Trim() + (device.Page.Length > 0 ? $" (EPLAN page {device.Page})" : ""),
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

        // Feeders of the busbars: devices fed from them, left to right in the drawing order; one busbar (or a row of
        // busbars) per supply — the main L1 busbar first, then L2230VAC, L+110VDC…
        var incomers = devices.Where(device => device.Incomer).ToList();
        var feeders = devices.Where(device => !device.Incomer && (device.Upstream.Length == 0 || !byTag.ContainsKey(device.Upstream))).ToList();
        // 11 feeders per busbar; its 12th feeder supplies the next bar when there are more.
        const int perBusbar = FeedersPerBusbar - 1;
        var busbars = new List<ObjectConfig>();
        var groups = feeders.GroupBy(device => device.Source).OrderBy(group => group.Key == "" ? 0 : group.Key == "?" ? 2 : 1)
            .Select(group => (Key: group.Key, Members: group.ToList())).ToList();
        if (groups.Count == 0)
        {
            groups.Add(("", []));
        }

        var nextColumn = Left;
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Place(PowerDevice device, int row)
        {
            if (!placed.Add(device.Id))
            {
                return;
            }

            var config = byTag[device.Id];
            config.Y = BusbarY + 70 + row * RowHeight;
            var children = devices.Where(child => child.Upstream.Equals(device.Id, StringComparison.OrdinalIgnoreCase) && !placed.Contains(child.Id)).ToList();
            config.X = nextColumn;
            if (children.Count == 0)
            {
                nextColumn += ColumnWidth;
                return;
            }

            foreach (var child in children)
            {
                Place(child, row + 1);
            }
        }

        foreach (var (supply, members) in groups)
        {
            var groupBars = new List<ObjectConfig>();
            var count = Math.Max(1, (members.Count + perBusbar - 1) / perBusbar);
            for (var b = 0; b < count; b++)
            {
                var label = supply.Length == 0 ? "" : supply == "?" ? "NoSupply" : ObjectName(supply);
                var busbar = new ObjectConfig
                {
                    Name = Unique(b == 0 ? "Busbar" + label : $"Busbar{label}_{b + 1}"),
                    Type = "Busbar",
                    Description = supply.Length == 0 ? "Busbar L1/L2/L3 (EPLAN)" : supply == "?" ? "Devices without a readable supply (EPLAN)" : $"Busbar {supply} (EPLAN)",
                    Screen = screen,
                    Y = BusbarY
                };
                busbar.Options["Feeders"] = FeedersPerBusbar.ToString(CultureInfo.InvariantCulture);
                if (b > 0)
                {
                    busbar.Ports["Supply1"] = $"@{groupBars[^1].Name}.Feeder{FeedersPerBusbar}";
                }
                else if (supply.Length == 0 && incomers.Count > 0)
                {
                    // The main switch supplies the busbar; drawn above it.
                    busbar.Ports["Supply1"] = $"@{byTag[incomers[0].Id].Name}.Live";
                    byTag[incomers[0].Id].X = Left;
                    byTag[incomers[0].Id].Y = IncomerY;
                }
                else if (supply.Length > 0)
                {
                    busbar.Ports["Supply1"] = "=1"; // its own supply (power supply unit, transformer, battery) is not simulated
                }

                groupBars.Add(busbar);
            }

            for (var i = 0; i < members.Count; i++)
            {
                byTag[members[i].Id].Ports["Supply"] = $"@{groupBars[i / perBusbar].Name}.Feeder{i % perBusbar + 1}";
            }

            // Layout: each busbar spans the columns of its feeders; a gap between the supplies.
            var feederColumns = new List<double>();
            foreach (var feeder in members)
            {
                feederColumns.Add(nextColumn);
                Place(feeder, 0);
            }

            for (var b = 0; b < groupBars.Count; b++)
            {
                var columns = feederColumns.Skip(b * perBusbar).Take(perBusbar).ToList();
                var first = columns.Count > 0 ? columns[0] : nextColumn;
                var end = b + 1 < groupBars.Count ? feederColumns[(b + 1) * perBusbar] : nextColumn;
                groupBars[b].X = first - 40;
                groupBars[b].Params["Length"] = Math.Max(300, end - first + 20);
            }

            busbars.AddRange(groupBars);
            nextColumn += ColumnWidth / 2;
        }

        foreach (var device in devices.Where(device => device.Upstream.Length > 0 && byTag.ContainsKey(device.Upstream)))
        {
            byTag[device.Id].Ports["Supply"] = $"@{byTag[device.Upstream].Name}.Live";
        }

        objects.InsertRange(0, busbars);
        objectNames = byTag.ToDictionary(pair => pair.Key, pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase);
        return objects;
    }

    /// <summary>Object name of a device tag: "-K53" → "K53", "2-F3" → "2F3".</summary>
    public static string ObjectName(string tag) => new string((tag ?? "").Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } name ? name : "Device";
}
