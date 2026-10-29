namespace Bridge;

// At most one speculative source URL, never a speculative player. A foreground
// Next shares YoutubeProvider's in-flight resolve rather than starting it twice.
public sealed class NextTrackPreparer(IMusicProvider provider) : IDisposable
{
    private readonly object gate = new();
    private string? desired;
    private CancellationTokenSource? cancellation;
    private string? readyTrackId;
    private DateTime readyAt;
    public object Status { get { lock (gate) return new { preparing = cancellation is null ? null : desired,
        ready = DateTime.UtcNow - readyAt < TimeSpan.FromMinutes(15) ? readyTrackId : null }; } }
    public void Schedule(Track? track, Func<bool> audioReady)
    {
        lock (gate)
        {
            if (track?.Id == desired && (track is null || cancellation is not null || DateTime.UtcNow - readyAt < TimeSpan.FromMinutes(15))) return;
            cancellation?.Cancel(); cancellation = null; desired = track?.Id; readyTrackId = null; readyAt = default;
            if (track is null || track.Provider != "youtube") return;
            var own = cancellation = new();
            _ = PrepareAsync(track, audioReady, own);
        }
    }
    private async Task PrepareAsync(Track track, Func<bool> ready, CancellationTokenSource own)
    {
        try
        {
            // Current audio gets the first connection and its initial buffer.
            for (var i = 0; !ready(); i++)
            {
                if (i >= 100) return;
                await Task.Delay(100, own.Token);
            }
            await Task.Delay(500, own.Token);
            await provider.ResolveAudioAsync(track, own.Token);
            lock (gate) if (ReferenceEquals(cancellation, own)) { readyTrackId = track.Id; readyAt = DateTime.UtcNow; }
        }
        catch (OperationCanceledException) { }
        catch (ProviderException) { /* Foreground Next can retry normally. */ }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(cancellation, own)) cancellation = null;
                own.Dispose();
            }
        }
    }
    public void Dispose() { lock (gate) { cancellation?.Cancel(); cancellation = null; desired = null; } }
}
