namespace CwcConfigurator.Services.Simulation;

/// <summary>
/// Number filter of the simulator signal list: "3", "1-4", "5-" (5 and up) or "1,3,7-9" (spaces and ';' also
/// separate). Empty matches everything; parts that are not numbers are ignored.
/// </summary>
public static class NumberFilter
{
    public static bool Matches(string? filter, int value)
    {
        var any = false;
        foreach (var part in (filter ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseRange(part, out var from, out var to))
            {
                continue;
            }

            any = true;
            if (value >= from && value <= to)
            {
                return true;
            }
        }

        return !any;
    }

    private static bool TryParseRange(string part, out int from, out int to)
    {
        var bounds = part.Split('-', 2);
        to = 0;
        if (!int.TryParse(bounds[0], out from))
        {
            return false;
        }

        if (bounds.Length == 1)
        {
            to = from;
        }
        else if (bounds[1].Length == 0)
        {
            to = int.MaxValue;
        }
        else if (!int.TryParse(bounds[1], out to))
        {
            return false;
        }

        if (to < from)
        {
            (from, to) = (to, from);
        }

        return true;
    }
}
