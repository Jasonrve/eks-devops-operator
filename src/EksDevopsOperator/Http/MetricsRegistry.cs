using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;

namespace EksDevopsOperator.Http;

/// Collects the operator's own metrics (System.Diagnostics.Metrics) and renders them in the Prometheus text format,
/// so the operator needs no web framework just to expose /metrics. Counters add up, observable instruments are read on each scrape,
/// histograms use fixed buckets (set per instrument, with a default).
public sealed class MetricsRegistry : IDisposable
{
    static readonly double[] DefaultBuckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    sealed class Series { public double Value; public long Count; public double Sum; public long[] Buckets = []; }
    sealed class Family { public string Help = ""; public string Type = ""; public double[] Bounds = []; public readonly SortedDictionary<string, Series> Series = new(StringComparer.Ordinal); }

    readonly MeterListener _listener = new();
    readonly object _gate = new();
    readonly SortedDictionary<string, Family> _families = new(StringComparer.Ordinal);
    readonly IReadOnlyDictionary<string, double[]> _buckets;

    public MetricsRegistry(string meterName, IReadOnlyDictionary<string, double[]>? buckets = null)
    {
        _buckets = buckets ?? new Dictionary<string, double[]>();
        _listener.InstrumentPublished = (instrument, listener) => { if (instrument.Meter.Name == meterName) listener.EnableMeasurementEvents(instrument); };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Record(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Record(i, v, tags));
        _listener.Start();
    }

    void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var labels = Labels(tags);
        lock (_gate)
        {
            var family = Family_(instrument);
            if (!family.Series.TryGetValue(labels, out var s)) family.Series[labels] = s = new Series { Buckets = new long[family.Bounds.Length] };
            switch (family.Type)
            {
                case "histogram":
                    s.Count++; s.Sum += value;
                    var idx = Array.FindIndex(family.Bounds, b => value <= b);
                    if (idx >= 0) s.Buckets[idx]++;
                    break;
                case "counter" when !instrument.IsObservable: s.Value += value; break;
                default: s.Value = value; break; // gauges and observable instruments report the current value
            }
        }
    }

    Family Family_(Instrument instrument)
    {
        if (_families.TryGetValue(instrument.Name, out var f)) return f;
        var kind = instrument.GetType().Name;
        var type = kind.StartsWith("Histogram", StringComparison.Ordinal) ? "histogram"
            : kind.Contains("UpDown", StringComparison.Ordinal) ? "gauge"
            : kind.Contains("Counter", StringComparison.Ordinal) ? "counter" : "gauge";
        return _families[instrument.Name] = new Family
        {
            Type = type, Help = string.IsNullOrWhiteSpace(instrument.Description) ? instrument.Name : instrument.Description!,
            Bounds = type == "histogram" ? (_buckets.TryGetValue(instrument.Name, out var b) ? b : DefaultBuckets) : [],
        };
    }

    static string Labels(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0) return "";
        var sorted = new List<KeyValuePair<string, object?>>(tags.Length);
        foreach (var t in tags) sorted.Add(t);
        sorted.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return string.Join(",", sorted.Select(t => $"{t.Key}=\"{Escape(Convert.ToString(t.Value, CultureInfo.InvariantCulture) ?? "")}\""));
    }

    static string Escape(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
    static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    static string With(string labels, string extra) => labels.Length == 0 ? extra : labels + "," + extra;
    static string Braces(string labels) => labels.Length == 0 ? "" : "{" + labels + "}";

    public string Render()
    {
        _listener.RecordObservableInstruments(); // pull the current value of observable gauges
        var sb = new StringBuilder();
        lock (_gate)
        {
            foreach (var (name, f) in _families)
            {
                sb.Append("# HELP ").Append(name).Append(' ').Append(f.Help.Replace("\\", "\\\\").Replace("\n", "\\n")).Append('\n');
                sb.Append("# TYPE ").Append(name).Append(' ').Append(f.Type).Append('\n');
                foreach (var (labels, s) in f.Series)
                {
                    if (f.Type != "histogram") { sb.Append(name).Append(Braces(labels)).Append(' ').Append(Num(s.Value)).Append('\n'); continue; }
                    long cumulative = 0;
                    for (var i = 0; i < f.Bounds.Length; i++)
                    {
                        cumulative += s.Buckets[i];
                        sb.Append(name).Append("_bucket{").Append(With(labels, $"le=\"{Num(f.Bounds[i])}\"")).Append("} ").Append(cumulative).Append('\n');
                    }
                    sb.Append(name).Append("_bucket{").Append(With(labels, "le=\"+Inf\"")).Append("} ").Append(s.Count).Append('\n');
                    sb.Append(name).Append("_sum").Append(Braces(labels)).Append(' ').Append(Num(s.Sum)).Append('\n');
                    sb.Append(name).Append("_count").Append(Braces(labels)).Append(' ').Append(s.Count).Append('\n');
                }
            }
        }
        return sb.ToString();
    }

    public void Dispose() => _listener.Dispose();
}
