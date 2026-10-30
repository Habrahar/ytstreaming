using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;

namespace PlayerAuth;

public record Credentials(string Username, string PasswordHash);

public static class AuthHost
{
    public const string CookieName = "__Host-ytstreaming";
    public static WebApplication Create(Credentials credentials, string stateDirectory, string? listen = null)
    {
        if (string.IsNullOrWhiteSpace(credentials.Username) || credentials.Username.Length > 64 || string.IsNullOrEmpty(credentials.PasswordHash))
            throw new InvalidOperationException("Invalid authentication configuration.");
        var builder = WebApplication.CreateBuilder();
        if (listen is not null) builder.WebHost.UseUrls(listen);
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 4096);
        builder.Logging.ClearProviders().AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);
        var keys = Directory.CreateDirectory(Path.Combine(stateDirectory, "keys"));
        builder.Services.AddDataProtection().SetApplicationName("ytstreaming.remote-login").PersistKeysToFileSystem(keys);
        var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentials.PasswordHash)));
        builder.Services.AddAuthentication("session").AddCookie("session", o =>
        {
            o.Cookie.Name = CookieName;
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.Path = "/";
            o.ExpireTimeSpan = TimeSpan.FromDays(30);
            o.SlidingExpiration = false;
            o.Events.OnValidatePrincipal = async context =>
            {
                if (context.Principal?.FindFirst("auth-version")?.Value != version)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync("session");
                }
            };
        });
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 8, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.OnRejected = async (context, token) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                context.HttpContext.Response.ContentType = "text/html; charset=utf-8";
                await context.HttpContext.Response.WriteAsync(Page("Слишком много попыток. Подожди минуту и попробуй снова.", false), token);
            };
        });
        var app = builder.Build();
        var forwarding = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
        forwarding.KnownNetworks.Clear();
        forwarding.KnownProxies.Clear();
        forwarding.KnownProxies.Add(IPAddress.Loopback);
        forwarding.KnownProxies.Add(IPAddress.IPv6Loopback);
        app.UseForwardedHeaders(forwarding);
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            // Native form POSTs use an opaque Origin under no-referrer.
            // Keep the origin available for same-site CSRF validation.
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            context.Response.Headers.ContentDisposition = "inline";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
            await next(context);
        });
        app.UseAuthentication();
        app.UseRateLimiter();
        app.MapGet("/health", () => Results.Ok(new { ready = true }));
        app.MapGet("/login", (HttpContext context) => Results.Content(Page(context.Request.Query.ContainsKey("error") ? "Не получилось войти. Проверь логин и пароль." : "", false), "text/html; charset=utf-8"));
        app.MapGet("/auth/login", (HttpContext context) => RedirectPage(context, "/login"));
        app.MapPost("/auth/login", async (HttpContext context) =>
        {
            if (!SameOrigin(context.Request))
                return Results.Content(Page("Обнови страницу входа и попробуй ещё раз.", false), "text/html; charset=utf-8", statusCode: 403);
            if (!context.Request.HasFormContentType)
                return Results.Content(Page("Не удалось прочитать форму. Повтори вход.", false), "text/html; charset=utf-8", statusCode: 400);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var username = form["username"].ToString();
            var supplied = form["password"].ToString();
            if (supplied.Length > 256)
                return Results.Content(Page("Пароль слишком длинный.", false), "text/html; charset=utf-8", statusCode: 400);
            var validPassword = new PasswordHasher<string>().VerifyHashedPassword(credentials.Username, credentials.PasswordHash, supplied)
                != PasswordVerificationResult.Failed;
            if (!validPassword || username != credentials.Username) return RedirectPage(context, "/login?error=1");
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, credentials.Username), new Claim("auth-version", version) }, "session");
            await context.SignInAsync("session", new ClaimsPrincipal(identity), new AuthenticationProperties
            {
                IsPersistent = form["remember"] == "on",
                AllowRefresh = true
            });
            return RedirectPage(context, "/");
        }).RequireRateLimiting("login");
        app.MapGet("/auth/check", (HttpContext context) =>
        {
            if (context.User.Identity?.IsAuthenticated == true) return Results.Ok();
            var uri = context.Request.Headers["X-Forwarded-Uri"].ToString().Split('?', 2)[0];
            if (context.Request.Headers["X-Forwarded-Method"] == "GET" && uri is "/" or "/index.html")
                return RedirectPage(context, "/login");
            return Results.Json(new { error = "Нужно войти в плеер." }, statusCode: 401);
        });
        app.MapGet("/auth/logout", () => Results.Content(Page("", true), "text/html; charset=utf-8"));
        app.MapPost("/auth/logout", async (HttpContext context) =>
        {
            if (!SameOrigin(context.Request))
                return Results.Content(Page("Обнови страницу и повтори выход.", true), "text/html; charset=utf-8", statusCode: 403);
            await context.SignOutAsync("session");
            return RedirectPage(context, "/login");
        });
        return app;
    }

    private static bool SameOrigin(HttpRequest request) => request.IsHttps &&
        Uri.TryCreate(request.Headers.Origin, UriKind.Absolute, out var origin) &&
        origin.Scheme == "https" && origin.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase);

    private static IResult RedirectPage(HttpContext context, string target)
    {
        context.Response.Headers.Location = target;
        return Results.Content($"<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><title>Переход · ytstreaming</title><a href=\"{WebUtility.HtmlEncode(target)}\">Продолжить</a></html>",
            "text/html; charset=utf-8", statusCode: 303);
    }

    private static string Page(string error, bool logout) => $$"""
<!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Вход · ytstreaming</title>
<style>*{box-sizing:border-box}body{margin:0;background:#101a14;color:#eff6ed;font:16px system-ui;min-height:100dvh;display:grid;place-items:center;padding:24px}.card{width:100%;max-width:360px}h1{font-size:30px;letter-spacing:-1px;margin:0 0 12px}p{color:#adbfaf;line-height:1.5}label{display:block;margin-top:20px}input:not([type=checkbox]){display:block;width:100%;margin-top:8px;padding:14px;border:1px solid #425547;border-radius:12px;background:#202c23;color:inherit;font:inherit}button{width:100%;padding:15px;border:0;border-radius:14px;background:#c5edaa;color:#1b3020;font:600 16px system-ui;margin-top:24px;cursor:pointer}.remember{display:flex;gap:10px;align-items:center;font-size:14px}.error{color:#f1c78f}a{color:#c5edaa}</style>
<main class="card"><h1>♫ Твоя музыка</h1><p>{{(logout ? "Выйти из плеера на этом устройстве?" : "Войди один раз и слушай свою музыку.")}}</p><p class="error" role="alert">{{error}}</p>
{{(logout ? "<form method=post action=/auth/logout><button>Выйти</button></form><p><a href=/ >Вернуться к музыке</a></p>" : "<form method=post action=/auth/login><label>Логин<input name=username autocomplete=username required maxlength=64></label><label>Пароль<input type=password name=password autocomplete=current-password required maxlength=256></label><label class=remember><input type=checkbox name=remember checked>Запомнить устройство на 30 дней</label><button>Войти</button></form>")}}
</main></html>
""";
}
