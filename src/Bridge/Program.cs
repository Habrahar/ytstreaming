using Bridge;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IMusicProvider, TestProvider>();
builder.Services.AddSingleton<AudioEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AudioEngine>());
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 4096);
var app = builder.Build();

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
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/healthz", (AudioEngine engine) => Results.Json(
    new { ready = engine.Ready, state = engine.State }, statusCode: engine.Ready ? 200 : 503));
app.MapGet("/health/live", (AudioEngine engine) => Results.Json(
    new { state = engine.State }, statusCode: engine.State == "error" ? 503 : 200));
app.MapGet("/listen.m3u", (HttpRequest request) => Results.Text(
    $"#EXTM3U\n#EXTINF:-1,ytstreaming\n{request.Scheme}://{request.Host}/stream.mp3\n", "audio/x-mpegurl"));
app.MapGet("/api/status", (AudioEngine engine) => Results.Json(engine.Status));
app.MapGet("/api/tracks", (string? q, IMusicProvider provider) => provider.Search(q));
app.MapPost("/api/player/play", async (PlayRequest request, AudioEngine engine, IMusicProvider provider) =>
{
    var track = provider.Search(null).FirstOrDefault(t => t.Id == request.Id);
    if (track is null) return Results.NotFound(new { error = "Unknown track" });
    await engine.PlayAsync(track);
    return Results.Ok(engine.Status);
});
app.MapPost("/api/player/next", async (AudioEngine engine, IMusicProvider provider) =>
{
    var tracks = provider.Search(null);
    var current = Array.FindIndex(tracks, t => t.Id == engine.Current?.Id);
    await engine.PlayAsync(tracks[(current + 1) % tracks.Length]);
    return Results.Ok(engine.Status);
});
app.MapPost("/api/player/stop", async (AudioEngine engine) =>
{
    await engine.StopPlaybackAsync();
    return Results.Ok(engine.Status);
});
app.MapGet("/stream.mp3", async (HttpContext context, AudioEngine engine) =>
{
    if (!engine.Ready)
    {
        context.Response.StatusCode = 503;
        context.Response.Headers.RetryAfter = "2";
        return;
    }
    var listener = engine.Subscribe();
    if (listener is null)
    {
        context.Response.StatusCode = 429;
        return;
    }
    context.Response.ContentType = "audio/mpeg";
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
});
app.Run();

record PlayRequest(string Id);
