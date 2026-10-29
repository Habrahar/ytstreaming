namespace Bridge;

// Local, bounded timing samples only: no track, browser identity or media URL.
public sealed record StartupSample(string RequestId, int CommandMs, int PlayingMs, int? MetadataMs, double BufferedSeconds);

public sealed class StartupMeasurements
{
    private readonly object gate = new();
    private readonly Queue<StartupSample> samples = new();
    public bool Add(StartupSample sample)
    {
        if (string.IsNullOrEmpty(sample.RequestId) || sample.RequestId.Length > 64 ||
            sample.CommandMs is < 0 or > 120000 || sample.PlayingMs is < 0 or > 120000 ||
            sample.MetadataMs is < 0 or > 120000 || !double.IsFinite(sample.BufferedSeconds) ||
            sample.BufferedSeconds is < 0 or > 600) return false;
        lock (gate)
        {
            samples.Enqueue(sample);
            while (samples.Count > 16) samples.Dequeue();
        }
        return true;
    }
    public StartupSample[] Snapshot { get { lock (gate) return samples.ToArray(); } }
}
