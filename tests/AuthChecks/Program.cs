using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using PlayerAuth;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
var directory = Path.Combine(Path.GetTempPath(), "ytstreaming-auth-" + Guid.NewGuid().ToString("N"));
var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
var credentials = new Credentials("listener", new PasswordHasher<string>().HashPassword("listener", secret));
async Task<(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client)> Start(Credentials account)
{
    var app = AuthHost.Create(account, directory, "http://127.0.0.1:0");
    await app.StartAsync();
    var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    return (app, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(url) });
}
static async Task<HttpResponseMessage> Send(HttpClient client, string path, string? cookie = null, Dictionary<string,string>? form = null, string origin = "https://music.example.test:8443", string peer = "192.0.2.10")
{
    var request = new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Post, path);
    request.Headers.Host = "music.example.test:8443";
    request.Headers.Add("X-Forwarded-Proto", "https");
    request.Headers.Add("X-Forwarded-For", peer);
    request.Headers.Add("X-Forwarded-Uri", "/");
    request.Headers.Add("X-Forwarded-Method", "GET");
    if (cookie is not null) request.Headers.Add("Cookie", cookie);
    if (form is not null)
    {
        request.Headers.Add("Origin", origin);
        request.Content = new FormUrlEncodedContent(form);
    }
    return await client.SendAsync(request);
}
var (app, client) = await Start(credentials);
string cookie;
try
{
    using var anonymous = await Send(client, "/auth/check");
    Check(anonymous.StatusCode == HttpStatusCode.Redirect && anonymous.Headers.Location!.ToString() == "/login", "Anonymous page must redirect to login.");
    Check(!anonymous.Headers.WwwAuthenticate.Any(), "Browser Basic Authentication prompt must never be returned.");
    using var page = await Send(client, "/login");
    Check(page.StatusCode == HttpStatusCode.OK && (await page.Content.ReadAsStringAsync()).Contains("Запомнить"), "A real login form must be available.");
    var form = new Dictionary<string,string> { ["username"] = "listener", ["password"] = secret, ["remember"] = "on" };
    using var cross = await Send(client, "/auth/login", form: form, origin: "https://foreign.example.test");
    Check(cross.StatusCode == HttpStatusCode.Forbidden, "Cross-site login must be rejected.");
    using var signed = await Send(client, "/auth/login", form: form);
    Check(signed.StatusCode == HttpStatusCode.SeeOther, "Valid credentials must sign in.");
    var header = signed.Headers.GetValues("Set-Cookie").Single();
    Check(header.Contains("secure", StringComparison.OrdinalIgnoreCase) && header.Contains("httponly", StringComparison.OrdinalIgnoreCase) && header.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase), "Cookie must be Secure, HttpOnly and SameSite=Lax.");
    Check(header.Contains("expires=", StringComparison.OrdinalIgnoreCase) && header.StartsWith(AuthHost.CookieName + "="), "Remembered login must have a persistent host-only cookie.");
    Check(!header.Contains("domain=", StringComparison.OrdinalIgnoreCase), "Cookie must not require a DNS domain.");
    cookie = header.Split(';')[0];
    using var accepted = await Send(client, "/auth/check", cookie);
    Check(accepted.StatusCode == HttpStatusCode.OK, "A saved cookie must authorize a new request.");
    var index = cookie.IndexOf('=') + 8;
    var tampered = cookie[..index] + (cookie[index] == 'A' ? 'B' : 'A') + cookie[(index + 1)..];
    using var rejected = await Send(client, "/auth/check", tampered);
    Check(rejected.StatusCode != HttpStatusCode.OK, "A forged cookie must be rejected.");
    form.Remove("remember");
    using var temporary = await Send(client, "/auth/login", form: form);
    Check(!temporary.Headers.GetValues("Set-Cookie").Single().Contains("expires=", StringComparison.OrdinalIgnoreCase), "Unchecked remember must use a session cookie.");
    for (var i = 0; i < 9; i++)
    {
        using var attempt = await Send(client, "/auth/login", form: form, peer: "192.0.2.20");
        if (i == 8) Check((int)attempt.StatusCode == 429, "Repeated logins must be rate limited per client.");
    }
}
finally { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
(app, client) = await Start(credentials);
try
{
    using var restored = await Send(client, "/auth/check", cookie);
    Check(restored.StatusCode == HttpStatusCode.OK, "Saved login must survive a server/container restart.");
    using var logout = await Send(client, "/auth/logout", cookie, new());
    Check(logout.StatusCode == HttpStatusCode.SeeOther && logout.Headers.GetValues("Set-Cookie").Any(c => c.Contains("expires=", StringComparison.OrdinalIgnoreCase)), "Logout must clear the browser cookie.");
}
finally { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
var changed = credentials with { PasswordHash = new PasswordHasher<string>().HashPassword("listener", Convert.ToHexString(RandomNumberGenerator.GetBytes(20))) };
(app, client) = await Start(changed);
try
{
    using var revoked = await Send(client, "/auth/check", cookie);
    Check(revoked.StatusCode != HttpStatusCode.OK, "Changing credentials must revoke old cookies.");
}
finally { client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
Console.WriteLine("Auth checks passed: remembered/session cookies, restart persistence, tampering, CSRF, rate limits and credential changes.");
