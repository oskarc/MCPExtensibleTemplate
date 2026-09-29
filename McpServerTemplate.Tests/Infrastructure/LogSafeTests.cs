using McpServerTemplate.Infrastructure;

namespace McpServerTemplate.Tests.Infrastructure;

/// <summary>
/// contract-005 · T-3 — a value the server did not choose (an unregistered token's issuer) is written into a log line only
/// once nothing in it can end the line, forge the next, rewrite a terminal or disguise the text, and only up to a length.
/// </summary>
public sealed class LogSafeTests
{
    /// <summary>Each way a value could break or disguise a line, with what is left of it.</summary>
    public static TheoryData<string, string, string> Hostile() => new()
    {
        { "a line break, forging the next line", "https://evil.example\r\n[00:00:00 WRN] forged", "https://evil.example[00:00:00 WRN] forged" },
        { "a lone line feed and a tab", "https://evil.example\n\tforged", "https://evil.exampleforged" },
        { "a terminal escape sequence", "https://evil.example\u001b[2Jforged", "https://evil.example[2Jforged" },
        { "NUL and DEL", "https://evil\u0000.example\u007f", "https://evil.example" },
        { "C1 controls, next line among them", "https://evil.example\u0085\u009bforged", "https://evil.exampleforged" },
        { "a direction override", "https://\u202Eelpmaxe.live", "https://elpmaxe.live" },
        { "the Unicode line and paragraph separators", "https://evil.example\u2028\u2029forged", "https://evil.exampleforged" },
        { "a zero-width joiner and a byte order mark", "https://evil\u200D.example\uFEFF", "https://evil.example" },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Characters_that_break_or_disguise_a_line_are_removed(string what, string value, string kept)
    {
        var safe = LogSafe.Text(value);
        Assert.True(safe == kept, $"{what}: '{value}' was made '{safe}', not '{kept}'.");
    }

    [Fact]
    public void An_ordinary_issuer_is_kept_as_it_is()
    {
        // Positive control: what an issuer looks like, non-ASCII letters included, passes untouched.
        Assert.Equal("https://idp.example.com/realms/corp", LogSafe.Text("https://idp.example.com/realms/corp"));
        Assert.Equal("https://idp.exämple.se/tenant/Ω", LogSafe.Text("https://idp.exämple.se/tenant/Ω"));
    }

    [Fact]
    public void A_long_value_is_cut_and_the_cut_marked()
    {
        var value = "https://" + new string('a', 5000);
        var safe = LogSafe.Text(value);

        Assert.Equal(LogSafe.MaxLength + 1, safe.Length);
        Assert.Equal(value[..LogSafe.MaxLength] + "…", safe);

        // A surrogate pair is never split by the cut, and an unpaired half is shown as U+FFFD.
        var emoji = LogSafe.Text(new string('a', LogSafe.MaxLength - 1) + "\U0001F600");
        Assert.Equal(new string('a', LogSafe.MaxLength - 1) + "…", emoji);
        Assert.Equal("a\uFFFDb", LogSafe.Text("a\uD800b"));
    }
}
