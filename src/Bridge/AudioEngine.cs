using System.Diagnostics;
using System.Threading.Channels;

namespace Bridge;

public sealed class AudioEngine(IMusicProvider provider, IConfiguration config, ILogger<AudioEngine> log)
    : IHostedService
{
    private readonly SemaphoreSlim control = new(1, 1);
    private readonly object listenersLock = new();
    private readonly Dictionary<Guid, Channel<byte[]>> listeners = new();
    private Process? process;
    private CancellationTokenSource? playback;
    private Task? pump;
    private long bytes;
    private long lastDataTicks;
    private volatile string state = "stopped";
    public Track? Current { get; private set; }
    public string State => state;
    public bool Ready => state == "playing" &&
        DateTime.UtcNow.Ticks - Interlocked.Read(ref lastDataTicks) < TimeSpan.FromSeconds(5).Ticks;
    public object Status
    {
        get
        {
            lock (listenersLock) return new { state, ready = Ready, track = Current,
                listeners = listeners.Count, bytesSentToHub = Interlocked.Read(ref bytes),
                stream = "/stream.mp3", provider = "test", youtubeMusic = "not-configured" };
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await PlayAsync(provider.Search(null)[0]);
    }
    public async Task StopAsync(CancellationToken cancellationToken) => await StopPlaybackAsync();

    public async Task PlayAsync(Track track)
    {
        await control.WaitAsync();
        try
        {
            await StopCoreAsync(false);
            state = "starting";
            Current = track;
            var info = new ProcessStartInfo(config["FFMPEG_PATH"] ?? "ffmpeg")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            string[] arguments = ["-hide_banner", "-loglevel", "warning", "-nostdin", "-re",
                "-stream_loop", "-1", "-i", provider.ResolveAudio(track), "-vn", "-threads", "1",
                "-c:a", "libmp3lame", "-b:a", "128k", "-ar", "44100", "-ac", "2",
                "-write_xing", "0", "-id3v2_version", "0", "-flush_packets", "1", "-f", "mp3", "pipe:1"];
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start FFmpeg");
            playback = new CancellationTokenSource();
            Interlocked.Exchange(ref bytes, 0);
            Interlocked.Exchange(ref lastDataTicks, 0);
            pump = PumpAsync(process, playback.Token);
        }
        catch
        {
            state = "error";
            CompleteListeners();
            throw;
        }
        finally { control.Release(); }
    }

    private async Task PumpAsync(Process encoder, CancellationToken token)
    {
        var stderr = ReadErrorsAsync(encoder, token);
        try
        {
            var buffer = new byte[4096];
            while (true)
            {
                var count = await encoder.StandardOutput.BaseStream.ReadAsync(buffer, token);
                if (count == 0) break;
                var chunk = buffer.AsSpan(0, count).ToArray();
                Interlocked.Add(ref bytes, count);
                Interlocked.Exchange(ref lastDataTicks, DateTime.UtcNow.Ticks);
                state = "playing";
                lock (listenersLock)
                {
                    foreach (var pair in listeners.ToArray())
                    {
                        // Disconnect slow players instead of retaining unbounded audio.
                        if (!pair.Value.Writer.TryWrite(chunk))
                        {
                            pair.Value.Writer.TryComplete();
                            listeners.Remove(pair.Key);
                        }
                    }
                }
            }
            await encoder.WaitForExitAsync(token);
            if (!token.IsCancellationRequested)
            {
                state = "error";
                log.LogError("Encoder exited unexpectedly with code {Code}", encoder.ExitCode);
                CompleteListeners();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            state = "error";
            log.LogError(ex, "Audio encoder failed");
            CompleteListeners();
        }
        finally { await stderr; }
    }

    private async Task ReadErrorsAsync(Process encoder, CancellationToken token)
    {
        try
        {
            while (await encoder.StandardError.ReadLineAsync(token) is { } line)
                log.LogWarning("FFmpeg: {Message}", line);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public (Guid Id, ChannelReader<byte[]> Reader)? Subscribe()
    {
        lock (listenersLock)
        {
            if (!Ready || listeners.Count >= 8) return null;
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true
            });
            listeners.Add(id, channel);
            return (id, channel.Reader);
        }
    }
    public void Unsubscribe(Guid id)
    {
        lock (listenersLock)
            if (listeners.Remove(id, out var channel)) channel.Writer.TryComplete();
    }
    private void CompleteListeners()
    {
        lock (listenersLock)
        {
            foreach (var channel in listeners.Values) channel.Writer.TryComplete();
            listeners.Clear();
        }
    }
    public async Task StopPlaybackAsync()
    {
        await control.WaitAsync();
        try { await StopCoreAsync(true); }
        finally { control.Release(); }
    }
    private async Task StopCoreAsync(bool disconnect)
    {
        playback?.Cancel();
        if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        if (pump is not null) await pump;
        process?.Dispose(); playback?.Dispose();
        process = null; playback = null; pump = null;
        state = "stopped";
        if (disconnect) CompleteListeners();
    }
}
