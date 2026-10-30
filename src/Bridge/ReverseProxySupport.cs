using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Bridge;

public static class ReverseProxySupport
{
    public static ForwardedHeadersOptions? CreateOptions(string? trustedAddresses)
    {
        if (string.IsNullOrWhiteSpace(trustedAddresses)) return null;
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var value in trustedAddresses.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IPAddress.TryParse(value, out var address))
                throw new InvalidOperationException("TRUSTED_PROXY_IPS must contain individual IP addresses separated by commas.");
            options.KnownProxies.Add(address);
        }
        if (options.KnownProxies.Count == 0)
            throw new InvalidOperationException("TRUSTED_PROXY_IPS must contain at least one IP address.");
        return options;
    }

    public static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        return string.IsNullOrEmpty(origin) ||
            (Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
             uri.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase) &&
             uri.Scheme == request.Scheme);
    }
}
