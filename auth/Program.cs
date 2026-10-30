using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using PlayerAuth;

if (args.SequenceEqual(new[] { "--hash" }))
{
    var input = JsonSerializer.Deserialize<HashInput>(Console.In.ReadToEnd(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException("Credentials are required on standard input.");
    var hasher = new PasswordHasher<string>();
    Console.Write(JsonSerializer.Serialize(new Credentials(input.Username, hasher.HashPassword(input.Username, input.Password))));
    return;
}
if (args.SequenceEqual(new[] { "--health" }))
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    using var response = await client.GetAsync("http://127.0.0.1:18092/health");
    response.EnsureSuccessStatusCode();
    return;
}
var credentials = JsonSerializer.Deserialize<Credentials>(File.ReadAllText(Environment.GetEnvironmentVariable("AUTH_CONFIG") ?? "/run/auth.json"))
    ?? throw new InvalidOperationException("Authentication configuration is required.");
await AuthHost.Create(credentials, Environment.GetEnvironmentVariable("AUTH_STATE") ?? "/state").RunAsync();

record HashInput(string Username, string Password);
