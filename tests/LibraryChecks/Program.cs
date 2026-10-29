using Bridge;
using Microsoft.Extensions.Configuration;

var directory = Path.Combine(Path.GetTempPath(), "ytstreaming-library-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LIBRARY_DIRECTORY"] = directory }).Build();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var song = new Track("youtube:AAAAAAAAAAA", "Название <песни>", "Исполнитель", "youtube", "3:15", "https://i.ytimg.com/vi/AAAAAAAAAAA/default.jpg");
try
{
    var library = new MusicLibrary(config);
    var liked = library.Like(song, true);
    Check(liked.LikedIds.SequenceEqual([song.Id]), "Like not saved");
    Check(library.Like(song, true).Revision == liked.Revision, "Duplicate like was not idempotent");
    var created = library.CreatePlaylist("  В   дорогу  ");
    var playlist = created.Playlists.Single();
    Check(playlist.Name == "В дорогу", "Playlist name not normalized");
    Check(library.CreatePlaylist("в дорогу").Playlists.Length == 1, "Duplicate playlist created");
    var added = library.SetMembership(playlist.Id, song, true);
    Check(library.SetMembership(playlist.Id, song, true).Revision == added.Revision, "Duplicate playlist entry");
    library.Like(song, false);
    var reopened = new MusicLibrary(config);
    Check(reopened.Snapshot.LikedIds.Length == 0, "Unlike not persisted");
    Check(reopened.Snapshot.Playlists.Single().TrackIds.SequenceEqual([song.Id]), "Playlist lost after reopening");
    Check(reopened.Find(song.Id) == song, "Saved metadata lost after reopening");
    Check(reopened.Snapshot.Tracks.Single().Url == "https://music.youtube.com/watch?v=AAAAAAAAAAA", "Canonical link lost");
    reopened.SetMembership(playlist.Id, song, false);
    Check(reopened.Find(song.Id) is null, "Unused track retained");
    Console.WriteLine("PASS: likes, unlike, playlist membership, deduplication, metadata/links and reopening");

    library = new MusicLibrary(config);
    await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
    {
        var track = song with { Id = "youtube:" + i.ToString("D11"), Title = "Song " + i };
        library.Like(track, true);
        library.SetMembership(playlist.Id, track, true);
    })));
    Check(new MusicLibrary(config).Snapshot.LikedIds.Length == 16, "Concurrent likes lost");
    Check(new MusicLibrary(config).Snapshot.Playlists.Single().TrackIds.Length == 16, "Concurrent playlist additions lost");
    var snapshot = library.Snapshot; snapshot.LikedIds[0] = "mutated"; snapshot.Playlists[0].TrackIds[0] = "mutated";
    Check(!library.Snapshot.LikedIds.Contains("mutated") && !library.Snapshot.Playlists[0].TrackIds.Contains("mutated"), "Snapshot leaked mutable state");
    Console.WriteLine("PASS: concurrent additions and detached snapshots");

    var before = File.ReadAllText(Path.Combine(directory, "library.json"));
    var revision = library.Revision;
    Directory.CreateDirectory(Path.Combine(directory, "library.json.tmp"));
    try { library.Like(song, true); throw new Exception("Write failure was hidden"); }
    catch (LibraryUnavailableException) { }
    Check(library.Revision == revision && !library.Snapshot.LikedIds.Contains(song.Id), "Failed write published memory state");
    Check(File.ReadAllText(Path.Combine(directory, "library.json")) == before, "Failed write damaged stored data");
    Directory.Delete(Path.Combine(directory, "library.json.tmp"));
    try { library.CreatePlaylist(new string('x', 61)); throw new Exception("Invalid name accepted"); } catch (LibraryException) { }
    try { library.Like(song with { Id = "https://example.com/" }, true); throw new Exception("URL accepted as a track ID"); } catch (LibraryException) { }
    Console.WriteLine("PASS: write failure preserves prior state; invalid names and IDs rejected");

    File.WriteAllText(Path.Combine(directory, "library.json"), "{broken");
    try { _ = new MusicLibrary(config); throw new Exception("Damaged file silently accepted"); } catch (System.Text.Json.JsonException) { }
    Check(File.ReadAllText(Path.Combine(directory, "library.json")) == "{broken", "Damaged file overwritten");
    Check(File.Exists(Path.Combine(directory, "library.json.bak")), "Previous version backup missing");
    Console.WriteLine("PASS: damaged file preserved; previous version backup exists");
}
finally
{
    var resolved = Path.GetFullPath(directory);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(resolved).StartsWith("ytstreaming-library-check-", StringComparison.Ordinal))
        throw new Exception("Unexpected test cleanup directory");
    Directory.Delete(resolved, recursive: true);
}
