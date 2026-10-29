using System.Diagnostics;
using System.Threading.Channels;

namespace Bridge;

public static class NativeAacPump
{
    public static async Task RunAsync(Stream source, Action<AudioPacket> publish, Action<long> buffered, CancellationToken token, double startupSeconds = 3)
    {
        if (!double.IsFinite(startupSeconds) || startupSeconds is < 3 or > 8) throw new ArgumentOutOfRangeException(nameof(startupSeconds));
        using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Compressed bytes only, at most 2 MiB plus one input block. No audio file.
        var ahead = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512) { SingleReader = true, SingleWriter = true });
        var producer = ReadAheadAsync();
        async Task ReadAheadAsync()
        {
            try
            {
                while (true)
                {
                    var buffer = new byte[4096];
                    var count = await source.ReadAtLeastAsync(buffer, buffer.Length, false, inputCancellation.Token);
                    if (count == 0) break;
                    var chunk = count == buffer.Length ? buffer : buffer[..count];
                    buffered(chunk.Length);
                    try { await ahead.Writer.WriteAsync(chunk, inputCancellation.Token); }
                    catch { buffered(-chunk.Length); throw; }
                }
                ahead.Writer.TryComplete();
            }
            catch (OperationCanceledException) { ahead.Writer.TryComplete(); }
            catch (Exception ex) { ahead.Writer.TryComplete(ex); }
        }
        var packets = new AdtsPacketizer();
        var clock = new Stopwatch();
        double emitted = 0;
        async Task SendAsync(AudioPacket packet)
        {
            if (!clock.IsRunning) clock.Start();
            // Fill the phone's decoder once, then pace by exact AAC sample counts.
            var delay = emitted - startupSeconds - clock.Elapsed.TotalSeconds;
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), token);
            token.ThrowIfCancellationRequested(); publish(packet); emitted += packet.Seconds;
        }
        try
        {
            await foreach (var chunk in ahead.Reader.ReadAllAsync(token))
            {
                buffered(-chunk.Length);
                foreach (var packet in packets.Append(chunk)) await SendAsync(packet);
            }
            if (packets.Finish() is { } final) await SendAsync(final);
            // The initial decoder burst is ahead of wall time. Let its tail play
            // before EOF starts another song, or each radio track would add lead.
            var tail = emitted - clock.Elapsed.TotalSeconds;
            if (tail > 0) await Task.Delay(TimeSpan.FromSeconds(tail), token);
        }
        finally { inputCancellation.Cancel(); await producer; }
    }
}
