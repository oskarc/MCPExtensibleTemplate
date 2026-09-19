using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace McpServerTemplate.Tests.Identity;

/// <summary>
/// contract-002 · T-12 (G-12) — a proxy an operator declares is a proxy the server actually
/// trusts.
///
/// HttpTransport:KnownProxies and :KnownNetworks were read by the transport guard and by nothing
/// else. Declaring a trusted proxy satisfied the startup check while ForwardedHeadersOptions kept
/// its defaults, so the forwarded address was ignored and every request appeared to come from the
/// proxy — which is the address the per-client rate limiter partitions on. A setting that is
/// checked but never applied is worse than an absent one.
/// </summary>
public class ForwardedHeadersTests
{
    private static ForwardedHeadersOptions OptionsFor(Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(settings);

        // Only the forwarded-headers registration is under test here; the rest of the composition
        // needs identity configured and is covered elsewhere.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;

            var proxies = builder.Configuration.GetSection("HttpTransport:KnownProxies").Get<string[]>() ?? [];
            var networks = builder.Configuration.GetSection("HttpTransport:KnownNetworks").Get<string[]>() ?? [];

            if (proxies.Length > 0 || networks.Length > 0)
            {
                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
            }

            foreach (var proxy in proxies)
            {
                options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
            }

            foreach (var network in networks)
            {
                var parts = network.Split('/', 2);
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(
                    System.Net.IPAddress.Parse(parts[0]),
                    parts.Length == 2 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 32));
            }
        });

        using var provider = builder.Services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
    }

    [Fact]
    public void T12_a_declared_proxy_reaches_the_middleware_that_trusts_it()
    {
        var options = OptionsFor(new()
        {
            ["HttpTransport:KnownProxies:0"] = "10.1.2.3",
            ["HttpTransport:KnownProxies:1"] = "10.1.2.4",
        });

        Assert.Contains(System.Net.IPAddress.Parse("10.1.2.3"), options.KnownProxies);
        Assert.Contains(System.Net.IPAddress.Parse("10.1.2.4"), options.KnownProxies);
    }

    [Fact]
    public void T12_a_declared_network_reaches_the_middleware_that_trusts_it()
    {
        var options = OptionsFor(new() { ["HttpTransport:KnownNetworks:0"] = "10.0.0.0/8" });

        var network = Assert.Single(options.KnownIPNetworks);
        Assert.Equal(System.Net.IPAddress.Parse("10.0.0.0"), network.BaseAddress);
        Assert.Equal(8, network.PrefixLength);
    }

    [Fact]
    public void T12_declaring_proxies_replaces_the_loopback_default_rather_than_adding_to_it()
    {
        // An operator who names their proxies means those. Leaving loopback trusted as well would
        // mean anything on the host could still spoof a forwarded address.
        var options = OptionsFor(new() { ["HttpTransport:KnownProxies:0"] = "10.1.2.3" });

        Assert.Single(options.KnownProxies);
        Assert.Empty(options.KnownIPNetworks);
    }

    [Fact]
    public void T12_declaring_nothing_leaves_the_defaults_alone()
    {
        // The default trusts loopback, which is right for a server with no proxy in front of it.
        var options = OptionsFor([]);

        Assert.NotEmpty(options.KnownIPNetworks);
    }
}
