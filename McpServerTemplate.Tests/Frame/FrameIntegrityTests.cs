using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Frame;
using McpServerTemplate.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerTemplate.Tests.Frame;

/// <summary>
/// contract-003 · T-13 (G-12) — a provider that tries to undo the frame stops the server, named;
/// and T-12 (G-11) — a setting the server would silently ignore stops it, with the nearest real key.
/// </summary>
public class FrameIntegrityTests
{
    [Theory]
    [InlineData(typeof(RemovingModule), "removed service registrations")]
    [InlineData(typeof(FilterAddingModule), "registered services that belong to the frame")]
    [InlineData(typeof(SideDoorModule), "registered services that belong to the frame")]
    [InlineData(typeof(IdentityTamperingModule), "registered services that belong to the frame")]
    public void T13_a_provider_that_reaches_into_the_frame_refuses_startup_named(Type moduleType, string why)
    {
        var module = (IProviderModule)Activator.CreateInstance(moduleType)!;
        var message = FrameHarness.Refusal([module], StartupRefusalTests.SettingsWithKey([module]));

        Assert.Contains($"'{module.Name}'", message, StringComparison.Ordinal);
        Assert.Contains(why, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (SEC-R3-01, SEC-R3-02) — declared, not inferred: a provider that adds a part of the frame
    /// of its own, which the check's list of forbidden namespaces did not name, refuses startup naming itself and the type.
    /// Each was accepted: a provider's HttpContextFactory made every request with no token a principal of its choosing; its
    /// ILoggerFactory took the whole log, security events included, round the line-safe pipeline; an ILoggerProvider or a
    /// Serilog logger of its own reaches the same log; its IOptionsFactory for ForwardedHeadersOptions put a /0 trust in force.
    /// The memory cache every provider shares is the frame's to lay, as logging is; and a provider declared in ASP.NET Core's
    /// own namespace owns none of ASP.NET Core's types, a namespace being only a name.
    /// </summary>
    [Theory]
    [InlineData(typeof(ContextForgingModule), "Microsoft.AspNetCore.Http.IHttpContextFactory")]
    [InlineData(typeof(LoggerFactoryModule), "Microsoft.Extensions.Logging.ILoggerFactory")]
    [InlineData(typeof(LoggerProviderModule), "Microsoft.Extensions.Logging.ILoggerProvider")]
    [InlineData(typeof(SerilogModule), "Serilog.ILogger")]
    [InlineData(typeof(ForwardedHeadersOptionsModule), "Microsoft.Extensions.Options.IOptionsFactory<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>")]
    [InlineData(typeof(MemoryCacheModule), "Microsoft.Extensions.Caching.Memory.IMemoryCache")]
    [InlineData(typeof(Microsoft.AspNetCore.Http.NamespaceSquatterModule), "Microsoft.AspNetCore.Http.IHttpContextFactory")]
    public void G17_a_provider_that_adds_a_part_of_the_frame_of_its_own_refuses_startup_naming_it(Type moduleType, string type)
    {
        var module = (IProviderModule)Activator.CreateInstance(moduleType)!;
        var message = FrameHarness.Refusal([module], StartupRefusalTests.SettingsWithKey([module]));

        Assert.Contains($"The provider '{module.Name}' registered services", message, StringComparison.Ordinal);
        Assert.Contains($"which no provider may register: {type}.", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (SEC-R3-01) — the control for the rows above: what a provider may register is exactly what
    /// the built-in providers' registrations add. Each built-in provider starts alone, which is when its registrations add the
    /// most (the HTTP client factory's and the resilience handler's plumbing are the first provider's to add), and all three
    /// start together; and so does a provider from another assembly with a typed client of its own, as the end-to-end test
    /// host's irreversible provider has.
    /// </summary>
    [Theory]
    [InlineData("Smhi")]
    [InlineData("SmhiObs")]
    [InlineData("JsonPlaceholder")]
    public void G17_each_built_in_provider_alone_starts(string name)
    {
        var module = BuiltInProviders.Create().Single(m => m.Name == name);
        using var provider = FrameHarness.Compose([module], StartupRefusalTests.SettingsWithKey([module]));

        Assert.Equal([name], provider.GetRequiredService<EnabledProviders>().Modules.Select(m => m.Name));
    }

    [Fact]
    public void G17_a_provider_with_a_typed_http_client_of_its_own_starts()
    {
        var module = new TypedClientModule();
        using var provider = FrameHarness.Compose([module], StartupRefusalTests.SettingsWithKey([module]));

        Assert.NotNull(provider.GetService<TypedClientModuleClient>());
    }

    /// <summary>
    /// contract-005 · G-17 round 3 — a provider owns the types of its namespace and those under it, in its own assembly, so a
    /// provider declared in a namespace that holds another provider's would own that provider's types and could register over
    /// them. The pair refuses startup, naming both. Two providers in one namespace of one assembly are one provider's parts —
    /// this suite's own test modules, the end-to-end test host's two — and start, as the rows above show.
    /// </summary>
    [Fact]
    public void G17_a_provider_whose_namespace_holds_another_providers_refuses_startup_naming_both()
    {
        IProviderModule[] modules = [new Nesting.OuterNamespaceModule(), new Nesting.Inner.InnerNamespaceModule()];
        var message = FrameHarness.Refusal(modules, StartupRefusalTests.SettingsWithKey(modules));

        Assert.Contains(
            "'Outer' (McpServerTemplate.Tests.Frame.Nesting) holds 'Inner' (McpServerTemplate.Tests.Frame.Nesting.Inner)",
            message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (SEC-R3-01) — defence in depth. Should an HttpContextFactory that forges every request's
    /// principal be in place anyway — put beside the composition here, where no provider can reach — a request to /mcp with no
    /// token is still refused 401 with the metadata challenge: /mcp's policy names the router, which puts what the bearer
    /// schemes made of the request in place of the principal it carried. The finding's forged principal read resources and
    /// listed tools with no token. The control: a request with a token is served, the forging factory still in place.
    /// </summary>
    [Fact]
    public async Task G17_a_principal_the_bearer_schemes_did_not_produce_is_refused_at_mcp()
    {
        using var corp = new TestIdentityProvider("corp", "https://corp.example.com/");
        var forged = 0;
        await using var server = await McpServerTemplate.Tests.Identity.InProcessServer.StartAsync(
            [corp],
            modules: [.. BuiltInProviders.Create(), new TestModule()],
            outsideTheFrame: services => services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextFactory>(
                sp => new ForgingHttpContextFactory(
                    new Microsoft.AspNetCore.Http.DefaultHttpContextFactory(sp), () => Interlocked.Increment(ref forged))));

        foreach (var method in new[] { "initialize", "tools/list" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, McpServerTemplate.Tests.Identity.InProcessServer.McpPath)
            {
                Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = new { } }),
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            using var response = await server.Client.SendAsync(request);

            Assert.True(Volatile.Read(ref forged) > 0, "the forging factory made no request's context, so this row shows nothing about it.");
            Assert.True(
                response.StatusCode == System.Net.HttpStatusCode.Unauthorized,
                $"{method} with no token, as the forged principal, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            Assert.Contains("resource_metadata", string.Join(" ", response.Headers.WwwAuthenticate), StringComparison.Ordinal);
        }

        using var signedIn = await server.PostAsync(corp.MintToken(GateClient.Resource, scopes: ["weather:read"]));
        Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, signedIn.StatusCode);
    }

    [Fact]
    public void T13_a_filter_the_frame_did_not_install_refuses_startup()
    {
        // Past registration, the installed filters are compared with the frame's own: a filter
        // that got in by any other route is named.
        using var provider = FrameHarness.Compose(BuiltInProviders.Create());
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        McpRequestFilter<CallToolRequestParams, CallToolResult> foreign = next => next;
        options.Filters.Request.CallToolFilters.Add(foreign);

        var message = Assert.Throws<ConfigurationException>(() => FrameIntegrity.VerifyInstalled(
            options, provider.GetRequiredService<FrameManifest>(), BuiltInProviders.Create())).Message;

        Assert.Contains("CallToolFilters", message, StringComparison.Ordinal);
        Assert.Contains(nameof(FrameIntegrityTests), message, StringComparison.Ordinal);
    }

    [Fact]
    public void T13_the_request_kind_gate_must_be_first()
    {
        using var provider = FrameHarness.Compose(BuiltInProviders.Create());
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        options.Filters.Message.IncomingFilters.Insert(0, next => next);

        var message = Assert.Throws<ConfigurationException>(() => FrameIntegrity.VerifyInstalled(
            options, provider.GetRequiredService<FrameManifest>(), BuiltInProviders.Create())).Message;

        Assert.Contains("not the first incoming message filter", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_implementations_under_one_name_refuse_startup()
    {
        // A second tool answering to a declared tool's name would carry that tool's policy.
        using var provider = FrameHarness.Compose(BuiltInProviders.Create());
        var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        // The SDK's own collection refuses a second tool under one name; the container does not.
        // The container holds the real get_forecast, so the impostor arrives in a list of its own.
        options.ToolCollection = [McpServerTool.Create(() => "impostor", new McpServerToolCreateOptions { Name = "get_forecast" })];

        var message = Assert.Throws<ConfigurationException>(() => PolicyRegistry.Build(
            provider, BuiltInProviders.Create(), FrameHarness.Identity(),
            new ConfigurationBuilder().AddInMemoryCollection(FrameHarness.Settings(BuiltInProviders.Create())).Build(),
            options, confirmationConfigured: true)).Message;

        Assert.Contains("'get_forecast' is served by 2 different implementations", message, StringComparison.Ordinal);
    }

    // ── T-12 (G-11) ──

    [Theory]
    [InlineData("Providers:TestAct:IdentityProvidr", "corp", "Providers:TestAct:IdentityProvider")]
    [InlineData("Limits:PerPrincipalPerMinut", "10", "Limits:PerPrincipalPerMinute")]
    [InlineData("Authentication:AdminIdentityProvder", "corp", "Authentication:AdminIdentityProvider")]
    // contract-005 · G-12 (2) — HttpTransport is governed: a misspelt host allowlist or proxy used to
    // leave the server answering any Host, or trusting no proxy, and saying nothing.
    [InlineData("HttpTransport:AlowedHosts:0", "mcp.example.com", "HttpTransport:AllowedHosts:{n}")]
    [InlineData("HttpTransport:KnownProxys:0", "10.0.0.1", "HttpTransport:KnownProxies:{n}")]
    [InlineData("HttpTransport:AllowedHosts", "mcp.example.com", "HttpTransport:AllowedHosts:{n}")]
    public void T12_a_misspelled_governed_setting_refuses_startup_with_the_nearest_real_key(string key, string value, string nearest)
    {
        var module = new TestModule();
        var settings = StartupRefusalTests.SettingsWithKey([module]);
        settings[key] = value;

        var message = FrameHarness.Refusal([module], settings);

        Assert.Contains($"'{key}' is not a setting this server reads", message, StringComparison.Ordinal);
        Assert.Contains($"Did you mean '{nearest}'", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RateLimit:MaxCallsPerToolPerMinute", "Limits:PerPrincipalPerMinute")]
    [InlineData("Authentication:ApiKey", "bearer token")]
    // contract-005 · G-12 (2) — retired, in every environment, with the transport guard's reason.
    [InlineData("HttpTransport:Certificate:Path", "does not terminate TLS itself")]
    [InlineData("HttpTransport:Certificate:Subject", "does not terminate TLS itself")]
    public void T12_a_retired_setting_refuses_startup_and_says_what_replaced_it(string key, string replacement)
    {
        var module = new TestModule();
        var settings = StartupRefusalTests.SettingsWithKey([module]);
        settings[key] = "10";

        var message = FrameHarness.Refusal([module], settings);

        Assert.Contains($"'{key}' is retired", message, StringComparison.Ordinal);
        Assert.Contains(replacement, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2) — ASP.NET Core reads the Kestrel section on its own: its endpoints override
    /// the address HttpTransport:BindAddress binds, around the host allowlist chosen for that address,
    /// and its certificates and limits reach the listener past every check here. It is governed with no
    /// setting known in it, so any key there refuses startup, with the reason rather than a guess.
    /// </summary>
    [Theory]
    [InlineData("Kestrel:Endpoints:Web:Url", "http://0.0.0.0:3001")]
    [InlineData("Kestrel:Certificates:Default:Path", "/etc/certs/server.pfx")]
    [InlineData("Kestrel:Limits:MaxRequestBodySize", "1000000000")]
    public void T12_a_kestrel_setting_refuses_startup_with_the_reason(string key, string value)
    {
        var module = new TestModule();
        var settings = StartupRefusalTests.SettingsWithKey([module]);
        settings[key] = value;

        var message = FrameHarness.Refusal([module], settings);

        Assert.Contains($"'{key}' is not a setting this server honours", message, StringComparison.Ordinal);
        Assert.Contains("HttpTransport:BindAddress", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Did you mean", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2) — the control for the rows above: every transport setting the server
    /// reads passes the allowlist, so a refusal there is the misspelling's, not the section's.
    /// </summary>
    [Fact]
    public void T12_every_transport_setting_the_server_reads_is_known()
    {
        var module = new TestModule();
        var settings = StartupRefusalTests.SettingsWithKey([module]);
        settings["HttpTransport:Port"] = "3001";
        settings["HttpTransport:BindAddress"] = "0.0.0.0";
        settings["HttpTransport:AllowedHosts:0"] = "mcp.example.com";
        settings["HttpTransport:AllowedOrigins:0"] = "https://app.example.com";
        settings["HttpTransport:KnownProxies:0"] = "10.0.0.1";
        settings["HttpTransport:KnownNetworks:0"] = "10.0.0.0/8";

        // An empty list, as the shipped appsettings.json writes "AllowedOrigins": [], is its section's
        // key with an empty value: it means no origins, and is not an unknown setting.
        settings["HttpTransport:AllowedOrigins"] = string.Empty;

        using var provider = FrameHarness.Compose([module], settings);
    }
}
