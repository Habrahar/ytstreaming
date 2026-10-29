using Bridge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

var provider = new DelayedProvider();
var engine = new AudioEngine(provider, new ConfigurationBuilder().Build(), NullLogger<AudioEngine>.Instance);
async Task Ready()
{
    for (var i = 0; i < 200; i++) { if (engine.Ready) return; await Task.Delay(50); }
    throw new Exception("Encoder did not start");
}
void Check(bool value, string message) { if (!value) throw new Exception(message); }
try
{
    var old = engine.PlayAsync(provider.Tracks[0], "old");
    await provider.Entered.Task;
    await engine.PlayAsync(provider.Tracks[1], "new");
    await Ready();
    var revision = engine.Revision;
    provider.Release.SetResult();
    try { await old; throw new Exception("Old selection was accepted"); } catch (SelectionSupersededException) { }
    Check(engine.Current?.Id == "pulse" && engine.Revision == revision, "Late selection replaced the new song");
    var listener = engine.Subscribe(revision) ?? throw new Exception("Cannot subscribe");
    await engine.PlayAsync(provider.Tracks[1], "another");
    while (listener.Reader.TryRead(out _)) { }
    await listener.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    Check(engine.Subscribe(revision) is null, "Old revision accepted a new subscriber");
    provider.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    provider.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var pending = engine.PlayAsync(provider.Tracks[0], "cancel-on-stop");
    await provider.Entered.Task;
    await engine.StopPlaybackAsync();
    provider.Release.SetResult();
    try { await pending; throw new Exception("Stop did not cancel selection"); } catch (SelectionSupersededException) { }
    Check(engine.State == "stopped", "Late preparation revived stopped playback");
    Console.WriteLine("PASS: latest selection wins, old response closes, stale revision rejected, stop cancels preparation");
}
finally { await engine.StopPlaybackAsync(); }

await NavigationChecks();

static async Task NavigationChecks()
{
    var source = new NavigationSource();
    var player = new AudioEngine(source, new ConfigurationBuilder().Build(), NullLogger<AudioEngine>.Instance);
    var related = new FakeRecommendations(source.Tracks);
    var navigation = new PlaybackNavigator(player, source, related);
    void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    try
    {
        await navigation.PlayAsync(source.Tracks[0], "a");
        await navigation.PlayAsync(source.Tracks[1], "b");
        await navigation.PreviousAsync("previous");
        Assert(player.Current?.Id == source.Tracks[0].Id, "Previous missed actual history");
        await navigation.NextAsync("forward");
        Assert(player.Current?.Id == source.Tracks[1].Id && related.Calls == 0, "Forward history fetched unrelated music");
        source.FailThird = true;
        try { await navigation.NextAsync("failed"); throw new Exception("Failure accepted"); } catch (ProviderException) { }
        Assert(player.Current?.Id == source.Tracks[1].Id, "Failed Next replaced current song");
        var status = System.Text.Json.JsonSerializer.SerializeToElement(navigation.Status);
        Assert(status.GetProperty("count").GetInt32() == 2 && status.GetProperty("position").GetInt32() == 1,
            "Failed Next modified history");
        source.FailThird = false;
        related.Delay = true;
        var late = navigation.NextAsync("late-radio");
        await related.Entered.Task;
        await navigation.PlayAsync(source.Tracks[0], "manual");
        var revision = player.Revision;
        related.Release.SetResult();
        try { await late; throw new Exception("Old radio accepted"); } catch (SelectionSupersededException) { }
        Assert(player.Current?.Id == source.Tracks[0].Id && player.Revision == revision, "Late radio overrode manual choice");
        Console.WriteLine("PASS: previous/forward history, failed Next preserves song/history, late radio cannot override manual selection");
    }
    finally { await navigation.StopAsync(); }
}

sealed class DelayedProvider : IMusicProvider
{
    private readonly TestProvider source = new();
    public Track[] Tracks => source.Search(null);
    public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Track[] Search(string? query) => source.Search(query);
    public async Task<AudioSource> ResolveAudioAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (track.Id == "melody") { Entered.TrySetResult(); await Release.Task; }
        // Deliberately ignore cancellation: an external operation may complete late.
        return await source.ResolveAudioAsync(track);
    }
}

sealed class NavigationSource : IMusicProvider
{
    private readonly TestProvider source = new();
    public bool FailThird;
    public Track[] Tracks { get; } = [new("youtube:AAAAAAAAAAA", "A", "Test", "youtube"),
        new("youtube:BBBBBBBBBBB", "B", "Test", "youtube"), new("youtube:CCCCCCCCCCC", "C", "Test", "youtube")];
    public Track[] Search(string? query) => Tracks;
    public Task<AudioSource> ResolveAudioAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (FailThird && track.Id == Tracks[2].Id) throw new ProviderException("Unavailable fixture");
        return source.ResolveAudioAsync(source.Search(null)[1], cancellationToken);
    }
}

sealed class FakeRecommendations(Track[] tracks) : ITrackRecommendations
{
    public bool Delay;
    public int Calls;
    public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<Track[]> RelatedAsync(Track track, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Delay) { Entered.TrySetResult(); await Release.Task; }
        return tracks;
    }
}
