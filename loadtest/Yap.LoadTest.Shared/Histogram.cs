namespace Yap.LoadTest.Shared;

/// <summary>
/// Latency counts in fixed log-spaced buckets. A worker fills one per second and ships the counts; the orchestrator
/// merges them across workers and over time. Percentiles come out as the upper bound of the bucket, so they are
/// never optimistic. Max, sum and count are exact.
/// </summary>
public sealed class Histogram
{
    public static readonly long[] Bounds = [1, 2, 3, 5, 8, 10, 15, 20, 30, 50, 75, 100, 150, 200, 300, 500, 750, 1000, 1500, 2000, 3000, 5000, 10000, 20000, long.MaxValue];

    private readonly long[] _counts = new long[Bounds.Length];
    private long _max, _sum, _count;
    private readonly Lock _gate = new();

    public void Add(long ms)
    {
        if (ms < 0) ms = 0;
        var i = Array.FindIndex(Bounds, b => ms <= b);
        lock (_gate)
        {
            _counts[i]++;
            _count++;
            _sum += ms;
            if (ms > _max) _max = ms;
        }
    }

    public void Merge(HistogramData data)
    {
        lock (_gate)
        {
            for (var i = 0; i < _counts.Length; i++) _counts[i] += data.Counts[i];
            _count += data.Count;
            _sum += data.Sum;
            _max = Math.Max(_max, data.Max);
        }
    }

    /// <summary>Takes the data and resets. A worker calls it once a second.</summary>
    public HistogramData Drain()
    {
        lock (_gate)
        {
            var data = new HistogramData((long[])_counts.Clone(), _max, _sum, _count);
            Array.Clear(_counts);
            (_max, _sum, _count) = (0, 0, 0);
            return data;
        }
    }

    public HistogramData Snapshot() { lock (_gate) return new HistogramData((long[])_counts.Clone(), _max, _sum, _count); }
}

public sealed record HistogramData(long[] Counts, long Max, long Sum, long Count)
{
    public static HistogramData Empty => new(new long[Histogram.Bounds.Length], 0, 0, 0);

    public HistogramData Plus(HistogramData other)
    {
        var counts = new long[Counts.Length];
        for (var i = 0; i < counts.Length; i++) counts[i] = Counts[i] + other.Counts[i];
        return new HistogramData(counts, Math.Max(Max, other.Max), Sum + other.Sum, Count + other.Count);
    }

    /// <summary>The upper bound of the bucket that holds the p-th percentile; -1 when empty.</summary>
    public long Percentile(double p)
    {
        if (Count == 0) return -1;
        var target = (long)Math.Ceiling(Count * p);
        long seen = 0;
        for (var i = 0; i < Counts.Length; i++)
        {
            seen += Counts[i];
            if (seen >= target) return i == Counts.Length - 1 ? Max : Histogram.Bounds[i];
        }
        return Max;
    }

    public double Mean => Count == 0 ? 0 : (double)Sum / Count;
}
