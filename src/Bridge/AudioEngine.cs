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
    private long streamGeneration;
    private long naturallyEndedRevision = -1;
    private volatile bool radioEnabled;
    private int? sourceSampleRate, sourceChannels;
    private Track? pending;
    private string? latestRequestId;
    private string? activeRequestId;
    private string? failedRequestId;
    private string? lastError;
    private long resolveMilliseconds;
    private long startedAtTicks;
    private long startupMilliseconds;
    private string? sourceStrategy;
    private int sourceExtractMilliseconds;
    private int sourceProbeMilliseconds;
    private double? sourceBitrateKbps;
    private string outputCodec = "mp3";
    private bool nativeCopy;
    private long aheadBytes, maxGapMilliseconds, slowDisconnects;
    private readonly Queue<AudioPacket> replay = new();
    private int replayBytes;
    private double replaySeconds;
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
    public string ContentType { get; private set; } = "audio/mpeg";
    public long Revision => Interlocked.Read(ref revision);
    public long StreamGeneration => Interlocked.Read(ref streamGeneration);
    public long NaturallyEndedRevision => Interlocked.Read(ref naturallyEndedRevision);
    public bool RadioEnabled { get => radioEnabled; set => radioEnabled = value; }
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
                revision = Revision, streamGeneration = StreamGeneration,
                naturallyEndedRevision = NaturallyEndedRevision, requestId = activeRequestId, latestRequestId, error = lastError,
                resolveMilliseconds, startupMilliseconds,
                sourceStrategy, sourceExtractMilliseconds, sourceProbeMilliseconds,
                sourceBitrateKbps, outputCodec, outputMode = nativeCopy ? "copy" : "encode", contentType = ContentType,
                bufferedSourceBytes = Interlocked.Read(ref aheadBytes), maxOutputGapMilliseconds = Interlocked.Read(ref maxGapMilliseconds),
                slowClientDisconnects = Interlocked.Read(ref slowDisconnects),
                listeners = listeners.Count, bytesSentToHub = Interlocked.Read(ref bytes),
                stream = "/stream", provider = Current?.Provider,
                youtubeMusic = string.IsNullOrEmpty(config["YOUTUBE_PROVIDER_URL"]) ? "not-configured" : "enabled" };
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await PlayAsync(provider.Search(null)[0]);
    }
    public async Task StopAsync(CancellationToken cancellationToken) => await StopPlaybackAsync();

    public async Task PlayAsync(Track track, string? requestId = null, CancellationToken cancellationToken = default, long? automaticFromRevision = null)
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
            var copy = !source.Loop && config["AUDIO_PASSTHROUGH"] != "false" &&
                (source.Codec?.StartsWith("mp4a.", StringComparison.Ordinal) == true || source.Codec == "aac");
            var contentType = copy ? "audio/aac" : "audio/mpeg";
            // Only a natural radio transition may append to existing responses.
            // Manual selections still close them to discard the phone's old buffer.
            var continuous = automaticFromRevision == Revision && NaturallyEndedRevision == Revision && radioEnabled &&
                ContentType == contentType && (!copy || source.SampleRate is > 0 && source.Channels is > 0 &&
                    source.SampleRate == sourceSampleRate && source.Channels == sourceChannels);
            await StopCoreAsync(!continuous);
            token.ThrowIfCancellationRequested();
            state = "starting";
            Current = track;
            activeRequestId = requestId;
            Interlocked.Increment(ref revision);
            if (!continuous) Interlocked.Increment(ref streamGeneration);
            resolveMilliseconds = watch.ElapsedMilliseconds;
            sourceStrategy = source.Strategy;
            sourceExtractMilliseconds = source.ExtractMilliseconds;
            sourceProbeMilliseconds = source.ProbeMilliseconds;
            sourceBitrateKbps = source.BitrateKbps;
            nativeCopy = copy;
            sourceSampleRate = source.SampleRate; sourceChannels = source.Channels;
            outputCodec = nativeCopy ? "aac" : "mp3";
            ContentType = contentType;
            Interlocked.Exchange(ref aheadBytes, 0); Interlocked.Exchange(ref maxGapMilliseconds, 0); Interlocked.Exchange(ref slowDisconnects, 0);
            lock (listenersLock) { replay.Clear(); replayBytes = 0; replaySeconds = 0; }
            startupMilliseconds = 0;
            startedAtTicks = Stopwatch.GetTimestamp();
            var info = new ProcessStartInfo(config["FFMPEG_PATH"] ?? "ffmpeg")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            List<string> arguments = ["-hide_banner", "-loglevel", "warning", "-nostdin"];
            if (!nativeCopy) arguments.Add("-re");
            if (source.Loop) arguments.AddRange(["-stream_loop", "-1"]);
            else arguments.AddRange(["-rw_timeout", "20000000", "-probesize", "32768", "-analyzeduration", nativeCopy ? "1" : "1000000"]);
            arguments.AddRange(["-i", source.Location, "-vn", "-threads", "1"]);
            if (nativeCopy) arguments.AddRange(["-c:a", "copy", "-f", "adts"]);
            else arguments.AddRange(["-c:a", "libmp3lame", "-b:a", "192k", "-ar", "44100", "-ac", "2",
                "-write_xing", "0", "-id3v2_version", "0", "-f", "mp3"]);
            arguments.AddRange(["-flush_packets", "1", "pipe:1"]);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start FFmpeg");
            playback = new CancellationTokenSource();
            Interlocked.Exchange(ref bytes, 0);
            Interlocked.Exchange(ref lastDataTicks, 0);
            pump = PumpAsync(process, nativeCopy, playback.Token);
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

    private void Publish(byte[] chunk, double seconds)
    {
        var now = DateTime.UtcNow.Ticks;
        var previous = Interlocked.Read(ref lastDataTicks);
        if (Interlocked.Read(ref bytes) == 0) startupMilliseconds = (long)Stopwatch.GetElapsedTime(startedAtTicks).TotalMilliseconds;
        else if (previous > 0)
        {
            var gap = (now - previous) / TimeSpan.TicksPerMillisecond;
            if (gap > Interlocked.Read(ref maxGapMilliseconds)) Interlocked.Exchange(ref maxGapMilliseconds, gap);
        }
        Interlocked.Add(ref bytes, chunk.Length); Interlocked.Exchange(ref lastDataTicks, now); state = "playing";
        lock (listenersLock)
        {
            replay.Enqueue(new(chunk,seconds)); replayBytes += chunk.Length; replaySeconds += seconds;
            while ((replayBytes > 65536 || replaySeconds > 4) && replay.Count > 1)
            {
                var old = replay.Dequeue(); replayBytes -= old.Bytes.Length; replaySeconds -= old.Seconds;
            }
            foreach (var pair in listeners.ToArray())
                if (!pair.Value.Writer.TryWrite(chunk))
                {
                    Interlocked.Increment(ref slowDisconnects);
                    pair.Value.Writer.TryComplete(); listeners.Remove(pair.Key);
                }
        }
    }

    private async Task PumpAsync(Process encoder, bool copy, CancellationToken token)
    {
        var stderr = ReadErrorsAsync(encoder, token);
        try
        {
            if (copy) await NativeAacPump.RunAsync(encoder.StandardOutput.BaseStream, packet => Publish(packet.Bytes,packet.Seconds), delta => Interlocked.Add(ref aheadBytes, delta), token);
            else
            {
                var buffer = new byte[4096];
                while (true)
                {
                    var count = await encoder.StandardOutput.BaseStream.ReadAtLeastAsync(buffer, buffer.Length, false, token);
                    if (count == 0) break;
                    Publish(buffer.AsSpan(0, count).ToArray(),count / 24000.0);
                }
            }
            await encoder.WaitForExitAsync(token);
            if (!token.IsCancellationRequested)
            {
                var natural = encoder.ExitCode == 0 && Interlocked.Read(ref bytes) > 0;
                state = natural ? "stopped" : "error";
                if (encoder.ExitCode != 0) log.LogError("Encoder exited with code {Code}", encoder.ExitCode);
                else if (!natural) log.LogError("Encoder produced no audio");
                if (natural)
                    Interlocked.Exchange(ref naturallyEndedRevision, Revision);
                if (!natural || !radioEnabled || Current?.Provider != "youtube") CompleteListeners();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            state = "error";
            log.LogError(ex, "Audio encoder failed");
            CompleteListeners();
        }
        finally { Interlocked.Exchange(ref aheadBytes, 0); await stderr; }
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

    public (Guid Id, ChannelReader<byte[]> Reader, string ContentType)? Subscribe(long? expectedRevision = null)
    {
        lock (listenersLock)
        {
            if (!Ready || listeners.Count >= 8 || (expectedRevision.HasValue && expectedRevision != Revision)) return null;
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true
            });
            foreach (var chunk in replay) channel.Writer.TryWrite(chunk.Bytes);
            listeners.Add(id, channel);
            return (id, channel.Reader, ContentType);
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
    public void ReleaseEndedStream(long expectedRevision)
    {
        lock (listenersLock)
            if (Revision == expectedRevision && NaturallyEndedRevision == expectedRevision) CompleteListeners();
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
        Interlocked.Exchange(ref naturallyEndedRevision, -1);
        playback?.Cancel();
        if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        if (pump is not null) await pump;
        process?.Dispose(); playback?.Dispose();
        process = null; playback = null; pump = null;
        state = "stopped";
        if (disconnect) CompleteListeners();
    }
}
