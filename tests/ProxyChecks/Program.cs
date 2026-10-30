using System.Net;
using Bridge;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static DefaultHttpContext Request(string peer, string proto = "https", string origin = "https://music.example.test:8443")
{
    var context = new DefaultHttpContext();
    context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
    context.Request.Scheme = "http";
    context.Request.Host = new HostString("music.example.test:8443");
    context.Request.Headers.Origin = origin;
    context.Request.Headers["X-Forwarded-Proto"] = proto;
    return context;
}
static async Task Forward(DefaultHttpContext context, string peers)
{
    var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask,
        NullLoggerFactory.Instance, Options.Create(ReverseProxySupport.CreateOptions(peers)!));
    await middleware.Invoke(context);
}

Check(ReverseProxySupport.CreateOptions(null) is null, "Direct LAN mode must not trust forwarding headers by default.");
Check(ReverseProxySupport.CreateOptions(" ") is null, "Empty proxy list must stay disabled.");
foreach (var bad in new[] { "0.0.0.0/0", "any", ",," })
{
    try { ReverseProxySupport.CreateOptions(bad); throw new Exception("An invalid trusted proxy was accepted."); }
    catch (InvalidOperationException) { }
}

var known = Request("192.0.2.10");
Check(!ReverseProxySupport.IsSameOrigin(known.Request), "HTTPS controls must fail before the proxy scheme is processed.");
await Forward(known, "192.0.2.10");
Check(known.Request.Scheme == "https" && ReverseProxySupport.IsSameOrigin(known.Request), "Trusted HTTPS proxy must preserve same-origin controls.");
Check(known.Connection.RemoteIpAddress!.Equals(IPAddress.Parse("192.0.2.10")), "Client IP headers must not be consumed.");

var stranger = Request("192.0.2.11");
await Forward(stranger, "192.0.2.10");
Check(stranger.Request.Scheme == "http" && !ReverseProxySupport.IsSameOrigin(stranger.Request), "An untrusted peer must not supply the HTTPS scheme.");
var loopback = Request("127.0.0.1");
await Forward(loopback, "192.0.2.10");
Check(loopback.Request.Scheme == "http", "Loopback must not be trusted implicitly when a proxy list is configured.");

var crossSite = Request("192.0.2.10", origin: "https://other.example.test:8443");
await Forward(crossSite, "192.0.2.10");
Check(!ReverseProxySupport.IsSameOrigin(crossSite.Request), "An HTTPS proxy must not bypass cross-site protection.");
var wrongPort = Request("192.0.2.10", origin: "https://music.example.test");
await Forward(wrongPort, "192.0.2.10");
Check(!ReverseProxySupport.IsSameOrigin(wrongPort.Request), "Origin ports must still match.");

var forgedHost = Request("192.0.2.10", origin: "https://other.example.test:8443");
forgedHost.Request.Headers["X-Forwarded-Host"] = "other.example.test:8443";
await Forward(forgedHost, "192.0.2.10");
Check(forgedHost.Request.Host.Value == "music.example.test:8443" && !ReverseProxySupport.IsSameOrigin(forgedHost.Request), "Forwarded host must not replace the actual Host header.");
var chained = Request("192.0.2.10", proto: "https,http");
await Forward(chained, "192.0.2.10");
Check(chained.Request.Scheme == "http" && !ReverseProxySupport.IsSameOrigin(chained.Request), "Only the nearest proxy's scheme may be used.");

var ipv6 = Request("2001:db8::10");
await Forward(ipv6, "192.0.2.10, 2001:db8::10");
Check(ipv6.Request.Scheme == "https", "Explicit IPv6 proxies must be supported.");
var mapped = Request("::ffff:192.0.2.10");
await Forward(mapped, "192.0.2.10");
Check(mapped.Request.Scheme == "https", "Mapped IPv4 peers must match their exact trusted IPv4 address.");

var lan = Request("192.0.2.11", origin: "http://music.example.test:8443");
Check(ReverseProxySupport.IsSameOrigin(lan.Request), "Direct LAN controls must stay available.");
lan.Request.Headers.Remove("Origin");
Check(ReverseProxySupport.IsSameOrigin(lan.Request), "Existing local API clients without Origin must stay compatible.");
Console.WriteLine("Proxy checks passed: trusted HTTPS, spoofed headers, cross-site controls, IPv4/IPv6 and unchanged LAN access.");
