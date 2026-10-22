using System.Globalization;
using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace TiaTraceStudio;

public static class TraceCsv
{
    static readonly string[] Colors = ["#2563EB", "#E88821", "#109C83", "#A855F7", "#E14D72", "#0891B2"];
    public static TraceData Read(string path)
    {
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        string? header = null; string delimiter = ","; int timeIndex = -1;
        // Siemens exports may include preamble records before the actual header.
        for (int line = 0; line < 100 && !reader.EndOfStream; line++)
        {
            var candidate = reader.ReadLine()!;
            foreach (var sep in new[] { ";", ",", "\t" })
            {
                var columns = ParseLine(candidate, sep);
                int index = Array.FindIndex(columns, c => IsTime(c));
                if (index >= 0 && columns.Length >= 2) { header = candidate; delimiter = sep; timeIndex = index; break; }
            }
            if (header != null) break;
        }
        if (header == null) throw new InvalidDataException("Nije pronađeno zaglavlje s vremenskim stupcem X(ms), Time ili Zeit. Podržani su CSV i TSV s numeričkim vremenom.");
        string[] names = ParseLine(header, delimiter);
        var signalIndices = Enumerable.Range(0, names.Length).Where(i => i != timeIndex && !names[i].Trim().Equals("Sample", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (signalIndices.Length == 0) throw new InvalidDataException("CSV ne sadrži signale.");
        var values = signalIndices.Select(_ => new List<double>()).ToArray();
        var times = new List<double>();
        int rejected = 0, missing = 0, duplicate = 0;
        decimal? origin = null;
        decimal divisor = names[timeIndex].Contains("ms", StringComparison.OrdinalIgnoreCase) ? 1000m :
            names[timeIndex].Contains("us", StringComparison.OrdinalIgnoreCase) || names[timeIndex].Contains("µs") ? 1000000m : 1m;
        using var parser = new TextFieldParser(reader) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
        parser.SetDelimiters(delimiter);
        while (!parser.EndOfData)
        {
            string[] row;
            try { row = parser.ReadFields() ?? []; } catch (MalformedLineException) { rejected++; continue; }
            if (row.Length != names.Length || !TryDecimal(row[timeIndex], delimiter, out decimal t)) { rejected++; continue; }
            origin ??= t;
            double relative = (double)((t - origin.Value) / divisor);
            if (times.Count > 0 && relative <= times[^1]) { duplicate++; continue; }
            times.Add(relative);
            for (int j = 0; j < signalIndices.Length; j++)
            {
                string raw = row[signalIndices[j]].Trim();
                if (raw.Equals("true", StringComparison.OrdinalIgnoreCase)) values[j].Add(1);
                else if (raw.Equals("false", StringComparison.OrdinalIgnoreCase)) values[j].Add(0);
                else if (TryDouble(raw, delimiter, out double v) && double.IsFinite(v)) values[j].Add(v);
                else { values[j].Add(double.NaN); missing++; }
            }
        }
        if (times.Count < 2) throw new InvalidDataException("Potrebna su barem dva uzorka s rastućim numeričkim vremenom.");
        var data = new TraceData { FileName = Path.GetFileName(path), TimeHeader = names[timeIndex], Time = times.ToArray() };
        for (int j = 0; j < signalIndices.Length; j++)
        {
            string source = names[signalIndices[j]];
            data.Signals.Add(new Signal { Source = source, Color = Colors[j % Colors.Length], Values = values[j].ToArray() });
        }
        if (rejected > 0) data.Warnings.Add($"Preskočeno redaka s neispravnom strukturom ili vremenom: {rejected}.");
        if (duplicate > 0) data.Warnings.Add($"Preskočeno duplih ili nerastućih vremenskih oznaka: {duplicate}.");
        if (missing > 0) data.Warnings.Add($"Nedostajuće/neispravne vrijednosti signala: {missing}. Prikazane su kao praznine.");
        var intervals = times.Skip(1).Select((v, i) => v - times[i]).Order().ToArray();
        double median = intervals[intervals.Length / 2];
        int gaps = intervals.Count(x => x > median * 1.5);
        if (gaps > 0) data.Warnings.Add($"Vremenske praznine veće od 1,5 × medijana intervala: {gaps}.");
        return data;
    }
    static bool IsTime(string text)
    {
        string t = text.Trim().ToLowerInvariant();
        return t.StartsWith("x(") || t.StartsWith("x (") || t.StartsWith("time") || t.StartsWith("zeit") || t == "t" || t.StartsWith("t(");
    }
    static string[] ParseLine(string line, string delimiter)
    {
        try { using var p = new TextFieldParser(new StringReader(line)); p.SetDelimiters(delimiter); p.HasFieldsEnclosedInQuotes = true; return p.ReadFields() ?? []; }
        catch (MalformedLineException) { return []; }
    }
    static bool TryDecimal(string value, string delimiter, out decimal number) => decimal.TryParse(
        delimiter == "," ? value : value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    static bool TryDouble(string value, string delimiter, out double number) => double.TryParse(
        delimiter == "," ? value : value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
}
