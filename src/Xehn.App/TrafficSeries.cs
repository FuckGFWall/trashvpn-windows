namespace Xehn.App;

public readonly record struct TrafficPoint(double Seconds, double Value);

// Time, not sample count, owns the viewport. Zero traffic and disconnects must
// not discard preceding measurements.
public sealed class TrafficSeries(TimeProvider? timeProvider = null)
{
    public const double WindowSeconds = 90;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly long origin = (timeProvider ?? TimeProvider.System).GetTimestamp();
    private readonly List<TrafficPoint> points = [];
    private readonly object gate = new();
    public double NowSeconds => clock.GetElapsedTime(origin).TotalSeconds;
    public event EventHandler<EventArgs>? Changed;

    public void Add(double value)
    {
        lock (gate)
        {
            var now = NowSeconds;
            Trim(now); // A pause must not bridge an expired sample to a new one.
            value = double.IsFinite(value) ? Math.Max(0, value) : 0;
            if (points.Count > 0 && now <= points[^1].Seconds)
                points[^1] = new(points[^1].Seconds, value);
            else points.Add(new(now, value));
            Trim(now);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public TrafficPoint[] Snapshot(double? at = null)
    {
        lock (gate) { Trim(at ?? NowSeconds); return points.ToArray(); }
    }
    public bool HasVisibleTraffic => Snapshot().Any(p => p.Value > 0);
    private void Trim(double now)
    {
        var cutoff = now - WindowSeconds;
        // One point beyond the edge preserves the interpolated boundary.
        var remove = 0;
        while (remove + 1 < points.Count && points[remove + 1].Seconds < cutoff) remove++;
        if (remove > 0) points.RemoveRange(0, remove);
        if (points.Count > 0 && points[^1].Seconds < cutoff) points.Clear();
    }
}
