using System.Net.Http.Json;

namespace Bridge;

public sealed class ProviderException(string message) : Exception(message);

public interface ITrackRecommendations
{
    Task<Track[]> RelatedAsync(Track track, CancellationToken cancellationToken = default);
}

public sealed class YoutubeProvider(IConfiguration config) : ITrackRecommendations
{
    private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(75) };
    private readonly object cacheLock = new();
    private readonly Dictionary<string, Track> known = new();
    private string BaseUrl => (config["YOUTUBE_PROVIDER_URL"] ?? "").TrimEnd('/');
    public bool Enabled => !string.IsNullOrEmpty(BaseUrl);

    public Track? Find(string id) { lock (cacheLock) return known.GetValueOrDefault(id); }

    private void Remember(Track[] tracks)
    {
        lock (cacheLock)
            foreach (var track in tracks)
            {
                if (known.Count >= 200) known.Remove(known.Keys.First());
                known[track.Id] = track;
            }
    }

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
            Remember(tracks);
            return tracks;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ProviderException) { throw; }
        catch { throw new ProviderException("Источник YouTube не ответил. Текущий эфир продолжает играть."); }
    }

    public async Task<Track[]> RelatedAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (!Enabled || !track.Id.StartsWith("youtube:", StringComparison.Ordinal)) return [];
        try
        {
            using var response = await client.GetAsync(BaseUrl + "/radio?id=" + Uri.EscapeDataString(track.Id[8..]), cancellationToken);
            if (!response.IsSuccessStatusCode) throw new ProviderException("YouTube не вернул следующую песню. Попробуй ещё раз.");
            var tracks = await response.Content.ReadFromJsonAsync<Track[]>(cancellationToken) ?? [];
            Remember(tracks);
            return tracks;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ProviderException) { throw; }
        catch { throw new ProviderException("Не удалось загрузить следующие песни."); }
    }

    public async Task<(byte[] Bytes, string ContentType)> ArtworkAsync(Track track, CancellationToken cancellationToken, bool preview = false)
    {
        // A saved track can outlive the provider's in-memory search catalog.
        // The internal adapter validates this fallback against its image allowlist.
        var thumbnail = string.IsNullOrEmpty(track.Thumbnail) ? "" : "&thumbnail=" + Uri.EscapeDataString(track.Thumbnail);
        using var response = await client.GetAsync(BaseUrl + "/artwork?id=" + Uri.EscapeDataString(track.Id[8..]) + (preview ? "&size=small" : "") + thumbnail, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new ProviderException("Обложка недоступна.");
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not ("image/jpeg" or "image/png" or "image/webp"))
            throw new ProviderException("Обложка недоступна.");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length is 0 or > 2097152) throw new ProviderException("Обложка недоступна.");
        return (bytes, contentType);
    }

    public async Task<AudioSource> ResolveAudioAsync(Track track, CancellationToken cancellationToken = default)
    {
        if (!Enabled || !track.Id.StartsWith("youtube:", StringComparison.Ordinal))
            throw new ProviderException("Неизвестный источник.");
        var id = Uri.EscapeDataString(track.Id[8..]);
        try
        {
            // A superseded Python extraction may still be unwinding. Retry only busy,
            // not arbitrary upstream failures, and always honour the latest selection.
            for (var attempt = 0; attempt < 40; attempt++)
            {
                using var response = await client.GetAsync(BaseUrl + "/resolve?id=" + id, cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable && attempt < 39)
                {
                    await Task.Delay(300, cancellationToken);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new ProviderException("Не удалось получить эту запись YouTube. Попробуй другую версию трека.");
                var resolution = await response.Content.ReadFromJsonAsync<Resolution>(cancellationToken);
                return new AudioSource(BaseUrl + "/audio?id=" + id, false, resolution?.Strategy,
                    resolution?.ExtractMilliseconds ?? 0, resolution?.ProbeMilliseconds ?? 0);
            }
            throw new ProviderException("Источник занят. Попробуй ещё раз.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (ProviderException) { throw; }
        catch { throw new ProviderException("Не удалось подготовить трек YouTube. Текущий эфир сохранён."); }
    }

    private sealed record Resolution(string? Strategy, int ExtractMilliseconds, int ProbeMilliseconds);
}
