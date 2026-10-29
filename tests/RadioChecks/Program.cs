using System.Diagnostics;
using System.Text.Json;
using Bridge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static async Task Wait(Func<bool> ready, string message)
{
    var watch = Stopwatch.StartNew();
    while (!ready()) { if (watch.Elapsed.TotalSeconds > 15) throw new Exception(message); await Task.Delay(25); }
}
var directory = Path.Combine(Path.GetTempPath(), "ytstreaming-radio-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
async Task Fixture(string file, string duration, params string[] encoding)
{
    var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
    foreach (var arg in new[] { "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", duration, "-ac", "2" }.Concat(encoding).Append(Path.Combine(directory, file))) info.ArgumentList.Add(arg);
    using var process = Process.Start(info)!;
    var errors = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
    Check(process.ExitCode == 0, errors);
}
try
{
    await Fixture("short.m4a", "1.5", "-c:a", "aac", "-b:a", "128k");
    await Fixture("long.m4a", "6", "-c:a", "aac", "-b:a", "128k");
    await Fixture("long.wav", "6", "-c:a", "pcm_s16le");
    await File.WriteAllTextAsync(Path.Combine(directory, "broken.m4a"), "Not audio");

    async Task Scenario(Func<FixtureSource, AudioEngine, PlaybackNavigator, Task> body)
    {
        var source = new FixtureSource(directory);
        var player = new AudioEngine(source, new ConfigurationBuilder().Build(), NullLogger<AudioEngine>.Instance);
        using var preparer = new NextTrackPreparer(source);
        using var navigator = new PlaybackNavigator(player, source, source, preparer);
        using var service = new RadioPlaybackService(player, navigator, NullLogger<RadioPlaybackService>.Instance);
        source.IsFirstPlaying = () => player.Current == source.Tracks[0] && player.Ready && player.NaturallyEndedRevision < 0;
        await service.StartAsync(CancellationToken.None);
        try { await body(source, player, navigator); }
        finally
        {
            source.Release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.StopAsync(timeout.Token); await navigator.StopAsync();
        }
    }

    await Scenario(async (source, player, navigator) =>
    {
        await navigator.PlayAsync(source.Tracks[0], "radio-a");
        await Wait(() => player.Ready, "First track never became ready");
        var revision = player.Revision; var generation = player.StreamGeneration; var clock = Stopwatch.StartNew();
        var listener = player.Subscribe(revision) ?? throw new Exception("Cannot subscribe");
        await Wait(() => player.Current == source.Tracks[1] && player.Ready, "Natural EOF did not advance without a browser");
        Check(clock.Elapsed.TotalSeconds >= 1.25, "Radio advanced before the initial AAC burst had played out");
        Check(player.Revision == revision + 1 && player.StreamGeneration == generation, "Automatic transition replaced the continuous stream");
        Check(source.PreparedBeforeEnd && !listener.Reader.Completion.IsCompleted, "Next was not prepared early, or the listener received EOF");
        var bytes = 0;
        while (listener.Reader.TryRead(out var chunk)) { Check(chunk[0] == 255 && (chunk[1] & 246) == 240, "Continuous AAC lost frame alignment"); bytes += chunk.Length; }
        Check(bytes > 16000, "No audio reached the continuous listener");
        await Wait(() => player.Current == source.Tracks[2] && player.Ready, "Radio stopped after its first automatic transition");
        Check(player.Revision == revision + 2 && player.StreamGeneration == generation && !listener.Reader.Completion.IsCompleted,
            "A second automatic transition lost the original connection");
        await navigator.StopAsync();
        while (listener.Reader.TryRead(out _)) { }
        await listener.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(450);
        Check(player.State == "stopped" && !player.RadioEnabled, "Stop revived radio");
        await navigator.PlayAsync(source.Tracks[0], "no-browser");
        // All fixtures were played; the recommendation fallback can reuse B.
        await Wait(() => player.Current == source.Tracks[1] && player.Ready, "Radio depends on an open listener");
        Console.WriteLine("PASS: two natural EOF transitions, early preparation, one continuous AAC stream, real-time tail and no-browser advance");
    });

    await Scenario(async (source, player, navigator) =>
    {
        source.BlockSecond = true;
        await navigator.PlayAsync(source.Tracks[0], "manual-wins-a");
        await Wait(() => Radio(navigator).GetProperty("advancing").GetBoolean(), "Automatic selection never began");
        await navigator.PlayAsync(source.Tracks[2], "manual-wins-c");
        var revision = player.Revision; source.Release.TrySetResult();
        await Task.Delay(450);
        Check(player.Current == source.Tracks[2] && player.Revision == revision, "Late automatic source replaced manual selection");
        Console.WriteLine("PASS: manual selection cancels an automatic transition, including late resolver completion");
    });

    await Scenario(async (source, player, navigator) =>
    {
        source.BlockSecond = true;
        await navigator.PlayAsync(source.Tracks[0], "stop-wins-a");
        await Wait(() => Radio(navigator).GetProperty("advancing").GetBoolean(), "Automatic selection never began");
        var revision = player.Revision; await navigator.StopAsync(); source.Release.TrySetResult();
        await Task.Delay(450);
        Check(player.State == "stopped" && player.Revision == revision && !player.RadioEnabled, "Late radio revived Stop");
        Console.WriteLine("PASS: Stop cancels pending radio and prevents revival");
    });

    await Scenario(async (source, player, navigator) =>
    {
        source.FailSecond = true;
        await navigator.PlayAsync(source.Tracks[0], "failure-a"); await Wait(() => player.Ready, "No first audio");
        var listener = player.Subscribe(player.Revision)!.Value;
        await Wait(() => Radio(navigator).GetProperty("error").ValueKind == JsonValueKind.String, "Failed radio source was not reported");
        var calls = source.SecondCalls;
        while (listener.Reader.TryRead(out _)) { }
        await listener.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(650);
        Check(source.SecondCalls == calls && player.Current == source.Tracks[0], "Failed next source entered an automatic retry loop");
        source.FailSecond = false; await navigator.NextAsync("retry-manually");
        Check(player.Current == source.Tracks[1] && Radio(navigator).GetProperty("error").ValueKind == JsonValueKind.Null, "Manual retry did not restore radio");
        Console.WriteLine("PASS: unavailable next track closes the stream, reports an error, avoids retry loops and permits manual retry");
    });

    await Scenario(async (source, player, navigator) =>
    {
        source.SecondIsWave = true;
        await navigator.PlayAsync(source.Tracks[0], "codec-a"); await Wait(() => player.Ready, "No AAC first audio");
        var generation = player.StreamGeneration; var listener = player.Subscribe(player.Revision)!.Value;
        await Wait(() => player.Current == source.Tracks[1] && player.Ready, "Different-codec next track did not start");
        Check(player.ContentType == "audio/mpeg" && player.StreamGeneration != generation, "Different MIME was appended to an AAC response");
        while (listener.Reader.TryRead(out _)) { }
        await listener.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Console.WriteLine("PASS: incompatible audio opens a new stream instead of mixing codecs");
    });

    await Scenario(async (source, player, navigator) =>
    {
        source.BrokenFirst = true;
        await navigator.PlayAsync(source.Tracks[0], "broken-a");
        await Wait(() => player.State == "error", "Broken input did not fail");
        await Task.Delay(450);
        Check(player.NaturallyEndedRevision < 0 && source.SecondCalls == 0, "Encoder failure was treated as natural EOF");
        Console.WriteLine("PASS: encoder failure does not trigger radio");
    });
}
finally
{
    var resolved = Path.GetFullPath(directory);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(resolved).StartsWith("ytstreaming-radio-check-", StringComparison.Ordinal)) throw new Exception("Unexpected cleanup directory");
    Directory.Delete(resolved, true);
}

static JsonElement Radio(PlaybackNavigator navigator) => JsonSerializer.SerializeToElement(navigator.Status).GetProperty("radio");

sealed class FixtureSource(string directory) : IMusicProvider, ITrackRecommendations
{
    public Track[] Tracks { get; } = [new("youtube:AAAAAAAAAAA", "A", "Fixture", "youtube"), new("youtube:BBBBBBBBBBB", "B", "Fixture", "youtube"), new("youtube:CCCCCCCCCCC", "C", "Fixture", "youtube")];
    public bool BlockSecond, FailSecond, SecondIsWave, BrokenFirst, PreparedBeforeEnd;
    public int SecondCalls;
    public Func<bool> IsFirstPlaying = () => false;
    public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Track[] Search(string? query) => Tracks;
    public async Task<AudioSource> ResolveAudioAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (track == Tracks[1])
        {
            Interlocked.Increment(ref SecondCalls);
            if (IsFirstPlaying()) PreparedBeforeEnd = true;
            if (BlockSecond) await Release.Task; // Deliberately ignore cancellation.
            if (FailSecond) throw new ProviderException("Unavailable fixture");
        }
        var wave = track == Tracks[1] && SecondIsWave;
        var file = track == Tracks[0] ? BrokenFirst ? "broken.m4a" : "short.m4a" : wave ? "long.wav" : "long.m4a";
        return new(Path.Combine(directory, file), false, Codec: wave ? null : "mp4a.40.2", BitrateKbps:128, SampleRate:44100, Channels:2);
    }
    public Task<Track[]> RelatedAsync(Track track, CancellationToken cancellationToken = default) =>
        Task.FromResult(Tracks.Skip(Array.IndexOf(Tracks, track) + 1).ToArray());
}
