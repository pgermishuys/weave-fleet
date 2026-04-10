using System.Globalization;
using System.Text;
using System.Text.Json;
using Shouldly;

namespace WeaveFleet.E2E.Infrastructure;

/// <summary>
/// Collects named latency measurements, computes summary statistics,
/// and supports threshold assertions for benchmark tests.
/// </summary>
public sealed class PerformanceMetrics
{
    private readonly Dictionary<string, List<double>> _measurements = new(StringComparer.Ordinal);

    /// <summary>Record a named measurement in milliseconds.</summary>
    public void Record(string metricName, double milliseconds)
    {
        if (!_measurements.TryGetValue(metricName, out var list))
        {
            list = [];
            _measurements[metricName] = list;
        }

        list.Add(milliseconds);
    }

    /// <summary>Compute summary statistics for a specific metric.</summary>
    public MetricSummary GetSummary(string metricName)
    {
        if (!_measurements.TryGetValue(metricName, out var list) || list.Count == 0)
            return new MetricSummary(metricName, 0, 0, 0, 0, 0, 0);

        var sorted = list.ToArray();
        Array.Sort(sorted);
        var count = sorted.Length;

        var p50 = Percentile(sorted, 0.50);
        var p95 = Percentile(sorted, 0.95);
        var mean = sorted.Average();

        return new MetricSummary(
            Name: metricName,
            Count: count,
            Min: sorted[0],
            P50: p50,
            P95: p95,
            Max: sorted[count - 1],
            Mean: mean);
    }

    /// <summary>Compute summaries for all recorded metrics.</summary>
    public IReadOnlyDictionary<string, MetricSummary> GetAllSummaries()
    {
        var result = new Dictionary<string, MetricSummary>(StringComparer.Ordinal);
        foreach (var key in _measurements.Keys)
            result[key] = GetSummary(key);
        return result;
    }

    /// <summary>
    /// Assert that a metric's p95 is below the given threshold.
    /// Throws <see cref="Xunit.Sdk.XunitException"/> on violation (via Shouldly).
    /// </summary>
    public void AssertP95Below(string metricName, double thresholdMs)
    {
        if (!_measurements.ContainsKey(metricName))
            throw new InvalidOperationException(
                $"Expected benchmark metric '{metricName}' to have recordings before asserting p95 threshold.");

        var summary = GetSummary(metricName);
        summary.P95.ShouldBeLessThan(thresholdMs,
            $"Metric '{metricName}' p95={summary.P95:F1}ms exceeded threshold {thresholdMs}ms " +
            $"(count={summary.Count}, min={summary.Min:F1}ms, max={summary.Max:F1}ms, mean={summary.Mean:F1}ms)");
    }

    /// <summary>
    /// Assert that no individual measurement exceeds the given ceiling.
    /// Throws <see cref="Xunit.Sdk.XunitException"/> on violation (via Shouldly).
    /// </summary>
    public void AssertMaxBelow(string metricName, double ceilingMs)
    {
        if (!_measurements.ContainsKey(metricName))
            throw new InvalidOperationException(
                $"Expected benchmark metric '{metricName}' to have recordings before asserting max threshold.");

        var summary = GetSummary(metricName);
        summary.Max.ShouldBeLessThan(ceilingMs,
            $"Metric '{metricName}' max={summary.Max:F1}ms exceeded ceiling {ceilingMs}ms " +
            $"(count={summary.Count}, p50={summary.P50:F1}ms, p95={summary.P95:F1}ms, mean={summary.Mean:F1}ms)");
    }

    /// <summary>Format all metrics as a human-readable table string for test output.</summary>
    public string ToReport()
    {
        var summaries = GetAllSummaries();
        if (summaries.Count == 0)
            return "(no metrics recorded)";

        const int metricWidth = 30;
        const int numWidth = 7;

        var sb = new StringBuilder();

        // Header
        sb.Append('┌');
        sb.Append('─', metricWidth);
        sb.Append('┬');
        sb.Append('─', numWidth);
        sb.Append('┬');
        sb.Append('─', numWidth);
        sb.Append('┬');
        sb.Append('─', numWidth);
        sb.Append('┬');
        sb.Append('─', numWidth);
        sb.Append('┬');
        sb.Append('─', numWidth);
        sb.AppendLine('┐'.ToString());

        sb.Append('│');
        sb.Append(" Metric".PadRight(metricWidth));
        sb.Append('│');
        sb.Append(" Count ".PadRight(numWidth));
        sb.Append('│');
        sb.Append(" P50   ".PadRight(numWidth));
        sb.Append('│');
        sb.Append(" P95   ".PadRight(numWidth));
        sb.Append('│');
        sb.Append(" Max   ".PadRight(numWidth));
        sb.Append('│');
        sb.Append(" Mean  ".PadRight(numWidth));
        sb.AppendLine("│");

        sb.Append('├');
        sb.Append('─', metricWidth);
        sb.Append('┼');
        sb.Append('─', numWidth);
        sb.Append('┼');
        sb.Append('─', numWidth);
        sb.Append('┼');
        sb.Append('─', numWidth);
        sb.Append('┼');
        sb.Append('─', numWidth);
        sb.Append('┼');
        sb.Append('─', numWidth);
        sb.AppendLine('┤'.ToString());

        foreach (var s in summaries.Values)
        {
            var name = s.Name.Length > metricWidth - 1
                ? s.Name[..(metricWidth - 2)] + "…"
                : s.Name;

            sb.Append('│');
            sb.Append((' ' + name).PadRight(metricWidth));
            sb.Append('│');
            sb.Append((' ' + s.Count.ToString(CultureInfo.InvariantCulture)).PadRight(numWidth));
            sb.Append('│');
            sb.Append((' ' + s.P50.ToString("F1", CultureInfo.InvariantCulture)).PadRight(numWidth));
            sb.Append('│');
            sb.Append((' ' + s.P95.ToString("F1", CultureInfo.InvariantCulture)).PadRight(numWidth));
            sb.Append('│');
            sb.Append((' ' + s.Max.ToString("F1", CultureInfo.InvariantCulture)).PadRight(numWidth));
            sb.Append('│');
            sb.Append((' ' + s.Mean.ToString("F1", CultureInfo.InvariantCulture)).PadRight(numWidth));
            sb.AppendLine("│");
        }

        sb.Append('└');
        sb.Append('─', metricWidth);
        sb.Append('┴');
        sb.Append('─', numWidth);
        sb.Append('┴');
        sb.Append('─', numWidth);
        sb.Append('┴');
        sb.Append('─', numWidth);
        sb.Append('┴');
        sb.Append('─', numWidth);
        sb.Append('┴');
        sb.Append('─', numWidth);
        sb.Append('┘');

        return sb.ToString();
    }

    /// <summary>Serialize all metrics to a JSON string for artifact capture.</summary>
    public string ToJson(BenchmarkMetricsMetadata metadata)
    {
        var payload = new BenchmarkMetricsPayload(metadata, GetAllSummaries());
        return JsonSerializer.Serialize(payload, JsonOptions.Indented);
    }

    private static class JsonOptions
    {
        internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 1)
            return sorted[0];

        var index = (int)Math.Ceiling(sorted.Length * percentile) - 1;
        index = Math.Max(0, Math.Min(index, sorted.Length - 1));
        return sorted[index];
    }
}

/// <summary>Summary statistics for a single named metric.</summary>
public sealed record MetricSummary(
    string Name,
    int Count,
    double Min,
    double P50,
    double P95,
    double Max,
    double Mean);

/// <summary>Metadata describing a benchmark scenario/test execution.</summary>
public sealed record BenchmarkMetricsMetadata(
    string TestClass,
    string TestMethod,
    string Scenario,
    string BenchmarkRunId,
    DateTimeOffset CapturedAtUtc);

/// <summary>Structured benchmark artifact payload for CI summaries.</summary>
public sealed record BenchmarkMetricsPayload(
    BenchmarkMetricsMetadata Metadata,
    IReadOnlyDictionary<string, MetricSummary> Metrics);
