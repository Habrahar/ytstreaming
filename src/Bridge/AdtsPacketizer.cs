namespace Bridge;

public record AudioPacket(byte[] Bytes, double Seconds);

// Preserve complete ADTS frames, including their original AAC payloads.
public sealed class AdtsPacketizer
{
    private static readonly int[] Rates = [96000,88200,64000,48000,44100,32000,24000,22050,16000,12000,11025,8000,7350];
    private readonly byte[] pending = new byte[16384];
    private int count;
    private readonly MemoryStream batch = new();
    private double seconds;
    public List<AudioPacket> Append(ReadOnlySpan<byte> bytes)
    {
        if (count + bytes.Length > pending.Length) throw new InvalidDataException("Oversized AAC input block");
        bytes.CopyTo(pending.AsSpan(count)); count += bytes.Length;
        var packets = new List<AudioPacket>();
        var offset = 0;
        while (count - offset >= 7)
        {
            var header = pending.AsSpan(offset, 7);
            var rate = (header[2] >> 2) & 15;
            var length = ((header[3] & 3) << 11) | (header[4] << 3) | (header[5] >> 5);
            if (header[0] != 255 || (header[1] & 246) != 240 || rate >= Rates.Length || length < ((header[1] & 1) != 0 ? 7 : 9))
                throw new InvalidDataException("Invalid AAC frame");
            if (count - offset < length) break;
            batch.Write(pending, offset, length);
            seconds += 1024.0 * ((header[6] & 3) + 1) / Rates[rate];
            offset += length;
            if (batch.Length >= 4096 || seconds >= .2) packets.Add(Take());
        }
        pending.AsSpan(offset, count - offset).CopyTo(pending); count -= offset;
        return packets;
    }
    public AudioPacket? Finish()
    {
        if (count != 0) throw new InvalidDataException("Truncated AAC frame");
        return batch.Length == 0 ? null : Take();
    }
    private AudioPacket Take()
    {
        var packet = new AudioPacket(batch.ToArray(), seconds);
        batch.SetLength(0); batch.Position = 0; seconds = 0;
        return packet;
    }
}
