using System.Net.Http.Json;

namespace Bridge;

public sealed class ProviderException(string message) : Exception(message);

public sealed class YoutubeProvider(IConfiguration config)
{
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(75) };
    private readonly object cacheLock = new();
    private readonly Dictionary<string, Track> known = new();
    private string BaseUrl => (config["YOUTUBE_PROVIDER_URL"] ?? "").TrimEnd('/');
    public bool Enabled => !string.IsNullOrEmpty(BaseUrl);

    public Track? Find(string id) { lock (cacheLock) return known.GetValueOrDefault(id); }

    public async Task<Track[]> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (!Enabled) throw new ProviderException("YouTube ещё не настроен на сервере.");
        if (query.Trim().Length is < 2 or > 128)
            throw new ProviderException("Введи от 2 до 128 символов для поиска.");
        try
        {
            using var response = await client.GetAsync(BaseUrl + "/search?q=" + Uri.EscapeDataString(query.Trim()), cancellationToken);
            if (!response.IsSuccessStatusCode) throw new ProviderException("YouTube Music недоступен с Raspberry Pi. Проверь подключение или прокси.");
            var tracks = await response.Content.ReadFromJsonAsync<Track[]>(cancellationToken) ?? [];
            lock (cacheLock)
            {
                foreach (var track in tracks)
                {
                    if (known.Count >= 200) known.Remove(known.Keys.First());
                    known[track.Id] = track;
                }
            }
            return tracks;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ProviderException) { throw; }
        catch { throw new ProviderException("Источник YouTube не ответил. Текущий эфир продолжает играть."); }
    }

    public async Task<AudioSource> ResolveAudioAsync(Track track)
    {
        if (!Enabled || !track.Id.StartsWith("youtube:", StringComparison.Ordinal))
            throw new ProviderException("Неизвестный источник.");
        var id = Uri.EscapeDataString(track.Id[8..]);
        try
        {
            using var response = await client.GetAsync(BaseUrl + "/resolve?id=" + id);
            if (!response.IsSuccessStatusCode)
                throw new ProviderException("YouTube не отдал аудио этого трека. Эфир сохранён; попробуй другую запись или проверь подключение.");
            return new AudioSource(BaseUrl + "/audio?id=" + id, false);
        }
        catch (ProviderException) { throw; }
        catch { throw new ProviderException("Не удалось подготовить трек YouTube. Текущий эфир сохранён."); }
    }
}
