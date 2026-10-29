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
