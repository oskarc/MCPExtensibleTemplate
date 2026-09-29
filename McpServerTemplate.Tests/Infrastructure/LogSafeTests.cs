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

    /// <summary>
    /// contract-005 · G-17 round 1 — a URL an operator configured is echoed in a refusal as written, but for the parts that
    /// can carry a credential: user information, the query, and (batch B) the fragment, where a browser's implicit-flow URL
    /// carries the access token. Read from the text: user information first, then whichever of ? and # comes first, so a ?
    /// or # inside a password, or a ? inside a fragment, is never taken for where a later part starts.
    /// </summary>
    [Theory]
    [InlineData("nothing to hide", "https://login.example.com/realms/corp", "https://login.example.com/realms/corp")]
    [InlineData("user information", "https://ops:S3cr3t@login.example.com/realms/corp", "https://***@login.example.com/realms/corp")]
    [InlineData("a query", "https://login.example.com/?client_secret=S3cr3t", "https://login.example.com/?***")]
    [InlineData("a query, then a fragment", "https://login.example.com/cb?state=1#access_token=S3cr3t", "https://login.example.com/cb?***")]
    [InlineData("a fragment", "https://login.example.com/cb#access_token=S3cr3t", "https://login.example.com/cb#***")]
    [InlineData("a fragment holding a ?", "https://login.example.com/cb#access_token=S3cr3t?state=1", "https://login.example.com/cb#***")]
    [InlineData("a password holding a #, and a fragment", "https://ops:S3#cr3t@login.example.com/#access_token=S3cr3t", "https://***@login.example.com/#***")]
    [InlineData("no scheme, and a fragment", "login.example.com#access_token=S3cr3t", "login.example.com#***")]
    public void A_urls_user_information_query_and_fragment_are_written_as_stars(string what, string url, string written)
    {
        var safe = LogSafe.Url(url);
        Assert.True(safe == written, $"{what}: '{url}' was written '{safe}', not '{written}'.");
        Assert.DoesNotContain("S3cr3t", safe, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-17 round 2 (SEC-R2-03) — what the log pipeline writes on the way to its sinks keeps every character but
    /// shows the ones that break or disguise a line as escapes, so what was there still shows and nothing starts a line.
    /// </summary>
    public static TheoryData<string, string, string> Escapes() => new()
    {
        { "a line break, forging the next line", "x\r\n[99:99:99 WRN] forged", "x\\r\\n[99:99:99 WRN] forged" },
        { "a tab", "x\ty", "x\\ty" },
        { "a terminal escape sequence", "x\u001b[2Jforged", "x\\u001B[2Jforged" },
        { "next line and a C1 control", "x\u0085\u009by", "x\\u0085\\u009By" },
        { "a direction override", "https://\u202Eelpmaxe.live", "https://\\u202Eelpmaxe.live" },
        { "the Unicode line and paragraph separators", "x\u2028\u2029y", "x\\u2028\\u2029y" },
        { "a tag character, beyond the basic plane", "x\U000E0041y", "x\\U000E0041y" },
    };

    [Theory]
    [MemberData(nameof(Escapes))]
    public void Escaped_writes_each_character_that_breaks_or_disguises_a_line_as_an_escape(string what, string value, string written)
    {
        var escaped = LogSafe.Escaped(value);
        Assert.True(escaped == written, $"{what}: '{value}' was escaped as '{escaped}', not '{written}'.");
        Assert.True(LogSafe.BreaksALine(value), $"{what}: '{value}' was not seen to break a line.");
        Assert.False(LogSafe.BreaksALine(escaped), $"{what}: '{escaped}' still breaks a line.");
    }

    [Fact]
    public void Escaped_shows_an_unpaired_surrogate_as_itself_and_leaves_an_ordinary_value_whole()
    {
        Assert.Equal("a\\uD800b", LogSafe.Escaped("a\uD800b"));

        // Positive control: an ordinary value, non-ASCII letters and a surrogate pair included, is the same instance, and
        // nothing is cut however long it is.
        const string ordinary = "https://idp.exämple.se/tenant/Ω \U0001F600";
        Assert.Same(ordinary, LogSafe.Escaped(ordinary));
        var longValue = new string('a', 5000);
        Assert.Same(longValue, LogSafe.Escaped(longValue));
        Assert.False(LogSafe.BreaksALine(ordinary));
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
