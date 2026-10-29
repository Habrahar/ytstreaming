namespace Bridge;

// Only completed selections enter history. Navigation and manual selection share
// cancellation, so a slow recommendation cannot replace a newer manual choice.
public sealed class PlaybackNavigator(AudioEngine engine, IMusicProvider catalog, ITrackRecommendations recommendations)
{
    private readonly object gate = new();
    private readonly List<Track> history = [];
    private int cursor = -1;
    private long version;
    private CancellationTokenSource? pending;

    public object Status
    {
        get
        {
            lock (gate) return new { canPrevious = cursor > 0,
                canNext = engine.Current?.Provider == "youtube", position = cursor, count = history.Count };
        }
    }

    public Track? Find(string id)
    {
        lock (gate) return history.LastOrDefault(track => track.Id == id);
    }

    public Task PlayAsync(Track track, string? requestId, CancellationToken token = default) => ChangeAsync(track, 0, requestId, token);
    public Task NextAsync(string? requestId, CancellationToken token = default) => ChangeAsync(null, 1, requestId, token);
    public Task PreviousAsync(string? requestId, CancellationToken token = default) => ChangeAsync(null, -1, requestId, token);

    private async Task ChangeAsync(Track? selected, int direction, string? requestId, CancellationToken token)
    {
        CancellationTokenSource own;
        long currentVersion;
        int? targetPosition = null;
        Track? seed;
        HashSet<string> visited;
        lock (gate)
        {
            pending?.Cancel();
            pending = own = CancellationTokenSource.CreateLinkedTokenSource(token);
            currentVersion = ++version;
            seed = engine.Current;
            visited = history.Select(track => track.Id).ToHashSet();
            if (direction < 0 && cursor > 0) targetPosition = cursor - 1;
            if (direction > 0 && cursor + 1 < history.Count) targetPosition = cursor + 1;
            if (targetPosition.HasValue) selected = history[targetPosition.Value];
        }
        try
        {
            if (selected is null)
            {
                if (direction < 0) throw new ProviderException("В истории пока нет предыдущей песни.");
                if (seed?.Provider == "youtube")
                {
                    var tracks = await recommendations.RelatedAsync(seed, own.Token);
                    selected = tracks.FirstOrDefault(track => track.Id != seed.Id && !visited.Contains(track.Id))
                        ?? tracks.FirstOrDefault(track => track.Id != seed.Id);
                    if (selected is null) throw new ProviderException("YouTube не предложил следующую песню.");
                }
                else
                {
                    var tracks = catalog.Search(null);
                    var index = Array.FindIndex(tracks, track => track.Id == seed?.Id);
                    selected = tracks[(index + 1) % tracks.Length];
                }
            }
            own.Token.ThrowIfCancellationRequested();
            await engine.PlayAsync(selected, requestId, own.Token);
            lock (gate)
            {
                own.Token.ThrowIfCancellationRequested();
                if (currentVersion != version) throw new SelectionSupersededException();
                if (targetPosition.HasValue) cursor = targetPosition.Value;
                else if (cursor < 0 || history[cursor].Id != selected.Id)
                {
                    if (cursor + 1 < history.Count) history.RemoveRange(cursor + 1, history.Count - cursor - 1);
                    history.Add(selected);
                    if (history.Count > 64) history.RemoveAt(0);
                    cursor = history.Count - 1;
                }
            }
        }
        catch (OperationCanceledException) when (own.IsCancellationRequested) { throw new SelectionSupersededException(); }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(pending, own)) pending = null;
                own.Dispose();
            }
        }
    }

    public async Task StopAsync()
    {
        lock (gate) { ++version; pending?.Cancel(); }
        await engine.StopPlaybackAsync();
    }
}
