using System.Diagnostics;
using System.Threading.Channels;

namespace Bridge;

public sealed class SelectionSupersededException : Exception { }

public sealed class AudioEngine(IMusicProvider provider, IConfiguration config, ILogger<AudioEngine> log)
    : IHostedService
{
    private readonly SemaphoreSlim control = new(1, 1);
    private readonly object selectionLock = new();
    private CancellationTokenSource? selection;
    private long selectionVersion;
    private long revision;
    private Track? pending;
    private string? latestRequestId;
    private string? activeRequestId;
    private string? failedRequestId;
    private string? lastError;
    private long resolveMilliseconds;
    private long startedAtTicks;
    private long startupMilliseconds;
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
    public long Revision => Interlocked.Read(ref revision);
    public string? ActiveRequestId => activeRequestId;
    public string? LatestRequestId => latestRequestId;
    public string? FailedRequestId => failedRequestId;
    public bool Ready => state == "playing" &&
        DateTime.UtcNow.Ticks - Interlocked.Read(ref lastDataTicks) < TimeSpan.FromSeconds(5).Ticks;
    public object Status
    {
        get
        {
            lock (listenersLock) return new { state, ready = Ready, track = Current, pending,
                revision = Revision, requestId = activeRequestId, latestRequestId, error = lastError,
                resolveMilliseconds, startupMilliseconds,
                listeners = listeners.Count, bytesSentToHub = Interlocked.Read(ref bytes),
                stream = "/stream.mp3", provider = Current?.Provider,
                youtubeMusic = string.IsNullOrEmpty(config["YOUTUBE_PROVIDER_URL"]) ? "not-configured" : "enabled" };
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await PlayAsync(provider.Search(null)[0]);
    }
    public async Task StopAsync(CancellationToken cancellationToken) => await StopPlaybackAsync();

    public async Task PlayAsync(Track track, string? requestId = null, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource ownSelection;
        long version;
        lock (selectionLock)
        {
            selection?.Cancel();
            ownSelection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            selection = ownSelection;
            version = ++selectionVersion;
            latestRequestId = requestId ?? Guid.NewGuid().ToString("N");
            requestId = latestRequestId;
            pending = track;
            lastError = null;
            failedRequestId = null;
        }
        var token = ownSelection.Token;
        var watch = Stopwatch.StartNew();
        var entered = false;
        try
        {
            // Prepare/validate remote audio while the previous shared stream keeps playing.
            var source = await provider.ResolveAudioAsync(track, token);
            token.ThrowIfCancellationRequested();
            await control.WaitAsync(token);
            entered = true;
            token.ThrowIfCancellationRequested();
            // Never mix tracks in an existing MP3 response: browsers retain old audio.
            await StopCoreAsync(true);
            token.ThrowIfCancellationRequested();
            state = "starting";
            Current = track;
            activeRequestId = requestId;
            Interlocked.Increment(ref revision);
            resolveMilliseconds = watch.ElapsedMilliseconds;
            startupMilliseconds = 0;
            startedAtTicks = Stopwatch.GetTimestamp();
            var info = new ProcessStartInfo(config["FFMPEG_PATH"] ?? "ffmpeg")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            List<string> arguments = ["-hide_banner", "-loglevel", "warning", "-nostdin", "-re"];
            if (source.Loop) arguments.AddRange(["-stream_loop", "-1"]);
            else arguments.AddRange(["-rw_timeout", "20000000", "-probesize", "65536", "-analyzeduration", "1000000"]);
            arguments.AddRange(["-i", source.Location, "-vn", "-threads", "1",
                "-c:a", "libmp3lame", "-b:a", "128k", "-ar", "44100", "-ac", "2",
                "-write_xing", "0", "-id3v2_version", "0", "-flush_packets", "1", "-f", "mp3", "pipe:1"]);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start FFmpeg");
            playback = new CancellationTokenSource();
            Interlocked.Exchange(ref bytes, 0);
            Interlocked.Exchange(ref lastDataTicks, 0);
            pump = PumpAsync(process, playback.Token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new SelectionSupersededException();
        }
        catch (ProviderException ex)
        {
            lock (selectionLock)
                if (version == selectionVersion) { lastError = ex.Message; failedRequestId = requestId; }
            throw;
        }
        catch
        {
            if (token.IsCancellationRequested) throw new SelectionSupersededException();
            state = "error";
            CompleteListeners();
            throw;
        }
        finally
        {
            if (entered) control.Release();
            lock (selectionLock)
            {
                if (ReferenceEquals(selection, ownSelection)) { selection = null; pending = null; }
                ownSelection.Dispose();
            }
        }
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
                if (Interlocked.Read(ref bytes) == 0)
                    startupMilliseconds = (long)Stopwatch.GetElapsedTime(startedAtTicks).TotalMilliseconds;
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
                state = encoder.ExitCode == 0 ? "stopped" : "error";
                if (encoder.ExitCode != 0) log.LogError("Encoder exited with code {Code}", encoder.ExitCode);
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

    public (Guid Id, ChannelReader<byte[]> Reader)? Subscribe(long? expectedRevision = null)
    {
        lock (listenersLock)
        {
            if (!Ready || listeners.Count >= 8 || (expectedRevision.HasValue && expectedRevision != Revision)) return null;
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
        lock (selectionLock)
        {
            ++selectionVersion;
            selection?.Cancel();
            pending = null;
            latestRequestId = null;
        }
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
