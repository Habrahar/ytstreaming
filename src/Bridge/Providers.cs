namespace Bridge;

// Catalog and playback resolution are separate contracts. A future catalog
// adapter must not assume that metadata grants permission/access to audio.
public record Track(string Id, string Title, string Artist, string Provider, string? Duration = null, string? Thumbnail = null);
public record AudioSource(string Location, bool Loop);
public interface IMusicProvider
{
    Track[] Search(string? query);
    Task<AudioSource> ResolveAudioAsync(Track track, CancellationToken cancellationToken = default);
}

public sealed class TestProvider : IMusicProvider
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ytstreaming-test");
    private readonly Track[] tracks =
    [
        new("melody", "Тестовая мелодия", "Генератор ytstreaming", "test"),
        new("pulse", "Тестовый пульс", "Генератор ytstreaming", "test")
    ];

    public Track[] Search(string? query) => tracks.Where(t => string.IsNullOrWhiteSpace(query) ||
        $"{t.Title} {t.Artist} {t.Id}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

    public string ResolveAudio(Track track)
    {
        if (!tracks.Any(t => t.Id == track.Id)) throw new ArgumentException("Unknown test track");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, track.Id + ".wav");
        if (File.Exists(path)) return path;
        // Original synthetic audio: no external media, account or download.
        const int rate = 44100, seconds = 16;
        using var writer = new BinaryWriter(File.Create(path));
        var samples = rate * seconds;
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);
        double[] notes = track.Id == "melody" ? [261.63, 329.63, 392, 523.25, 440, 392, 329.63, 293.66]
            : [220, 220, 330, 220, 440, 220, 330, 220];
        for (var i = 0; i < samples; i++)
        {
            var time = (double)i / rate;
            var position = time % 0.5;
            var envelope = Math.Min(position / .02, 1) * Math.Max(0, 1 - position / .48);
            var frequency = notes[(int)(time * 2) % notes.Length];
            var sample = .18 * envelope * (Math.Sin(2 * Math.PI * frequency * time) +
                .2 * Math.Sin(4 * Math.PI * frequency * time));
            writer.Write((short)(sample * short.MaxValue));
        }
        return path;
    }

    public Task<AudioSource> ResolveAudioAsync(Track track, CancellationToken cancellationToken = default) => Task.FromResult(new AudioSource(ResolveAudio(track), true));
}

public sealed class MusicCatalog(TestProvider test, YoutubeProvider youtube) : IMusicProvider
{
    public Track[] Search(string? query) => test.Search(query);
    public Track? Find(string id) => test.Search(null).FirstOrDefault(t => t.Id == id) ?? youtube.Find(id);
    public Task<AudioSource> ResolveAudioAsync(Track track, CancellationToken cancellationToken = default) => track.Provider == "youtube"
        ? youtube.ResolveAudioAsync(track, cancellationToken) : test.ResolveAudioAsync(track, cancellationToken);
}
