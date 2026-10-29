using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bridge;

public sealed class LibraryException(string message) : Exception(message);
public sealed class LibraryUnavailableException : Exception;
public record LibraryEntry(Track Track, string Url, DateTimeOffset SavedAt);
public record LibraryPlaylist(string Id, string Name, string[] TrackIds, DateTimeOffset CreatedAt);
public record LibrarySnapshot(long Revision, LibraryEntry[] Tracks, string[] LikedIds, LibraryPlaylist[] Playlists);

// One shared local library. Publish a new in-memory state only after an atomic
// replacement on disk succeeds; failed writes must never look like saved likes.
public sealed class MusicLibrary
{
    private const int MaxTracks = 2000, MaxPlaylists = 64;
    private readonly object gate = new();
    private readonly string file;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private Document document;
    private sealed record Document(int Version, long Revision, Dictionary<string, LibraryEntry> Tracks,
        string[] LikedIds, LibraryPlaylist[] Playlists);

    public MusicLibrary(IConfiguration configuration)
    {
        var directory = configuration["LIBRARY_DIRECTORY"] ?? Path.Combine(Directory.GetCurrentDirectory(), ".local", "library");
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "library.json");
        document = new(1, 0, new(), [], []);
        if (!File.Exists(file)) return;
        // Preserve a damaged or incompatible file and fail instead of silently
        // starting an empty library that would overwrite the user's collection.
        if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("Library file is too large");
        document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(file), json)
            ?? throw new InvalidDataException("Empty library file");
        if (document.Version != 1 || document.Revision < 0 || document.Tracks is null ||
            document.LikedIds is null || document.Playlists is null || document.Tracks.Count > MaxTracks ||
            document.Playlists.Length > MaxPlaylists) throw new InvalidDataException("Invalid library document");
        foreach (var (id, entry) in document.Tracks)
        {
            if (entry is null || entry.Track is null || entry.Track.Id != id || !ValidTrack(entry.Track) || entry.Url != TrackUrl(id))
                throw new InvalidDataException("Invalid saved track");
        }
        bool ValidIds(string[]? ids) => ids is not null && ids.Length <= MaxTracks &&
            ids.Distinct().Count() == ids.Length && ids.All(document.Tracks.ContainsKey);
        if (!ValidIds(document.LikedIds) || document.Playlists.Any(p => p is null ||
            !Guid.TryParseExact(p.Id, "N", out _) || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 60 || !ValidIds(p.TrackIds)) ||
            document.Playlists.Select(p => p.Id).Distinct().Count() != document.Playlists.Length)
            throw new InvalidDataException("Invalid library references");
    }

    private static bool ValidTrack(Track track) => track.Provider == "youtube" &&
        Regex.IsMatch(track.Id ?? "", @"^youtube:[a-zA-Z0-9_-]{11}$") &&
        !string.IsNullOrWhiteSpace(track.Title) && track.Title.Length <= 512 &&
        track.Artist is not null && track.Artist.Length <= 512 &&
        (track.Duration?.Length ?? 0) <= 32 && (track.Thumbnail?.Length ?? 0) <= 2048;
    private static string TrackUrl(string id) => "https://music.youtube.com/watch?v=" + id[8..];

    public Track? Find(string id) { lock (gate) return document.Tracks.GetValueOrDefault(id)?.Track; }
    public long Revision { get { lock (gate) return document.Revision; } }
    public LibrarySnapshot Snapshot { get { lock (gate) return SnapshotOf(document); } }
    private static LibrarySnapshot SnapshotOf(Document value) => new(value.Revision,
        value.Tracks.Values.OrderByDescending(t => t.SavedAt).ToArray(), value.LikedIds.ToArray(),
        value.Playlists.Select(p => p with { TrackIds = p.TrackIds.ToArray() }).ToArray());

    public LibrarySnapshot Like(Track track, bool liked)
    {
        lock (gate)
        {
            CheckTrack(track);
            if (document.LikedIds.Contains(track.Id) == liked) return SnapshotOf(document);
            var next = WithTrack(document, track);
            next = next with { LikedIds = liked ? [track.Id, ..next.LikedIds] : next.LikedIds.Where(id => id != track.Id).ToArray() };
            return Commit(next);
        }
    }

    public LibrarySnapshot CreatePlaylist(string? name)
    {
        name = Regex.Replace(name?.Trim() ?? "", @"\s+", " ");
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl))
            throw new LibraryException("Название плейлиста: от 1 до 60 символов.");
        lock (gate)
        {
            if (document.Playlists.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return SnapshotOf(document);
            if (document.Playlists.Length >= MaxPlaylists) throw new LibraryException("Можно сохранить до 64 плейлистов.");
            return Commit(document with { Playlists = [..document.Playlists, new(Guid.NewGuid().ToString("N"), name, [], DateTimeOffset.UtcNow)] });
        }
    }

    public LibrarySnapshot SetMembership(string playlistId, Track track, bool added)
    {
        lock (gate)
        {
            CheckTrack(track);
            var playlist = document.Playlists.FirstOrDefault(p => p.Id == playlistId)
                ?? throw new LibraryException("Плейлист не найден.");
            if (playlist.TrackIds.Contains(track.Id) == added) return SnapshotOf(document);
            var next = WithTrack(document, track);
            var updated = playlist with { TrackIds = added ? [..playlist.TrackIds, track.Id] : playlist.TrackIds.Where(id => id != track.Id).ToArray() };
            return Commit(next with { Playlists = next.Playlists.Select(p => p.Id == playlistId ? updated : p).ToArray() });
        }
    }

    private static void CheckTrack(Track track)
    {
        if (!ValidTrack(track)) throw new LibraryException("Сохранить можно только найденную песню YouTube Music.");
    }
    private static Document WithTrack(Document value, Track track)
    {
        if (value.Tracks.ContainsKey(track.Id)) return value;
        if (value.Tracks.Count >= MaxTracks) throw new LibraryException("В библиотеке можно сохранить до 2000 песен.");
        var tracks = new Dictionary<string, LibraryEntry>(value.Tracks) { [track.Id] = new(track, TrackUrl(track.Id), DateTimeOffset.UtcNow) };
        return value with { Tracks = tracks };
    }

    private LibrarySnapshot Commit(Document next)
    {
        var retained = next.LikedIds.Concat(next.Playlists.SelectMany(p => p.TrackIds)).ToHashSet();
        next = next with { Revision = document.Revision + 1, Tracks = next.Tracks.Where(t => retained.Contains(t.Key)).ToDictionary() };
        try
        {
            using (var stream = new FileStream(file + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, next, json);
                if (stream.Length > 8 * 1024 * 1024) throw new LibraryException("Библиотека достигла предельного размера.");
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(file)) File.Copy(file, file + ".bak", overwrite: true);
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new LibraryUnavailableException(); }
        document = next;
        return SnapshotOf(document);
    }
}
