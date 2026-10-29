namespace Bridge;

public record NavigationPreview(long Revision, long Version, Track[] Previous, Track[] Next, Track? Track = null);

// Only completed selections enter history. Navigation and manual selection share
// cancellation, so a slow recommendation cannot replace a newer manual choice.
public sealed class PlaybackNavigator(AudioEngine engine, IMusicProvider catalog, ITrackRecommendations recommendations, NextTrackPreparer? preparer = null) : IDisposable
{
    private readonly object gate = new();
    private readonly List<Track> history = [];
    private int cursor = -1;
    private long version;
    private CancellationTokenSource? pending;
    private string? pendingRequestId;
    private bool automaticPending;
    private string? radioError;
    private SelectionPlan? plan;
    private readonly Dictionary<string, (long Sequence, long Order)> clients = new();
    private long commandOrder;
    private sealed class SelectionPlan(IEnumerable<Track> tracks, int historyCount, int queueStart, long position)
    {
        public readonly List<Track> Tracks = tracks.ToList();
        public readonly int HistoryCount = historyCount, QueueStart = queueStart;
        public long Start, Position = position;
        public long CommittedPosition = position, GroupOrigin;
        public string? GroupId;
        public Track[] RetryQueue = [];
        public Track? At(long index) => index >= Start && index - Start < Tracks.Count ? Tracks[(int)(index - Start)] : null;
        public IEnumerable<Track> After(long index) => Tracks.Skip((int)Math.Clamp(index + 1 - Start, 0, Tracks.Count));
    }
    private readonly List<Track> upcoming = [];
    private long queueGeneration;
    private RadioLoad? radio;
    private sealed record RadioLoad(Track Seed, long Generation, HashSet<string> Visited,
        CancellationTokenSource Cancellation, TaskCompletionSource<Exception?> Completion);

    public object Status
    {
        get
        {
            lock (gate) return new { canPrevious = plan is { } p ? p.Position > p.Start : cursor > 0,
                canNext = (plan?.At(plan.Position) ?? engine.Current)?.Provider == "youtube", position = cursor, count = history.Count,
                target = pending is null ? null : plan?.At(plan.Position), requestId = pending is null ? null : pendingRequestId,
                version, busy = pending is not null, preparation = preparer?.Status,
                radio = new { enabled = engine.RadioEnabled, advancing = automaticPending, error = radioError } };
        }
    }

    public Track? Find(string id)
    {
        lock (gate) return history.LastOrDefault(track => track.Id == id) ?? upcoming.FirstOrDefault(track => track.Id == id)
            ?? plan?.Tracks.FirstOrDefault(track => track.Id == id);
    }

    public Task PlayAsync(Track track, string? requestId, CancellationToken token = default, string? clientId = null, long? commandSequence = null) => ChangeAsync(track, 0, requestId, token, clientId: clientId, commandSequence: commandSequence);
    public Task NextAsync(string? requestId, CancellationToken token = default, string? expectedId = null, long? expectedRevision = null, string? clientId = null, long? commandSequence = null, string? navigationId = null, long? navigationOffset = null, Track? anchor = null) => ChangeAsync(null, 1, requestId, token, expectedId, expectedRevision, clientId, commandSequence, navigationId, navigationOffset, anchor);
    public Task PreviousAsync(string? requestId, CancellationToken token = default, string? expectedId = null, long? expectedRevision = null, string? clientId = null, long? commandSequence = null, string? navigationId = null, long? navigationOffset = null, Track? anchor = null) => ChangeAsync(null, -1, requestId, token, expectedId, expectedRevision, clientId, commandSequence, navigationId, navigationOffset, anchor);

    public Task AdvanceAutomaticallyAsync(long endedRevision, CancellationToken token = default) =>
        ChangeAsync(null, 1, "radio-" + Guid.NewGuid().ToString("N"), token, automaticFromRevision: endedRevision);

    public async Task PrepareRadioAsync(long revision, CancellationToken token)
    {
        long expectedVersion;
        lock (gate)
        {
            if (!engine.RadioEnabled || !engine.Ready || pending is not null || engine.Revision != revision)
                throw new SelectionSupersededException();
            expectedVersion = version;
        }
        await PreviewAsync(revision, expectedVersion, token);
    }

    public void AutomaticFailed(long endedRevision)
    {
        lock (gate)
        {
            if (!engine.RadioEnabled || pending is not null || engine.Revision != endedRevision || engine.NaturallyEndedRevision != endedRevision) return;
            radioError = "Не удалось включить следующую песню. Нажми ⏭, чтобы попробовать ещё раз, или выбери другую.";
            engine.ReleaseEndedStream(endedRevision);
        }
    }

    public async Task<NavigationPreview> PreviewAsync(long revision, long expectedVersion, CancellationToken token = default, bool deferUntilPlayback = false)
    {
        Track? seed;
        SelectionPlan? selectionPlan;
        bool load;
        lock (gate)
        {
            CheckPreview(revision, expectedVersion);
            // Passive previews from any phone must not start recommendations
            // while another phone is still opening the selected audio.
            if (deferUntilPlayback && !engine.BackgroundReady) throw new SelectionSupersededException();
            selectionPlan = plan;
            seed = history.LastOrDefault() ?? engine.Current;
            load = selectionPlan is null && seed?.Provider == "youtube" && engine.LatestRequestId is not null &&
                history.Count - cursor - 1 < 2 && upcoming.Count == 0;
        }
        if (selectionPlan is not null) await EnsurePlanAsync(selectionPlan, selectionPlan.Position + 2, token);
        if (load) await EnsureUpcomingAsync(seed!, token);
        NavigationPreview preview;
        lock (gate)
        {
            CheckPreview(revision, expectedVersion);
            preview = plan is { } p
                ? new(revision, version, p.Tracks.Take((int)Math.Clamp(p.Position - p.Start, 0, p.Tracks.Count)).Reverse().Take(64).ToArray(),
                    p.After(p.Position).ToArray(), p.At(p.Position))
                : new(revision, version, history.Take(Math.Max(0, cursor)).Reverse().Take(64).ToArray(),
                    history.Skip(cursor + 1).Concat(upcoming).ToArray(), engine.Current);
            if (pending is null && engine.Ready) preparer?.Schedule(preview.Next.FirstOrDefault(), () => engine.BackgroundReady);
        }
        return preview;
    }

    private void CheckPreview(long revision, long expectedVersion)
    {
        if (engine.Revision != revision || version != expectedVersion) throw new SelectionSupersededException();
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
                radio = new(seed, queueGeneration, history.Concat(plan?.Tracks ?? []).Select(t => t.Id).ToHashSet(), new(),
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

    private void AcceptCommand(string? clientId, long? sequence)
    {
        if (clientId is null && sequence is null) return; // Older API clients.
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 64 || sequence is null or < 1)
            throw new ProviderException("Некорректная команда плеера.");
        if (clients.TryGetValue(clientId, out var previous) && sequence <= previous.Sequence)
            throw new SelectionSupersededException();
        if (!clients.ContainsKey(clientId) && clients.Count >= 64) clients.Remove(clients.MinBy(pair => pair.Value.Order).Key);
        clients[clientId] = (sequence.Value, ++commandOrder);
    }

    private void AdoptUpcoming(SelectionPlan selectionPlan)
    {
        selectionPlan.Tracks.AddRange(upcoming); upcoming.Clear();
        if (selectionPlan.RetryQueue.Length == 0)
            selectionPlan.RetryQueue = selectionPlan.After(Math.Max(selectionPlan.CommittedPosition, selectionPlan.HistoryCount - 1)).Take(12).ToArray();
        // A long burst may cross several recommendation pages. Keep a bounded
        // window and absolute positions so skipped metadata cannot grow forever.
        var remove = (int)Math.Min(Math.Max(0, selectionPlan.Tracks.Count - 128), Math.Max(0, selectionPlan.Position - selectionPlan.Start - 64));
        if (remove > 0) { selectionPlan.Tracks.RemoveRange(0, remove); selectionPlan.Start += remove; }
    }

    private async Task EnsurePlanAsync(SelectionPlan selectionPlan, long position, CancellationToken token)
    {
        while (true)
        {
            Track? seed;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (plan != selectionPlan) throw new SelectionSupersededException();
                AdoptUpcoming(selectionPlan);
                if (selectionPlan.At(position) is not null) return;
                if (position < selectionPlan.Start) throw new SelectionSupersededException();
                seed = selectionPlan.Tracks.LastOrDefault() ?? engine.Current;
                if (seed?.Provider != "youtube")
                {
                    var tracks = catalog.Search(null);
                    var index = Array.FindIndex(tracks, track => track.Id == seed?.Id);
                    upcoming.Add(tracks[(index + 1) % tracks.Length]);
                    continue;
                }
            }
            await EnsureUpcomingAsync(seed, token);
        }
    }

    private async Task ChangeAsync(Track? selected, int direction, string? requestId, CancellationToken token,
        string? expectedId = null, long? expectedRevision = null, string? clientId = null, long? commandSequence = null,
        string? navigationId = null, long? navigationOffset = null, Track? anchor = null, long? automaticFromRevision = null)
    {
        CancellationTokenSource own;
        SelectionPlan selectionPlan;
        long currentVersion, targetPosition;
        bool completed = false;
        lock (gate)
        {
            if (automaticFromRevision.HasValue && (!engine.RadioEnabled || pending is not null ||
                engine.Revision != automaticFromRevision || engine.NaturallyEndedRevision != automaticFromRevision))
                throw new SelectionSupersededException();
            AcceptCommand(clientId, commandSequence);
            if (expectedRevision.HasValue && expectedRevision != engine.Revision) throw new SelectionSupersededException();
            if ((navigationId is null) != (navigationOffset is null) || navigationId?.Length is 0 or > 64)
                throw new ProviderException("Некорректная команда переключения.");
            if (direction == 0 || (anchor is not null && plan?.GroupId != navigationId && (plan?.At(plan.Position) ?? engine.Current)?.Id != anchor.Id))
            {
                CancelRadio(clear: true); preparer?.Schedule(null, () => false);
                var prefix = history.Take(cursor + 1).ToList();
                var count = prefix.Count;
                var initial = direction == 0 ? selected! : anchor!;
                if (prefix.LastOrDefault()?.Id != initial.Id) prefix.Add(initial);
                plan = new(prefix, count, prefix.Count, prefix.Count - 1);
            }
            else if (plan is null)
            {
                plan = new(history.Concat(upcoming), history.Count, history.Count, cursor);
                plan.RetryQueue = upcoming.ToArray();
                upcoming.Clear();
                if (plan.Tracks.Count == 0 && engine.Current is { } current)
                { plan.Tracks.Add(current); plan.Position = 0; }
            }
            selectionPlan = plan!;
            if (navigationId is not null && selectionPlan.GroupId != navigationId)
            { selectionPlan.GroupId = navigationId; selectionPlan.GroupOrigin = selectionPlan.Position; }
            targetPosition = navigationId is null ? selectionPlan.Position + direction : checked(selectionPlan.GroupOrigin + navigationOffset!.Value);
            if (targetPosition < selectionPlan.Start) throw new ProviderException("В истории пока нет предыдущей песни.");
            selected = selectionPlan.At(targetPosition);
            if (expectedId is not null && selected is not null && selected.Id != expectedId) throw new SelectionSupersededException();
            selectionPlan.Position = targetPosition; // Advance before any network/audio await.
            var previous = pending;
            pending = own = CancellationTokenSource.CreateLinkedTokenSource(token);
            pendingRequestId = requestId ??= Guid.NewGuid().ToString("N");
            automaticPending = automaticFromRevision.HasValue; radioError = null;
            if (direction == 0) engine.RadioEnabled = selected?.Provider == "youtube";
            currentVersion = ++version;
            // Cancellation can run the previous task's finally synchronously.
            // Publish its replacement first so it cannot roll back this plan.
            previous?.Cancel();
            preparer?.CancelUnless(selected?.Id);
        }
        try
        {
            await EnsurePlanAsync(selectionPlan, targetPosition, own.Token);
            lock (gate)
            {
                own.Token.ThrowIfCancellationRequested();
                if (plan != selectionPlan || currentVersion != version) throw new SelectionSupersededException();
                selected = selectionPlan.At(targetPosition)!;
                if (expectedId is not null && selected.Id != expectedId) throw new SelectionSupersededException();
                if (!automaticFromRevision.HasValue) engine.RadioEnabled = selected.Provider == "youtube";
                preparer?.CancelUnless(selected.Id);
            }
            await engine.PlayAsync(selected, requestId, own.Token, automaticFromRevision);
            lock (gate)
            {
                own.Token.ThrowIfCancellationRequested();
                if (currentVersion != version) throw new SelectionSupersededException();
                var remaining = selectionPlan.After(Math.Max(targetPosition, selectionPlan.HistoryCount - 1)).Concat(upcoming).ToArray();
                var heard = targetPosition < selectionPlan.CommittedPosition
                    ? cursor > 0 ? history.FindLastIndex(cursor - 1, cursor, t => t.Id == selected.Id) : -1
                    : targetPosition > selectionPlan.CommittedPosition
                        ? history.FindIndex(Math.Min(history.Count, cursor + 1), t => t.Id == selected.Id)
                        : cursor >= 0 && history[cursor].Id == selected.Id ? cursor : -1;
                if (heard >= 0) cursor = heard;
                else if (cursor < 0 || history[cursor].Id != selected.Id)
                {
                    if (cursor + 1 < history.Count) history.RemoveRange(cursor + 1, history.Count - cursor - 1);
                    history.Add(selected);
                    if (history.Count > 64) history.RemoveAt(0);
                    cursor = history.Count - 1;
                }
                upcoming.Clear();
                selectionPlan.CommittedPosition = targetPosition;
                selectionPlan.RetryQueue = remaining.Take(12).ToArray();
                if (navigationId is null) { upcoming.AddRange(remaining); plan = null; }
                completed = true;
                preparer?.Schedule(plan?.After(targetPosition).FirstOrDefault() ?? history.Skip(cursor + 1).Concat(upcoming).FirstOrDefault(), () => engine.BackgroundReady);
            }
        }
        catch (OperationCanceledException) when (own.IsCancellationRequested) { throw new SelectionSupersededException(); }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(pending, own))
                {
                    if (!completed && plan == selectionPlan) RestoreQueue();
                    pending = null; pendingRequestId = null; automaticPending = false;
                }
                own.Dispose();
            }
        }
    }

    private void RestoreQueue()
    {
        if (plan is not { } p) return;
        var queued = p.RetryQueue.Concat(upcoming).DistinctBy(t => t.Id).Take(12).ToArray();
        upcoming.Clear(); upcoming.AddRange(queued); plan = null;
    }

    public async Task StopAsync(string? clientId = null, long? commandSequence = null)
    {
        lock (gate)
        {
            AcceptCommand(clientId, commandSequence);
            ++version; var previous = pending; pending = null; pendingRequestId = null; automaticPending = false;
            engine.RadioEnabled = false; radioError = null; previous?.Cancel();
            RestoreQueue(); CancelRadio(clear: false); preparer?.Schedule(null, () => false);
        }
        await engine.StopPlaybackAsync();
    }

    public void Dispose() { lock (gate) { pending?.Cancel(); CancelRadio(clear: false); } }
}
