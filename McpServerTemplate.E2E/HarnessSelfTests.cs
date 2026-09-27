using McpServerTemplate.E2E.Harness;

namespace McpServerTemplate.E2E;

/// <summary>
/// The harness's own checks that need no environment: nothing here starts a container.
/// </summary>
public sealed class HarnessSelfTests
{
    /// <summary>
    /// contract-005 · G-8 — a key outside the sections the server's own startup check governs is one
    /// the server would ignore silently, so the delta refuses it before any container starts, naming
    /// the key and the nearest key the server is declared to read. Serilog:WriteTo:1:Args:pth is one
    /// that left a server running as if it were not there; the others are misspellings of the rest of
    /// what the server reads outside the governed sections.
    /// </summary>
    [Theory]
    [InlineData("Serilog:WriteTo:1:Args:pth", "Serilog:WriteTo:{n}:Args:path")]
    [InlineData("Serilog__MinimumLevel__Defualt", "Serilog:MinimumLevel:Default")]
    [InlineData("Transprot", "Transport")]
    [InlineData("SSL_CERT_FIEL", "SSL_CERT_FILE")]
    public void A_misspelt_key_outside_the_governed_sections_is_refused_naming_the_nearest_declared_key(string key, string nearest)
    {
        // Positive controls: a declared key and a governed key both pass, so the refusal below is
        // this key's and not a delta that refuses everything.
        SettingsDelta.None.Set("Serilog:WriteTo:1:Args:path", "logs/e2e-.log");
        SettingsDelta.None.Set("Authentication:Resourse", "left for the server's own check");

        var set = Assert.Throws<ArgumentException>(() => SettingsDelta.None.Set(key, "x"));
        Assert.Contains($"'{key}'", set.Message, StringComparison.Ordinal);
        Assert.Contains($"Did you mean '{nearest}'?", set.Message, StringComparison.Ordinal);

        var removed = Assert.Throws<ArgumentException>(() => SettingsDelta.None.Remove(key));
        Assert.Contains($"'{key}'", removed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-12 (2) — HttpTransport is governed by the server's own check now, so a
    /// misspelt transport key is passed through untouched: refusing it is the server's job, and a test
    /// of that refusal needs the key to arrive. These two were refused here before.
    /// </summary>
    [Theory]
    [InlineData("HttpTransport:AlowedHosts:0")]
    [InlineData("HttpTransport__KnownProxys__0")]
    public void A_misspelt_transport_key_reaches_the_server(string key)
    {
        var delta = SettingsDelta.None.Set(key, "x");
        Assert.Equal("x", delta.Changes[key.Replace("__", ":", StringComparison.Ordinal)]);
    }

    /// <summary>contract-005 · G-8 — a key under no section at all is refused the same way.</summary>
    [Fact]
    public void An_invented_top_level_key_is_refused()
    {
        var refusal = Assert.Throws<ArgumentException>(() => SettingsDelta.None.Set("Bogus:Key", "1"));
        Assert.Contains("'Bogus:Key'", refusal.Message, StringComparison.Ordinal);
    }
}
