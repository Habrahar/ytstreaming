namespace Bridge;

public record NavigationPreview(long Revision, long Version, Track[] Previous, Track[] Next);

// Only completed selections enter history. Navigation and manual selection share
// cancellation, so a slow recommendation cannot replace a newer manual choice.
public sealed class PlaybackNavigator(AudioEngine engine, IMusicProvider catalog, ITrackRecommendations recommendations) : IDisposable
{
    private readonly object gate = new();
    private readonly List<Track> history = [];
    private int cursor = -1;
    private long version;
    private CancellationTokenSource? pending;
    private readonly List<Track> upcoming = [];
    private long queueGeneration;
    private RadioLoad? radio;
    private sealed record RadioLoad(Track Seed, long Generation, HashSet<string> Visited,
        CancellationTokenSource Cancellation, TaskCompletionSource<Exception?> Completion);

    public object Status
    {
        get
        {
            lock (gate) return new { canPrevious = cursor > 0,
                canNext = engine.Current?.Provider == "youtube", position = cursor, count = history.Count,
                version, busy = pending is not null };
        }
    }

    public Track? Find(string id)
    {
        lock (gate) return history.LastOrDefault(track => track.Id == id) ?? upcoming.FirstOrDefault(track => track.Id == id);
    }

    public Task PlayAsync(Track track, string? requestId, CancellationToken token = default) => ChangeAsync(track, 0, requestId, token);
    public Task NextAsync(string? requestId, CancellationToken token = default, string? expectedId = null, long? expectedRevision = null) => ChangeAsync(null, 1, requestId, token, expectedId, expectedRevision);
    public Task PreviousAsync(string? requestId, CancellationToken token = default, string? expectedId = null, long? expectedRevision = null) => ChangeAsync(null, -1, requestId, token, expectedId, expectedRevision);

    public async Task<NavigationPreview> PreviewAsync(long revision, long expectedVersion, CancellationToken token = default)
    {
        Track? seed;
        bool load;
        lock (gate)
        {
            CheckPreview(revision, expectedVersion);
            seed = history.LastOrDefault() ?? engine.Current;
            load = seed?.Provider == "youtube" && engine.LatestRequestId is not null &&
                history.Count - cursor - 1 < 2 && upcoming.Count == 0;
        }
        if (load) await EnsureUpcomingAsync(seed!, token);
        lock (gate)
        {
            CheckPreview(revision, expectedVersion);
            return new(revision, version, history.Take(Math.Max(0, cursor)).Reverse().Take(2).ToArray(),
                history.Skip(cursor + 1).Concat(upcoming).Take(2).ToArray());
        }
    }

    private void CheckPreview(long revision, long expectedVersion)
    {
        if (engine.Revision != revision || version != expectedVersion || pending is not null) throw new SelectionSupersededException();
    }

    private async Task EnsureUpcomingAsync(Track seed, CancellationToken token)
    {
        RadioLoad load;
        bool start = false;
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (upcoming.Count > 0) return;
            if (radio is null)
            {
                radio = new(seed, queueGeneration, history.Select(t => t.Id).ToHashSet(), new(),
                    new(TaskCreationOptions.RunContinuationsAsynchronously));
                start = true;
            }
            load = radio;
        }
        if (start) _ = LoadRadioAsync(load);
        var error = await load.Completion.Task.WaitAsync(token);
        if (error is not null) throw error;
    }

    private async Task LoadRadioAsync(RadioLoad load)
    {
        Exception? error = null;
        try
        {
            var tracks = await recommendations.RelatedAsync(load.Seed, load.Cancellation.Token);
            lock (gate)
            {
                if (radio != load || load.Generation != queueGeneration) throw new SelectionSupersededException();
                var candidates = tracks.Where(t => t.Provider == "youtube" && t.Id != load.Seed.Id).DistinctBy(t => t.Id).ToArray();
                var fresh = candidates.Where(t => !load.Visited.Contains(t.Id)).ToArray();
                upcoming.AddRange((fresh.Length > 0 ? fresh : candidates).Take(12));
                if (upcoming.Count == 0) throw new ProviderException("YouTube не предложил следующую песню.");
            }
        }
        catch (OperationCanceledException) { error = new SelectionSupersededException(); }
        catch (Exception ex) { error = ex; }
        finally
        {
            lock (gate) if (radio == load) radio = null;
            load.Completion.TrySetResult(error);
            load.Cancellation.Dispose();
        }
    }

    private void CancelRadio(bool clear)
    {
        ++queueGeneration;
        if (radio is { } load)
        {
            radio = null;
            load.Cancellation.Cancel();
            load.Completion.TrySetResult(new SelectionSupersededException());
        }
        if (clear) upcoming.Clear();
    }

    private async Task ChangeAsync(Track? selected, int direction, string? requestId, CancellationToken token,
        string? expectedId = null, long? expectedRevision = null)
    {
        CancellationTokenSource own;
        long currentVersion;
        int? targetPosition = null;
        Track? seed;
        bool fromQueue = false;
        lock (gate)
        {
            if (expectedRevision.HasValue && expectedRevision != engine.Revision) throw new SelectionSupersededException();
            pending?.Cancel();
            if (direction == 0) CancelRadio(clear: true);
            pending = own = CancellationTokenSource.CreateLinkedTokenSource(token);
            currentVersion = ++version;
            seed = engine.Current;
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
                    await EnsureUpcomingAsync(seed, own.Token);
                    lock (gate) selected = upcoming.FirstOrDefault();
                    if (selected is null) throw new ProviderException("YouTube не предложил следующую песню.");
                    fromQueue = true;
                }
                else
                {
                    var tracks = catalog.Search(null);
                    var index = Array.FindIndex(tracks, track => track.Id == seed?.Id);
                    selected = tracks[(index + 1) % tracks.Length];
                }
            }
            own.Token.ThrowIfCancellationRequested();
            if (expectedId is not null && selected.Id != expectedId) throw new SelectionSupersededException();
            await engine.PlayAsync(selected, requestId, own.Token);
            lock (gate)
            {
                own.Token.ThrowIfCancellationRequested();
                if (currentVersion != version) throw new SelectionSupersededException();
                if (fromQueue && upcoming.FirstOrDefault()?.Id == selected.Id) upcoming.RemoveAt(0);
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
        lock (gate) { ++version; pending?.Cancel(); CancelRadio(clear: false); }
        await engine.StopPlaybackAsync();
    }

    public void Dispose() { lock (gate) { pending?.Cancel(); CancelRadio(clear: false); } }
}
