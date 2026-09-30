using McpServerTemplate.Infrastructure;

namespace McpServerTemplate.Tests.Infrastructure;

/// <summary>
/// contract-005 · T-3 — a value the server did not choose (an unregistered token's issuer) is written into a log line only
/// once nothing in it can end the line, forge the next, rewrite a terminal or disguise the text, and only up to a length.
///
/// contract-005 · G-17 round 3 (SEC-R3-04, SEC-R3-07) — written escaped, not with those characters removed: removed, a
/// name that differs from a real one by an invisible character was written as the real one. And the backslash is escaped
/// too, so every escape reads back to exactly what was sent.
/// </summary>
public sealed class LogSafeTests
{
    /// <summary>Each way a value could break or disguise a line, with how it is written.</summary>
    public static TheoryData<string, string, string> Hostile() => new()
    {
        { "a line break, forging the next line", "https://evil.example\r\n[00:00:00 WRN] forged", "https://evil.example\\r\\n[00:00:00 WRN] forged" },
        { "a lone line feed and a tab", "https://evil.example\n\tforged", "https://evil.example\\n\\tforged" },
        { "a terminal escape sequence", "https://evil.example\u001b[2Jforged", "https://evil.example\\u001B[2Jforged" },
        { "NUL and DEL", "https://evil\u0000.example\u007f", "https://evil\\u0000.example\\u007F" },
        { "C1 controls, next line among them", "https://evil.example\u0085\u009bforged", "https://evil.example\\u0085\\u009Bforged" },
        { "a direction override", "https://\u202Eelpmaxe.live", "https://\\u202Eelpmaxe.live" },
        { "the Unicode line and paragraph separators", "https://evil.example\u2028\u2029forged", "https://evil.example\\u2028\\u2029forged" },
        { "a zero-width joiner and a byte order mark", "https://evil\u200D.example\uFEFF", "https://evil\\u200D.example\\uFEFF" },
        { "a tag character, beyond the basic plane", "https://evil.example\U000E0020", "https://evil.example\\U000E0020" },
        { "a backslash, so a caller's own escape reads as written", "tool\\r\\n[00:00:00 WRN] forged", "tool\\\\r\\\\n[00:00:00 WRN] forged" },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Characters_that_break_or_disguise_a_line_are_written_escaped(string what, string value, string written)
    {
        var safe = LogSafe.Text(value);
        Assert.True(safe == written, $"{what}: '{value}' was made '{safe}', not '{written}'.");
        Assert.False(LogSafe.BreaksALine(safe), $"{what}: '{safe}' still breaks a line.");
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (SEC-R3-04) — the finding's pairs: a tool name, a resource URI, an issuer and a principal
    /// that differ from real ones only by an invisible character were written as the real ones. Each is written as itself now,
    /// escaped, and so distinguishably; the real ones stay as they are.
    /// </summary>
    [Theory]
    [InlineData("get_forecast", "get_fore\u200Bcast", "get_fore\\u200Bcast")]
    [InlineData("smhi://coverage-area", "smhi://coverage\u00AD-area", "smhi://coverage\\u00AD-area")]
    [InlineData("https://corp.example.com/", "https://corp.example.com/\u200B", "https://corp.example.com/\\u200B")]
    [InlineData("corp:user-1", "corp:user\u2060-1", "corp:user\\u2060-1")]
    public void A_value_differing_from_a_real_one_by_an_invisible_character_is_written_as_itself(string real, string sent, string written)
    {
        Assert.Equal(real, LogSafe.Text(real));
        Assert.Equal(written, LogSafe.Text(sent));
        Assert.NotEqual(LogSafe.Text(real), LogSafe.Text(sent));
        Assert.Equal(written, SafeText.Value(sent).ToString());
    }

    [Fact]
    public void An_ordinary_issuer_is_kept_as_it_is()
    {
        // Positive control: what an issuer looks like, non-ASCII letters included, passes untouched.
        Assert.Equal("https://idp.example.com/realms/corp", LogSafe.Text("https://idp.example.com/realms/corp"));
        Assert.Equal("https://idp.ex\u00E4mple.se/tenant/\u03A9", LogSafe.Text("https://idp.ex\u00E4mple.se/tenant/\u03A9"));
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

    /// <summary>
    /// contract-005 · G-17 round 3 (SEC-R3-07) — the finding's pairs: a real line break, and a caller's own text written as its
    /// escape — a backslash then r, a backslash then n — were escaped the same, so neither a reader nor a parser could tell
    /// them apart; so were a real direction override and the six characters of its escape. The backslash is escaped now, as
    /// \\, so they differ, and each reads back to what it was.
    /// </summary>
    [Theory]
    [InlineData("tool\r\n[99:99:99 WRN] forged", "tool\\r\\n[99:99:99 WRN] forged")]
    [InlineData("\u202E", "\\u202E")]
    [InlineData("a real \n, and a written \\n", "a real \\n, and a written \\\\n")]
    public void Escaped_writes_a_backslash_as_an_escape_so_no_two_values_are_written_the_same(string real, string written)
    {
        var escapedReal = LogSafe.Escaped(real);
        Assert.NotEqual(escapedReal, LogSafe.Escaped(written));
        Assert.Equal(real, Unescaped(escapedReal));
        Assert.Equal(written, Unescaped(LogSafe.Escaped(written)));

        // A backslash alone breaks no line, but it is escaped: BreaksALine and NeedsEscaping say which is which.
        Assert.False(LogSafe.BreaksALine("C:\\logs"));
        Assert.True(LogSafe.NeedsEscaping("C:\\logs"));
        Assert.Equal("C:\\\\logs", LogSafe.Escaped("C:\\logs"));
    }

    /// <summary>What <see cref="LogSafe.Escaped"/> wrote, read back: the reading the escapes are made for.</summary>
    private static string Unescaped(string escaped) =>
        System.Text.RegularExpressions.Regex.Replace(escaped, @"\\(\\|r|n|t|u[0-9A-F]{4}|U[0-9A-F]{8})", match => match.Groups[1].Value switch
        {
            "\\" => "\\",
            "r" => "\r",
            "n" => "\n",
            "t" => "\t",
            var code => char.ConvertFromUtf32(int.Parse(code[1..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture)),
        });

    [Fact]
    public void Escaped_shows_an_unpaired_surrogate_as_itself_and_leaves_an_ordinary_value_whole()
    {
        Assert.Equal("a\\uD800b", LogSafe.Escaped("a\uD800b"));

        // Positive control: an ordinary value, non-ASCII letters and a surrogate pair included, is the same instance, and
        // nothing is cut however long it is.
        const string ordinary = "https://idp.ex\u00E4mple.se/tenant/\u03A9 \U0001F600";
        Assert.Same(ordinary, LogSafe.Escaped(ordinary));
        var longValue = new string('a', 5000);
        Assert.Same(longValue, LogSafe.Escaped(longValue));
        Assert.False(LogSafe.BreaksALine(ordinary));
        Assert.False(LogSafe.NeedsEscaping(ordinary));
    }

    [Fact]
    public void A_long_value_is_cut_and_the_cut_marked()
    {
        var value = "https://" + new string('a', 5000);
        var safe = LogSafe.Text(value);

        Assert.Equal(LogSafe.MaxLength + 1, safe.Length);
        Assert.Equal(value[..LogSafe.MaxLength] + "\u2026", safe);

        // A surrogate pair is never split by the cut; contract-005 · G-17 round 3 (SEC-R3-04) — nor is an escape, and an
        // unpaired half is written as the escape of itself.
        var emoji = LogSafe.Text(new string('a', LogSafe.MaxLength - 1) + "\U0001F600");
        Assert.Equal(new string('a', LogSafe.MaxLength - 1) + "\u2026", emoji);
        var escape = LogSafe.Text(new string('a', LogSafe.MaxLength - 3) + "\u200B");
        Assert.Equal(new string('a', LogSafe.MaxLength - 3) + "\u2026", escape);
        Assert.Equal("a\\uD800b", LogSafe.Text("a\uD800b"));

        // Many invisible characters are cut by the length they are written at, not by how many there are.
        var invisible = LogSafe.Text(new string('\u200B', 1000));
        Assert.True(invisible.Length <= LogSafe.MaxLength + 1, $"1000 invisible characters were written as {invisible.Length} characters.");
        Assert.EndsWith("\u2026", invisible, StringComparison.Ordinal);
    }

    /// <summary>
    /// contract-005 · G-17 round 3 (SEC-R3-04) — the frame's own text: a line's words escaped whole, each value in it escaped
    /// and cut, and text escaped already kept as it is, never escaped twice.
    /// </summary>
    [Fact]
    public void A_line_the_frame_writes_escapes_each_value_in_it_once()
    {
        const string sent = "get_fore\u200Bcast";
        var line = SafeText.Line($"You may not use the tool '{sent}', or it does not exist.");
        Assert.Equal("You may not use the tool 'get_fore\\u200Bcast', or it does not exist.", line.ToString());

        var nested = SafeText.Line($"authz_fail (rule: not-permitted). {line}");
        Assert.Equal("authz_fail (rule: not-permitted). You may not use the tool 'get_fore\\u200Bcast', or it does not exist.", nested.ToString());

        var cut = SafeText.Line($"The argument '{new string('a', 5000)}' must be a finite number.");
        Assert.Equal($"The argument '{new string('a', LogSafe.MaxLength)}\u2026' must be a finite number.", cut.ToString());

        Assert.Equal("a; b\\u200B", SafeText.Join("; ", [SafeText.Value("a"), SafeText.Value("b\u200B")]).ToString());
        Assert.Equal("words\\\\and\\n", SafeText.Of("words\\and\n").ToString());
    }
}
