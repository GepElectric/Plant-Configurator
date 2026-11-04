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
public sealed record PowerDevice(string Tag, PowerDeviceKind Kind, string Text, string Upstream, string Page = "", bool Incomer = false);

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

    public static List<ObjectConfig> Build(IReadOnlyList<PowerDevice> devices, string screen, ISet<string> relayCoils, IReadOnlySet<string> existingNames)
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

            if (device.Kind == PowerDeviceKind.Contactor && relayCoils.Contains(device.Tag.Trim()))
            {
                config.Ports["Command"] = $"@{ProcessEngine.RelaysLink}.{device.Tag.Trim()}";
            }

            byTag.TryAdd(device.Tag.Trim(), config);
            objects.Add(config);
        }

        // Feeders of the busbar: devices fed from it, left to right in the drawing order.
        var incomers = devices.Where(device => device.Incomer).ToList();
        var feeders = devices.Where(device => !device.Incomer && (device.Upstream.Length == 0 || !byTag.ContainsKey(device.Upstream))).ToList();
        // 11 feeders per busbar; its 12th feeder supplies the next bar when there are more.
        const int perBusbar = FeedersPerBusbar - 1;
        var busbars = new List<ObjectConfig>();
        var busbarCount = Math.Max(1, (feeders.Count + perBusbar - 1) / perBusbar);
        for (var b = 0; b < busbarCount; b++)
        {
            var busbar = new ObjectConfig
            {
                Name = Unique(b == 0 ? "Busbar" : $"Busbar{b + 1}"),
                Type = "Busbar",
                Description = "Busbar L1/L2/L3 (EPLAN)",
                Screen = screen,
                X = Left - 40 + b * perBusbar * ColumnWidth,
                Y = BusbarY
            };
            busbar.Options["Feeders"] = FeedersPerBusbar.ToString(CultureInfo.InvariantCulture);
            busbar.Params["Length"] = Math.Max(300, Math.Min(perBusbar, feeders.Count - b * perBusbar) * ColumnWidth + 40);
            if (b > 0)
            {
                busbar.Ports["Supply1"] = $"@{busbars[^1].Name}.Feeder{FeedersPerBusbar}";
            }
            else if (incomers.Count > 0)
            {
                // The main switch supplies the busbar; drawn above it.
                busbar.Ports["Supply1"] = $"@{byTag[incomers[0].Tag.Trim()].Name}.Live";
                byTag[incomers[0].Tag.Trim()].X = Left;
                byTag[incomers[0].Tag.Trim()].Y = IncomerY;
            }

            busbars.Add(busbar);
        }

        for (var i = 0; i < feeders.Count; i++)
        {
            byTag[feeders[i].Tag.Trim()].Ports["Supply"] = $"@{busbars[i / perBusbar].Name}.Feeder{i % perBusbar + 1}";
        }

        foreach (var device in devices.Where(device => device.Upstream.Length > 0 && byTag.ContainsKey(device.Upstream)))
        {
            byTag[device.Tag.Trim()].Ports["Supply"] = $"@{byTag[device.Upstream].Name}.Live";
        }

        // Layout: one column per feeder, the chain below it.
        for (var i = 0; i < feeders.Count; i++)
        {
            var row = 0;
            var column = Left + i * ColumnWidth;
            var current = feeders[i];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (current is not null && seen.Add(current.Tag))
            {
                var config = byTag[current.Tag.Trim()];
                config.X = column;
                config.Y = BusbarY + 70 + row * RowHeight;
                row++;
                current = devices.FirstOrDefault(device => device.Upstream.Equals(current.Tag, StringComparison.OrdinalIgnoreCase));
            }
        }

        objects.InsertRange(0, busbars);
        return objects;
    }

    /// <summary>Object name of a device tag: "-K53" → "K53", "2-F3" → "2F3".</summary>
    public static string ObjectName(string tag) => new string((tag ?? "").Where(char.IsLetterOrDigit).ToArray()) is { Length: > 0 } name ? name : "Device";
}
