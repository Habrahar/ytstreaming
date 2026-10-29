using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Bridge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
var startupSamples = new StartupMeasurements();
Check(!startupSamples.Add(new("", 1, 2, null, 0)) && !startupSamples.Add(new("request", 1, -1, null, 0)) &&
      !startupSamples.Add(new("request", 1, 2, null, double.NaN)), "Invalid startup sample accepted");
for (var i = 0; i < 20; i++) Check(startupSamples.Add(new(i.ToString(), 10, 30, 20, 3)), "Valid timing rejected");
Check(startupSamples.Snapshot.Length == 16 && startupSamples.Snapshot[0].RequestId == "4", "Startup samples are not bounded");
var snapshot = startupSamples.Snapshot; snapshot[0] = new("changed", 0, 0, null, 0);
Check(startupSamples.Snapshot[0].RequestId == "4", "Startup snapshot exposed mutable storage");
Console.WriteLine("PASS: startup samples validate numeric fields, retain only 16 entries and expose a snapshot");
static byte[] Frames(int count, int size = 220)
{
    var data = new byte[count * size];
    for (var n = 0; n < count; n++)
    {
        var frame = data.AsSpan(n * size, size);
        frame.Fill((byte)(n % 230)); frame[0] = 255; frame[1] = 241; frame[2] = 80;
        frame[3] = (byte)(128 | (size >> 11)); frame[4] = (byte)(size >> 3);
        frame[5] = (byte)(((size & 7) << 5) | 31); frame[6] = 252;
    }
    return data;
}
var original = Frames(500);
var framer = new AdtsPacketizer();
var packets = new List<AudioPacket>();
for (var i = 0; i < original.Length;)
{
    var size = Math.Min(original.Length - i, i % 397 + 1);
    packets.AddRange(framer.Append(original.AsSpan(i, size))); i += size;
}
if (framer.Finish() is { } tail) packets.Add(tail);
Check(original.SequenceEqual(packets.SelectMany(p => p.Bytes)), "AAC payload or frame boundaries changed");
Check(Math.Abs(packets.Sum(p => p.Seconds) - 500 * 1024.0 / 44100) < .00001, "AAC pacing clock is inaccurate");
Check(packets.All(p => p.Bytes[0] == 255 && p.Bytes[1] == 241), "A listener could start in the middle of an AAC frame");
var silence = new AdtsPacketizer();
var silentPackets = new List<AudioPacket>();
var silentBytes = Frames(500, 20);
for (var i = 0; i < silentBytes.Length; i += 1000) silentPackets.AddRange(silence.Append(silentBytes.AsSpan(i, Math.Min(1000, silentBytes.Length - i))));
if (silence.Finish() is { } silentTail) silentPackets.Add(silentTail);
Check(silentPackets.All(p => p.Seconds < .25) && silentPackets.SelectMany(p => p.Bytes).SequenceEqual(silentBytes), "Quiet AAC accumulated seconds before its first packet");
var broken = new AdtsPacketizer(); broken.Append(original.AsSpan(0, 11));
try { broken.Finish(); throw new Exception("Truncated frame accepted"); } catch (InvalidDataException) { }
try { new AdtsPacketizer().Append(new byte[8]); throw new Exception("Invalid header accepted"); } catch (InvalidDataException) { }
Console.WriteLine("PASS: AAC bytes preserved across split reads, valid frame boundaries and exact sample timing");

var clock = Stopwatch.StartNew(); var sent = new List<byte[]>();
await NativeAacPump.RunAsync(new MemoryStream(Frames(200)), packet => sent.Add(packet.Bytes), _ => { }, CancellationToken.None, 8);
Check(sent.SelectMany(b => b).SequenceEqual(Frames(200)), "Paced output changed AAC bytes");
Check(clock.Elapsed.TotalSeconds >= 4.5 && clock.Elapsed.TotalSeconds < 6, "EOF did not wait for the initial audio burst to play out");
long buffered = 0, maximum = 0;
using (var stop = new CancellationTokenSource())
{
    try
    {
        await NativeAacPump.RunAsync(new MemoryStream(Frames(20000, 519)), _ => stop.Cancel(), n =>
        {
            var amount = Interlocked.Add(ref buffered, n); maximum = Math.Max(maximum, amount);
        }, stop.Token);
        throw new Exception("Native read-ahead ignored cancellation");
    }
    catch (OperationCanceledException) { }
}
Check(maximum <= 2 * 1024 * 1024 + 4096 && maximum >= 2 * 1024 * 1024, "Read-ahead is not bounded to 2 MiB");
Console.WriteLine("PASS: initial burst, real-time pacing through EOF, bounded compressed reserve and prompt cancellation");
using (var burstStop = new CancellationTokenSource())
{
    var burstClock = Stopwatch.StartNew(); double burstAudio = 0;
    try
    {
        await NativeAacPump.RunAsync(new MemoryStream(Frames(1000)), packet =>
        {
            burstAudio += packet.Seconds;
            if (burstAudio >= 8) burstStop.Cancel();
        }, _ => { }, burstStop.Token, 8);
    }
    catch (OperationCanceledException) { }
    Check(burstAudio >= 8 && burstClock.Elapsed.TotalSeconds < 1, "Eight-second initial audio was delayed instead of sent immediately");
}
Console.WriteLine("PASS: larger initial decoder burst is sent immediately without waiting eight seconds");

var handler = new DelayedHandler();
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["YOUTUBE_PROVIDER_URL"] = "http://fixture" }).Build();
var youtube = new YoutubeProvider(configuration, new HttpClient(handler));
var music = new Music(youtube);
var track = new Track("youtube:AAAAAAAAAAA", "A", "Fixture", "youtube");
using (var warm = new NextTrackPreparer(music))
{
    var ready = false; warm.Schedule(track, () => ready);
    await Task.Delay(150); Check(handler.Calls == 0, "Prefetch contended with current startup");
    ready = true;
    await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var foreground = youtube.ResolveAudioAsync(track);
    warm.Schedule(null, () => false);
    handler.Release.TrySetResult();
    var resolved = await foreground;
    Check(handler.Calls == 1 && resolved.Codec == "mp4a.40.2" && resolved.BitrateKbps == 129.5,
        "Foreground Next repeated extraction or lost source quality metadata");
}
handler.Reset();
using (var cancel = new CancellationTokenSource())
{
    var cancelled = youtube.ResolveAudioAsync(track, cancel.Token);
    await handler.Entered.Task; cancel.Cancel();
    try { await cancelled; throw new Exception("Last listener cancellation ignored"); } catch (OperationCanceledException) { }
    await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
}
Console.WriteLine("PASS: one shared warm/foreground extraction, current audio priority, quality metadata and last-reader cancellation");

if (args.Contains("--ffmpeg"))
{
    var temporary = Path.Combine(Path.GetTempPath(), "ytstreaming-audio-check-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temporary);
    AudioEngine? player = null;
    try
    {
        var source = new TestProvider();
        var wave = source.ResolveAudio(source.Search(null)[0]);
        var native = Path.Combine(temporary, "fixture.m4a");
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-v", "error", "-i", wave, "-t", "8", "-c:a", "aac", "-b:a", "128k", native }) info.ArgumentList.Add(argument);
        using (var process = Process.Start(info)!)
        {
            var errors = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
            Check(process.ExitCode == 0, errors);
        }
        var nativeProvider = new NativeFixture(native);
        player = new(nativeProvider, new ConfigurationBuilder().Build(), NullLogger<AudioEngine>.Instance);
        await player.PlayAsync(track, "native-fixture");
        for (var i = 0; !player.Ready && i < 100; i++) await Task.Delay(30);
        Check(player.Ready, "AAC copy never became ready");
        await Task.Delay(400);
        Check(!player.BackgroundReady, "Background preparation started at the first packet");
        player.ConfirmBrowserPlayback("old-request");
        Check(!player.BackgroundReady, "Old browser confirmation released a new selection");
        player.ConfirmBrowserPlayback("native-fixture");
        Check(player.BackgroundReady, "Actual browser start did not release background work");
        var listener = player.Subscribe(player.Revision) ?? throw new Exception("Cannot subscribe to native AAC");
        Check(listener.ContentType == "audio/aac", "Native stream advertised as MP3");
        var burst = 0;
        while (listener.Reader.TryRead(out var chunk)) { Check(chunk[0] == 255 && chunk[1] == 241, "Replay begins mid-frame"); burst++; }
        Check(burst >= 2, "New listener did not get the warm replay buffer");
        var state = JsonSerializer.SerializeToElement(player.Status);
        Check(state.GetProperty("outputMode").GetString() == "copy" && state.GetProperty("outputCodec").GetString() == "aac", "AAC was re-encoded");
        await player.PlayAsync(track, "new-native-fixture");
        for (var i = 0; !player.Ready && i < 100; i++) await Task.Delay(30);
        Check(player.Ready && !player.BackgroundReady, "Manual selection inherited the previous startup confirmation");
        player.ConfirmBrowserPlayback("native-fixture");
        Check(!player.BackgroundReady, "Late playing report unlocked a later selection");
        await Task.Delay(5150);
        Check(player.BackgroundReady, "A client without timing reports could never prepare radio");
        await player.StopPlaybackAsync();
        Check(!player.BackgroundReady, "Stop left background preparation unlocked");
        while (listener.Reader.TryRead(out _)) { }
        await listener.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Console.WriteLine("PASS: real FFmpeg AAC passthrough, correct MIME, frame-aligned replay burst and Stop");
    }
    finally
    {
        if (player is not null) await player.StopPlaybackAsync();
        var resolved = Path.GetFullPath(temporary);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("ytstreaming-audio-check-", StringComparison.Ordinal)) throw new Exception("Unexpected cleanup target");
        Directory.Delete(resolved, true);
    }
}

sealed class DelayedHandler : HttpMessageHandler
{
    public int Calls;
    public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Reset() { Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously); Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Interlocked.Increment(ref Calls); Entered.TrySetResult();
        try { await Release.Task.WaitAsync(token); }
        catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
        return new(HttpStatusCode.OK) { Content = new StringContent("{\"ready\":true,\"strategy\":\"visionos\",\"codec\":\"mp4a.40.2\",\"bitrateKbps\":129.5,\"sampleRate\":44100,\"channels\":2}", Encoding.UTF8, "application/json") };
    }
}
sealed class Music(YoutubeProvider youtube) : IMusicProvider
{
    public Track[] Search(string? query) => [];
    public Task<AudioSource> ResolveAudioAsync(Track track, CancellationToken token = default) => youtube.ResolveAudioAsync(track, token);
}
sealed class NativeFixture(string file) : IMusicProvider
{
    public Track[] Search(string? query) => [];
    public Task<AudioSource> ResolveAudioAsync(Track track, CancellationToken token = default) => Task.FromResult(new AudioSource(file, false, Codec: "mp4a.40.2", BitrateKbps: 128));
}
