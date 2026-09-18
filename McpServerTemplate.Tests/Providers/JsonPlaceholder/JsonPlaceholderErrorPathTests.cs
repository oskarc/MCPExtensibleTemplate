using System.Net;
using System.Text;
using McpServerTemplate.Providers.JsonPlaceholder;
using ModelContextProtocol;

namespace McpServerTemplate.Tests.Providers.JsonPlaceholder;

/// <summary>
/// contract-001 · T-5, the error half (G-4) — "every tool returns a populated model or throws
/// with a recovery hint".
///
/// The record half was covered from the start; this half was not, and verification found the
/// gap: the client called EnsureSuccessStatusCode() ahead of every hint-bearing path, so an
/// upstream 500 surfaced as "Response status code does not indicate success: 500" and reached
/// the model as "An error occurred invoking 'get_blog_post'" — naming no recovery at all.
/// </summary>
public class JsonPlaceholderErrorPathTests
{
    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public StatusHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
    }

    private static JsonPlaceholderApiClient Client(HttpStatusCode status) =>
        new(new HttpClient(new StatusHandler(status))
        {
            BaseAddress = new Uri("https://jsonplaceholder.typicode.com"),
        });

    public static TheoryData<string, Func<JsonPlaceholderApiClient, Task>> EveryTool() => new()
    {
        { "get_blog_post", c => c.GetPostAsync(1) },
        { "get_post_comments", c => c.GetPostCommentsAsync(1) },
        { "get_user_todos", c => c.GetUserTodosAsync(1) },
        { "create_blog_post", c => c.CreatePostAsync(1, "t", "b") },
        { "add_post_comment", c => c.CreateCommentAsync(1, "n", "e@example.com", "b") },
        { "create_user_todo", c => c.CreateTodoAsync(1, "t") },
    };

    [Theory]
    [MemberData(nameof(EveryTool))]
    public async Task T5_an_upstream_failure_names_a_recovery(string tool, Func<JsonPlaceholderApiClient, Task> call)
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => call(Client(HttpStatusCode.InternalServerError)));

        // The framework's own message is the thing this guards against: it names a status code
        // and nothing a caller can act on.
        Assert.DoesNotContain(
            "Response status code does not indicate success",
            ex.Message,
            StringComparison.Ordinal);

        // A recovery the model can actually follow.
        Assert.Contains("try again", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(tool));
    }

    [Theory]
    [MemberData(nameof(EveryTool))]
    public async Task T5_a_missing_record_says_so_rather_than_reporting_a_status_code(
        string tool, Func<JsonPlaceholderApiClient, Task> call)
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => call(Client(HttpStatusCode.NotFound)));

        Assert.DoesNotContain(
            "Response status code does not indicate success",
            ex.Message,
            StringComparison.Ordinal);

        // Retrying a 404 is the wrong move, so the message must not invite it; it must say the
        // thing is not there.
        Assert.Contains("not", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(tool));
    }

    [Fact]
    public async Task T5_being_rate_limited_by_the_upstream_is_distinguishable_from_being_broken()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => Client(HttpStatusCode.TooManyRequests).GetPostAsync(1));

        Assert.Contains("too many", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// A handler that fails before any response exists — the upstream is unreachable, or the
    /// attempt ran out of time. No status code is involved, so status mapping never sees it.
    private sealed class FailingTransport : HttpMessageHandler
    {
        private readonly Exception _failure;

        public FailingTransport(Exception failure) => _failure = failure;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(_failure);
    }

    private static JsonPlaceholderApiClient FailingClient(Exception failure) =>
        new(new HttpClient(new FailingTransport(failure))
        {
            BaseAddress = new Uri("https://jsonplaceholder.typicode.com"),
        });

    [Theory]
    [MemberData(nameof(EveryTool))]
    public async Task T5_an_unreachable_upstream_names_a_recovery(string tool, Func<JsonPlaceholderApiClient, Task> call)
    {
        // The gap the status mapping left: a host that does not resolve never produces a status,
        // so the caller was told only "An error occurred invoking 'get_blog_post'".
        var ex = await Assert.ThrowsAsync<McpException>(
            () => call(FailingClient(new HttpRequestException("No such host is known."))));

        Assert.Contains("reach", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(tool));
    }

    [Theory]
    [MemberData(nameof(EveryTool))]
    public async Task T5_an_upstream_that_runs_out_of_time_names_a_recovery(
        string tool, Func<JsonPlaceholderApiClient, Task> call)
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => call(FailingClient(new TaskCanceledException("The request timed out."))));

        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(tool));
    }

    [Fact]
    public async Task T5_a_caller_who_cancels_is_not_told_the_upstream_failed()
    {
        // Cancellation is the caller's own doing; dressing it up as an upstream fault would
        // send the model chasing a problem that does not exist.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FailingClient(new TaskCanceledException()).GetPostAsync(1, cancelled.Token));
    }

    [Theory]
    [MemberData(nameof(EveryTool))]
    public async Task T5_an_open_circuit_names_a_recovery(string tool, Func<JsonPlaceholderApiClient, Task> call)
    {
        // Once the breaker opens, the pipeline stops calling the upstream and raises this
        // instead. It is neither an HTTP failure nor a timeout, so it escaped the mapping and
        // reached the caller as "An error occurred invoking 'get_blog_post'" — on every call
        // after the first, which is exactly when a model most needs to be told to stop.
        var ex = await Assert.ThrowsAsync<McpException>(
            () => call(FailingClient(new Polly.CircuitBreaker.BrokenCircuitException(
                "The circuit is now open and is not allowing calls."))));

        Assert.DoesNotContain("circuit", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(tool));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-1)]
    public async Task T5_an_out_of_range_id_names_the_valid_range(int postId)
    {
        // The tools file's own guidance says to throw McpException with a recovery hint. It threw
        // ArgumentException, which the model receives as the generic failure text — so a caller
        // that picked a bad id was never told what a good one looks like.
        // The range check lives on the tool, which is the surface a model calls — not on the
        // client underneath it.
        var ex = await Assert.ThrowsAsync<McpException>(
            () => JsonPlaceholderTools.GetBlogPost(Client(HttpStatusCode.OK), postId));

        Assert.Contains("1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("100", ex.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, Func<JsonPlaceholderApiClient, Task>> EveryWriteToolWithABlankRequiredArgument() => new()
    {
        { "create_blog_post/title", c => c.CreatePostAsync(1, "", "body") },
        { "create_blog_post/body", c => c.CreatePostAsync(1, "title", "   ") },
        { "add_post_comment/name", c => c.CreateCommentAsync(1, "", "e@example.com", "body") },
        { "add_post_comment/email", c => c.CreateCommentAsync(1, "name", "  ", "body") },
        { "add_post_comment/body", c => c.CreateCommentAsync(1, "name", "e@example.com", "") },
        { "create_user_todo/title", c => c.CreateTodoAsync(1, "  ") },
    };

    [Theory]
    [MemberData(nameof(EveryWriteToolWithABlankRequiredArgument))]
    public async Task T5_a_blank_required_argument_names_what_is_missing(
        string which, Func<JsonPlaceholderApiClient, Task> call)
    {
        // These threw ArgumentException, which a model receives as the bare "An error occurred
        // invoking 'create_blog_post'." — telling it nothing about which argument to supply, on
        // the one class of failure it can actually fix by itself.
        var ex = await Assert.ThrowsAsync<McpException>(() => call(Client(HttpStatusCode.OK)));

        var argument = which.Split('/')[1];
        Assert.Contains(argument, ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
