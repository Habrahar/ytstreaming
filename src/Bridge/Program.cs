using Bridge;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<TestProvider>();
builder.Services.AddSingleton<YoutubeProvider>();
builder.Services.AddSingleton<ITrackRecommendations>(sp => sp.GetRequiredService<YoutubeProvider>());
builder.Services.AddSingleton<MusicCatalog>();
builder.Services.AddSingleton<IMusicProvider>(sp => sp.GetRequiredService<MusicCatalog>());
builder.Services.AddSingleton<AudioEngine>();
builder.Services.AddSingleton<PlaybackNavigator>();
builder.Services.AddSingleton<NextTrackPreparer>();
builder.Services.AddSingleton<MusicLibrary>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AudioEngine>());
builder.Services.AddHostedService<RadioPlaybackService>();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 4096);
var app = builder.Build();
var musicLibrary = app.Services.GetRequiredService<MusicLibrary>();
var statusJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
JsonNode PlayerStatus(AudioEngine engine, PlaybackNavigator navigator)
{
    var status = JsonSerializer.SerializeToNode(engine.Status, statusJsonOptions)!;
    status["navigation"] = JsonSerializer.SerializeToNode(navigator.Status, statusJsonOptions);
    status["libraryRevision"] = musicLibrary.Revision;
    return status;
}

// Same-origin JSON controls: a page on another site cannot change playback.
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (HttpMethods.IsPost(context.Request.Method))
    {
        var origin = context.Request.Headers.Origin.ToString();
        var sameOrigin = string.IsNullOrEmpty(origin) ||
            (Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
             uri.Authority.Equals(context.Request.Host.Value, StringComparison.OrdinalIgnoreCase) &&
             uri.Scheme == context.Request.Scheme);
        if (!sameOrigin || !context.Request.HasJsonContentType())
        {
            context.Response.StatusCode = 403;
            return;
        }
    }
    try { await next(context); }
    catch (SelectionSupersededException) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = 409;
        await context.Response.WriteAsJsonAsync(new { error = "Уже выбрана другая песня." });
    }
    catch (ProviderException ex) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = 502;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (LibraryException ex) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (LibraryUnavailableException) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { error = "Не удалось сохранить библиотеку на Pi. Изменение не сохранено; попробуй ещё раз." });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context =>
    context.Context.Response.Headers.CacheControl = "no-cache" });
app.MapGet("/healthz", (AudioEngine engine) => Results.Json(
    new { ready = engine.Ready, state = engine.State }, statusCode: engine.Ready ? 200 : 503));
app.MapGet("/health/live", (AudioEngine engine) => Results.Json(
    new { state = engine.State }, statusCode: engine.State == "error" ? 503 : 200));
app.MapGet("/listen.m3u", (HttpRequest request) => Results.Text(
    $"#EXTM3U\n#EXTINF:-1,ytstreaming\n{request.Scheme}://{request.Host}/stream\n", "audio/x-mpegurl"));
app.MapGet("/api/status", (AudioEngine engine, PlaybackNavigator navigator) => Results.Json(PlayerStatus(engine, navigator)));
var startupMeasurements = new StartupMeasurements();
app.MapGet("/api/player/startups", () => startupMeasurements.Snapshot);
app.MapPost("/api/player/startups", (StartupSample sample, AudioEngine engine) =>
{
    if (!startupMeasurements.Add(sample)) return Results.BadRequest();
    engine.ConfirmBrowserPlayback(sample.RequestId);
    return (IResult)Results.NoContent();
});
app.MapGet("/api/player/neighbors", (long revision, long version, PlaybackNavigator navigator, HttpContext context) =>
    navigator.PreviewAsync(revision, version, context.RequestAborted, deferUntilPlayback: true));
app.MapGet("/api/tracks", (string? q, IMusicProvider provider) => provider.Search(q));
app.MapGet("/api/youtube/search", (string q, YoutubeProvider youtube, HttpContext context) => youtube.SearchAsync(q, context.RequestAborted));
Track? FindTrack(string? id, MusicCatalog catalog, PlaybackNavigator navigator, AudioEngine engine, MusicLibrary library) =>
    string.IsNullOrEmpty(id) || id.Length > 64 ? null :
        catalog.Find(id) ?? library.Find(id) ?? navigator.Find(id) ?? (engine.Current?.Id == id ? engine.Current : null);
app.MapGet("/api/library", (MusicLibrary library) => library.Snapshot);
app.MapPost("/api/library/likes", (LikeRequest request, MusicLibrary library, MusicCatalog catalog, PlaybackNavigator navigator, AudioEngine engine) =>
{
    var track = FindTrack(request.Id, catalog, navigator, engine, library);
    if (track is null) return Results.NotFound(new { error = "Песня не найдена. Повтори поиск." });
    return Results.Ok(library.Like(track, request.Liked));
});
app.MapPost("/api/library/playlists", (PlaylistRequest request, MusicLibrary library) => library.CreatePlaylist(request.Name));
app.MapPost("/api/library/playlists/{playlistId}/tracks", (string playlistId, MembershipRequest request, MusicLibrary library, MusicCatalog catalog, PlaybackNavigator navigator, AudioEngine engine) =>
{
    var track = FindTrack(request.Id, catalog, navigator, engine, library);
    if (track is null) return Results.NotFound(new { error = "Песня не найдена. Повтори поиск." });
    return Results.Ok(library.SetMembership(playlistId, track, request.Added));
});
app.MapGet("/api/artwork/{videoId}", async (string videoId, MusicCatalog catalog, PlaybackNavigator navigator, AudioEngine engine, MusicLibrary library, YoutubeProvider youtube, HttpContext context) =>
{
    if (videoId.Length != 11 || videoId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return Results.NotFound();
    var id = "youtube:" + videoId;
    var track = FindTrack(id, catalog, navigator, engine, library);
    if (track is null) return Results.NotFound();
    var size = context.Request.Query["size"].ToString();
    var artwork = await youtube.ArtworkAsync(track, context.RequestAborted, size == "card" ? 320 : size == "small" ? 96 : 0);
    context.Response.Headers.CacheControl = "private, max-age=3600";
    return Results.File(artwork.Bytes, artwork.ContentType);
});
app.MapPost("/api/player/play", async (PlayRequest request, AudioEngine engine, PlaybackNavigator navigator, MusicCatalog provider, MusicLibrary library, HttpContext context) =>
{
    if (request.RequestId?.Length > 64) return Results.BadRequest();
    var track = FindTrack(request.Id, provider, navigator, engine, library);
    if (track is null) return Results.NotFound(new { error = "Unknown track" });
    await navigator.PlayAsync(track, request.RequestId, context.RequestAborted, request.ClientId, request.CommandSequence);
    return Results.Ok(PlayerStatus(engine, navigator));
});
app.MapPost("/api/player/next", async (NavigationRequest request, AudioEngine engine, PlaybackNavigator navigator, MusicCatalog catalog, MusicLibrary library, HttpContext context) =>
{
    if (request.RequestId?.Length > 64 || request.ExpectedId?.Length > 64) return Results.BadRequest();
    var anchor = FindTrack(request.AnchorId, catalog, navigator, engine, library);
    if (request.AnchorId is not null && anchor is null) return Results.NotFound(new { error = "Песня не найдена." });
    await navigator.NextAsync(request.RequestId, context.RequestAborted, request.ExpectedId, request.ExpectedRevision,
        request.ClientId, request.CommandSequence, request.NavigationId, request.NavigationOffset, anchor);
    return Results.Ok(PlayerStatus(engine, navigator));
});
app.MapPost("/api/player/previous", async (NavigationRequest request, AudioEngine engine, PlaybackNavigator navigator, MusicCatalog catalog, MusicLibrary library, HttpContext context) =>
{
    if (request.RequestId?.Length > 64 || request.ExpectedId?.Length > 64) return Results.BadRequest();
    var anchor = FindTrack(request.AnchorId, catalog, navigator, engine, library);
    if (request.AnchorId is not null && anchor is null) return Results.NotFound(new { error = "Песня не найдена." });
    await navigator.PreviousAsync(request.RequestId, context.RequestAborted, request.ExpectedId, request.ExpectedRevision,
        request.ClientId, request.CommandSequence, request.NavigationId, request.NavigationOffset, anchor);
    return Results.Ok(PlayerStatus(engine, navigator));
});
app.MapPost("/api/player/stop", async (ControlRequest request, AudioEngine engine, PlaybackNavigator navigator) =>
{
    await navigator.StopAsync(request.ClientId, request.CommandSequence);
    return Results.Ok(PlayerStatus(engine, navigator));
});
var serveAudio = async (HttpContext context, AudioEngine engine) =>
{
    var requestId = context.Request.Query["requestId"].ToString();
    var revisionText = context.Request.Query["revision"].ToString();
    if (requestId.Length > 64) { context.Response.StatusCode = 400; return; }
    var deadline = DateTime.UtcNow.AddSeconds(90);
    var observed = false;
    // The browser calls play() directly during the tap; this connection waits for
    // exactly that selection, retaining the mobile user gesture during preparation.
    while (!string.IsNullOrEmpty(requestId))
    {
        if (engine.ActiveRequestId == requestId && engine.Ready) break;
        observed |= engine.LatestRequestId == requestId;
        if ((observed && engine.LatestRequestId != requestId) || engine.FailedRequestId == requestId || DateTime.UtcNow >= deadline)
        { context.Response.StatusCode = 409; return; }
        await Task.Delay(100, context.RequestAborted);
    }
    if (!engine.Ready)
    {
        context.Response.StatusCode = 503;
        context.Response.Headers.RetryAfter = "2";
        return;
    }
    long? expectedRevision = long.TryParse(revisionText, out var requestedRevision) ? requestedRevision : null;
    if (expectedRevision.HasValue && expectedRevision != engine.Revision)
    { context.Response.StatusCode = 409; return; }
    var listener = engine.Subscribe(expectedRevision ?? engine.Revision);
    if (listener is null)
    {
        context.Response.StatusCode = 429;
        return;
    }
    context.Response.ContentType = listener.Value.ContentType;
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    try
    {
        await foreach (var chunk in listener.Value.Reader.ReadAllAsync(context.RequestAborted))
        {
            await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (IOException) { /* A player disconnected. */ }
    finally { engine.Unsubscribe(listener.Value.Id); }
};
app.MapGet("/stream", serveAudio);
app.MapGet("/stream.mp3", serveAudio); // Legacy address, with the actual MIME type.
app.Run();

record PlayRequest(string Id, string? RequestId = null, string? ClientId = null, long? CommandSequence = null);
record NavigationRequest(string? RequestId = null, string? ExpectedId = null, long? ExpectedRevision = null,
    string? ClientId = null, long? CommandSequence = null, string? NavigationId = null, long? NavigationOffset = null, string? AnchorId = null);
record ControlRequest(string? ClientId = null, long? CommandSequence = null);
record LikeRequest(string Id, bool Liked);
record PlaylistRequest(string? Name);
record MembershipRequest(string Id, bool Added);
