using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using S7.Net;
using InvalidDataException = System.IO.InvalidDataException;

namespace TiaTraceStudio;

public interface IPlcReader : IDisposable
{
    bool IsConnected { get; }
    void Connect();
    byte[] Read(S7Area area, int db, int start, int count);
    void Disconnect();
}

/// <summary>S7 (PUT/GET) access to absolute addresses of non-optimized blocks.</summary>
public sealed class S7NetReader(string ip, short rack, short slot) : IPlcReader
{
    Plc? plc;
    public bool IsConnected => plc?.IsConnected == true;
    public void Connect()
    {
        Disconnect();
        plc = new Plc(CpuType.S71500, ip.Trim(), rack, slot) { ReadTimeout = 2000, WriteTimeout = 2000 };
        plc.Open();
    }
    public byte[] Read(S7Area area, int db, int start, int count) =>
        (plc ?? throw new InvalidOperationException("PLC nije spojen.")).ReadBytes(area switch
        {
            S7Area.DataBlock => DataType.DataBlock, S7Area.Memory => DataType.Memory, S7Area.Input => DataType.Input, _ => DataType.Output
        }, db, start, count);
    public void Disconnect() { try { plc?.Close(); } catch { /* already closed */ } plc = null; }
    public void Dispose() => Disconnect();
}

public sealed record RecordingChannel(string Tag, S7Address Address, string Prefix = "")
{
    /// <summary>Same column name as a TIA trace export, so names and scaling are shared with imported CSVs;
    /// recordings from several PLCs prefix the PLC name ("PLC agregata 2: MEAS…").</summary>
    public string Source => (Prefix.Length > 0 ? Prefix + ": " : "") + $"{TagMatcher.Normalize(Tag)}[{Address}]";
}

public sealed record PlcSource(string Name, IPlcReader Reader, IReadOnlyList<RecordingChannel> Channels);

public static class ReadPlan
{
    public sealed record Chunk(S7Area Area, int Db, int Start, int Count, (int Channel, int Offset)[] Items);

    /// <summary>Groups nearby addresses of the same block into a few contiguous byte reads.</summary>
    public static List<Chunk> Build(IReadOnlyList<S7Address> addresses, int maxGap = 64, int maxSize = 900)
    {
        var chunks = new List<Chunk>();
        foreach (var block in addresses.Select((a, i) => (a, i)).GroupBy(x => (x.a.Area, x.a.Db)))
        {
            var items = block.OrderBy(x => x.a.Byte).ToList();
            int start = items[0].a.Byte, end = start + items[0].a.Size; var members = new List<(int, S7Address)>();
            void Flush() => chunks.Add(new(block.Key.Area, block.Key.Db, start, end - start, members.Select(m => (m.Item1, m.Item2.Byte - start)).ToArray()));
            foreach (var (address, index) in items)
            {
                int itemEnd = address.Byte + address.Size;
                if (members.Count > 0 && (address.Byte - end > maxGap || Math.Max(end, itemEnd) - start > maxSize))
                { Flush(); members.Clear(); start = address.Byte; end = itemEnd; }
                members.Add((index, address)); end = Math.Max(end, itemEnd);
            }
            Flush();
        }
        return chunks;
    }
}

/// <summary>
/// Polls one or more PLCs at a fixed interval on a background thread and streams every sample to a CSV file.
/// All PLCs are read in parallel within each tick and share one time axis; a PLC that is slow or unreachable
/// gets NaN for that tick and never holds up the others. Only the channels chosen for the test are read.
/// </summary>
public sealed class PlcRecorder : IDisposable
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);

    sealed class SourceState(PlcSource source, int offset)
    {
        public readonly PlcSource Source = source;
        public readonly int Offset = offset;
        public readonly List<ReadPlan.Chunk> Plan = ReadPlan.Build(source.Channels.Select(c => c.Address).ToList());
        public Task<double[]>? Pending;
        public DateTime RetryAt;
    }

    readonly List<SourceState> sources = [];
    readonly object gate = new();
    readonly List<double> time = [];
    readonly List<double>[] values;
    readonly StreamWriter csv;
    double readTotal;
    int errors; string lastError = "";
    public IReadOnlyList<RecordingChannel> Channels { get; }
    public TimeSpan Interval { get; }
    public string CsvPath { get; }
    public DateTime StartedAt { get; private set; }
    public int Samples { get { lock (gate) return time.Count; } }
    public int Overruns { get; private set; }
    public int Errors { get { lock (gate) return errors; } }
    public string LastError { get { lock (gate) return lastError; } }
    public double MaxReadMs { get; private set; }
    public double AverageReadMs => Samples == 0 ? 0 : readTotal / Samples;
    public bool Connected => sources.All(s => s.Source.Reader.IsConnected);
    public double Elapsed { get { lock (gate) return time.Count == 0 ? 0 : time[^1]; } }

    public PlcRecorder(IPlcReader reader, IReadOnlyList<RecordingChannel> channels, TimeSpan interval, string csvPath)
        : this([new PlcSource("PLC", reader, channels)], interval, csvPath) { }

    public PlcRecorder(IReadOnlyList<PlcSource> plcs, TimeSpan interval, string csvPath)
    {
        Channels = plcs.SelectMany(p => p.Channels).ToList();
        if (Channels.Count == 0) throw new InvalidDataException("Nema tagova s adresom za snimanje.");
        if (interval < TimeSpan.FromMilliseconds(100)) throw new InvalidDataException("Najmanji takt snimanja je 100 ms.");
        Interval = interval; CsvPath = csvPath;
        int offset = 0;
        foreach (var plc in plcs.Where(p => p.Channels.Count > 0)) { sources.Add(new SourceState(plc, offset)); offset += plc.Channels.Count; }
        values = Channels.Select(_ => new List<double>()).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
        csv = new StreamWriter(csvPath, false, new System.Text.UTF8Encoding(true));
    }

    double[] Poll(SourceState state)
    {
        var result = new double[state.Source.Channels.Count]; Array.Fill(result, double.NaN);
        var reader = state.Source.Reader;
        bool connecting = false;
        try
        {
            if (!reader.IsConnected)
            {
                if (DateTime.UtcNow < state.RetryAt) return result;
                connecting = true; reader.Connect(); connecting = false;
            }
            foreach (var chunk in state.Plan)
            {
                var data = reader.Read(chunk.Area, chunk.Db, chunk.Start, chunk.Count);
                foreach (var (channel, offset) in chunk.Items) result[channel] = state.Source.Channels[channel].Address.Decode(data.AsSpan(offset));
            }
        }
        catch (Exception ex)
        {
            lock (gate) { errors++; lastError = (sources.Count > 1 ? state.Source.Name + ": " : "") + ex.Message; }
            // A broken read reconnects on the next tick; a failed connection waits before retrying.
            state.RetryAt = connecting ? DateTime.UtcNow.AddSeconds(2) : DateTime.MinValue;
            try { reader.Disconnect(); } catch { /* already broken */ }
            Array.Fill(result, double.NaN);
        }
        return result;
    }

    public void Run(CancellationToken token)
    {
        StartedAt = DateTime.Now;
        csv.WriteLine($"# TIA Trace Studio · snimanje s PLC-a · {StartedAt:yyyy-MM-dd HH:mm:ss} · takt {Interval.TotalMilliseconds:0} ms · {string.Join(", ", sources.Select(s => s.Source.Name))}");
        csv.WriteLine("Time [s];" + string.Join(";", Channels.Select(c => c.Source)));
        timeBeginPeriod(1);
        var clock = Stopwatch.StartNew(); var flushed = TimeSpan.Zero; long tick = 0;
        var row = new double[Channels.Count];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var before = clock.Elapsed;
                // Start a read on every PLC that is not still busy with an earlier one.
                var started = new List<SourceState>();
                foreach (var state in sources)
                    if (state.Pending == null || state.Pending.IsCompleted) { var s = state; state.Pending = Task.Run(() => Poll(s)); started.Add(s); }
                var budget = TimeSpan.FromMilliseconds(Math.Max(50, Interval.TotalMilliseconds * 0.7));
                try { Task.WaitAll(started.Select(s => (Task)s.Pending!).ToArray(), budget); } catch (AggregateException) { }
                Array.Fill(row, double.NaN);
                foreach (var state in started.Where(s => s.Pending!.IsCompletedSuccessfully))
                    Array.Copy(state.Pending!.Result, 0, row, state.Offset, state.Source.Channels.Count);
                var after = clock.Elapsed; double readMs = (after - before).TotalMilliseconds;
                double t = (before + after).TotalSeconds / 2;
                lock (gate)
                {
                    if (time.Count == 0 || t > time[^1])
                    {
                        time.Add(t); for (int i = 0; i < row.Length; i++) values[i].Add(row[i]);
                        readTotal += readMs; MaxReadMs = Math.Max(MaxReadMs, readMs);
                    }
                }
                csv.WriteLine(t.ToString("0.000", CultureInfo.InvariantCulture) + ";" + string.Join(";", row.Select(v => double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) : "")));
                if (clock.Elapsed - flushed > TimeSpan.FromSeconds(1)) { csv.Flush(); flushed = clock.Elapsed; }
                tick++;
                var next = Interval * tick;
                if (clock.Elapsed > next)
                {
                    long due = (long)(clock.Elapsed / Interval) + 1;
                    Overruns += (int)(due - tick); tick = due; next = Interval * tick;
                }
                var wait = next - clock.Elapsed;
                if (wait > TimeSpan.Zero) token.WaitHandle.WaitOne(wait);
            }
        }
        finally
        {
            timeEndPeriod(1); csv.Flush();
            try { Task.WaitAll(sources.Where(s => s.Pending != null).Select(s => (Task)s.Pending!).ToArray(), 3000); } catch (AggregateException) { }
            foreach (var state in sources) state.Source.Reader.Disconnect();
        }
    }

    /// <summary>Copies the recorded samples, optionally only the last <paramref name="seconds"/>.</summary>
    public (double[] Time, double[][] Values) Snapshot(double? seconds = null)
    {
        lock (gate)
        {
            int from = 0;
            if (seconds is double window && time.Count > 0)
            {
                double limit = time[^1] - window; from = time.BinarySearch(limit); if (from < 0) from = ~from;
            }
            int count = time.Count - from;
            return (time.GetRange(from, count).ToArray(), values.Select(v => v.GetRange(from, count).ToArray()).ToArray());
        }
    }

    public TraceData ToTraceData()
    {
        var (t, v) = Snapshot();
        if (t.Length < 2) throw new InvalidDataException("Snimka ima manje od dva uzorka.");
        double origin = t[0];
        var data = new TraceData { FileName = Path.GetFileName(CsvPath), TimeHeader = $"Time [s] · PC sat · {StartedAt:yyyy-MM-dd HH:mm:ss}", Time = t.Select(x => x - origin).ToArray() };
        string[] colors = ["#2563EB", "#E88821", "#109C83", "#A855F7", "#E14D72", "#0891B2"];
        for (int i = 0; i < Channels.Count; i++) data.Signals.Add(new Signal { Source = Channels[i].Source, Color = colors[i % colors.Length], Values = v[i] });
        data.Warnings.Add($"Snimljeno izravno s PLC-a (S7), takt {Interval.TotalMilliseconds:0} ms, vrijeme s PC sata · PLC: {string.Join(", ", sources.Select(s => s.Source.Name))}. Čitanje: prosjek {AverageReadMs:0.0} ms, najviše {MaxReadMs:0.0} ms.");
        if (Overruns > 0) data.Warnings.Add($"Propušteno taktova (čitanje dulje od takta): {Overruns}.");
        if (Errors > 0) data.Warnings.Add($"Greške komunikacije: {Errors}. Zadnja: {LastError}");
        var invalid = data.Signals.Select(s => (s.Source, Count: s.Values.Count(double.IsNaN))).Where(x => x.Count > 0).ToList();
        if (invalid.Count > 0)
            data.Warnings.Add($"Neispravna mjerenja (NaN – greška senzora, mjerenja ili komunikacije), prikazana kao 0 u boji {SignalDisplay.InvalidColor}: " +
                string.Join("; ", invalid.Take(20).Select(x => $"{x.Source} – {x.Count} od {t.Length} uzoraka")) + (invalid.Count > 20 ? $"; … još {invalid.Count - 20}" : "") + ".");
        return data;
    }

    public void Dispose() { csv.Dispose(); foreach (var state in sources) state.Source.Reader.Dispose(); }
}
