using System.Text.Json;
using McpServerTemplate.Providers.JsonPlaceholder.Models;
using ModelContextProtocol;

namespace McpServerTemplate.Providers.JsonPlaceholder;

/// <summary>
/// HTTP client for the JSONPlaceholder fake REST API.
///
/// Handles CRUD operations for posts, comments, and todos.
/// JSONPlaceholder is a free service that simulates a backend.
/// </summary>
public class JsonPlaceholderApiClient
{
    /// <summary>
    /// Web defaults: camelCase on the wire and case-insensitive matching on the way back.
    /// The models also name every property explicitly, so a response binds whether or not
    /// these options are in play. Belt and braces: the failure this guards against is silent —
    /// a case mismatch yields a fully-defaulted record that every tool reports as success.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;

    public JsonPlaceholderApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Sends a request and returns the body, turning every way the call can fail into an error
    /// that names a recovery.
    ///
    /// contract-001 · UC-3 — a call fails in two shapes, and both reach the model. Either the
    /// upstream answered with a status that is not success, or it never answered at all: the
    /// host does not resolve, the connection drops, the attempt runs out of time. Mapping only
    /// the first shape left the second reaching a caller as "An error occurred invoking
    /// 'get_blog_post'", which names nothing to do. Both are mapped here, in one place, so a
    /// new call cannot be written that handles one and forgets the other.
    /// </summary>
    private static async Task<string> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        string what,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send(cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response, what);
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new McpException(
                $"Unable to reach the JSONPlaceholder API for {what}. Check network connectivity "
                + "and try again; if it persists, confirm Providers:JsonPlaceholder:BaseUrl.", ex);
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException ex)
        {
            // The breaker has opened: the pipeline is no longer calling the upstream at all.
            // This is the one failure where retrying is guaranteed to be useless, so it must not
            // read like the transient ones above — and it is the failure a caller meets most
            // often, since every call after the breaker trips arrives here.
            throw new McpException(
                $"The upstream is unavailable and this server has stopped calling it for {what} "
                + "after repeated failures. Do not retry for a minute or so; nothing is reaching "
                + "the API until it recovers.", ex);
        }
        catch (Polly.Timeout.TimeoutRejectedException ex)
        {
            // The resilience pipeline exhausted its budget. This is neither an HTTP failure nor a
            // cancellation, so it reaches here as its own type and would otherwise escape unnamed.
            throw new McpException(
                $"The upstream did not answer for {what} within the time this server allows, "
                + "including retries. Try again shortly; if it persists the upstream is degraded.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Guarded on the token: a caller who cancelled gets their own cancellation back,
            // not a story about the upstream having failed.
            throw new McpException(
                $"The request for {what} timed out. Try again; if it keeps timing out the "
                + "upstream is overloaded and a smaller request may succeed.", ex);
        }
    }

    /// <summary>
    /// Turns a non-success response into an error a model can act on.
    ///
    /// contract-001 · G-4 — the framework's own status guard throws HttpRequestException carrying
    /// "Response status code does not indicate success: 500", which reaches the caller as
    /// "An error occurred invoking 'get_blog_post'" and names no recovery. The guarantee is that
    /// a tool either returns a populated model or throws with a recovery hint, so every status is
    /// mapped here, preserving the distinction that matters to a caller: wait and try again, or
    /// stop asking for something that is not there.
    /// </summary>
    private static void EnsureSuccess(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode)
            return;

        var statusCode = (int)response.StatusCode;

        throw new McpException(statusCode switch
        {
            404 => $"{what} does not exist upstream. Do not retry; check the id. "
                 + "JSONPlaceholder serves posts 1-100 and users 1-10.",
            429 => $"The upstream refused {what}: too many requests. Wait a minute and try again, "
                 + "reusing results you already have rather than re-fetching them.",
            >= 500 => $"The upstream is temporarily unavailable and could not serve {what}. "
                    + "Try again in a few moments; if it persists the service is down and no "
                    + "retry will help.",
            401 or 403 => $"The upstream refused {what} as unauthorised. That is a server "
                        + "configuration problem, not something to try again.",
            _ => $"The upstream returned HTTP {statusCode} for {what}. Try again; if it persists, "
               + "the request may no longer match what the API accepts.",
        });
    }

    private static StringContent JsonBody<T>(T value) =>
        new(JsonSerializer.Serialize(value, JsonOptions), System.Text.Encoding.UTF8, "application/json");

    /// <summary>
    /// Retrieve a single post by ID.
    /// </summary>
    public async Task<Post> GetPostAsync(int postId, CancellationToken cancellationToken = default)
    {
        var body = await SendAsync(
            ct => _httpClient.GetAsync($"/posts/{postId}", ct), $"post {postId}", cancellationToken);

        return JsonSerializer.Deserialize<Post>(body, JsonOptions)
            ?? throw new McpException(
                $"The upstream returned no post for id {postId}. Retry; if it persists, confirm "
                + "JsonPlaceholder:BaseUrl points at the JSONPlaceholder API and that the id exists.");
    }

    /// <summary>
    /// Create a new post.
    /// Returns the created post with an ID assigned by the API.
    /// </summary>
    public async Task<Post> CreatePostAsync(
        int userId,
        string title,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title cannot be empty", nameof(title));

        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Body cannot be empty", nameof(body));

        var post = new Post
        {
            UserId = userId,
            Title = title,
            Body = body
        };

        using var payload = JsonBody(post);
        var response = await SendAsync(
            ct => _httpClient.PostAsync("/posts", payload, ct), "the new post", cancellationToken);

        return JsonSerializer.Deserialize<Post>(response, JsonOptions)
            ?? throw new McpException(
                "The upstream accepted the post but returned no record of it. The post may or may not "
                + "have been created: read it back before retrying, or the retry will duplicate it.");
    }

    /// <summary>
    /// Retrieve comments for a specific post.
    /// </summary>
    public async Task<List<Comment>> GetPostCommentsAsync(int postId, CancellationToken cancellationToken = default)
    {
        var body = await SendAsync(
            ct => _httpClient.GetAsync($"/posts/{postId}/comments", ct),
            $"the comments on post {postId}",
            cancellationToken);

        return JsonSerializer.Deserialize<List<Comment>>(body, JsonOptions)
            ?? throw new McpException(
                $"The upstream returned no comment list for post {postId} — not an empty list, but no "
                + "list at all. Retry; if it persists, confirm the post id exists.");
    }

    /// <summary>
    /// Create a new comment on a post.
    /// </summary>
    public async Task<Comment> CreateCommentAsync(
        int postId,
        string name,
        string email,
        string body,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name cannot be empty", nameof(name));

        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email cannot be empty", nameof(email));

        if (string.IsNullOrWhiteSpace(body))
            throw new ArgumentException("Body cannot be empty", nameof(body));

        var comment = new Comment
        {
            PostId = postId,
            Name = name,
            Email = email,
            Body = body
        };

        using var payload = JsonBody(comment);
        var response = await SendAsync(
            ct => _httpClient.PostAsync("/comments", payload, ct), "the new comment", cancellationToken);

        return JsonSerializer.Deserialize<Comment>(response, JsonOptions)
            ?? throw new McpException(
                "The upstream accepted the comment but returned no record of it. The comment may or may "
                + "not have been created: read the post's comments back before retrying.");
    }

    /// <summary>
    /// Retrieve todos for a specific user.
    /// </summary>
    public async Task<List<Todo>> GetUserTodosAsync(int userId, CancellationToken cancellationToken = default)
    {
        var body = await SendAsync(
            ct => _httpClient.GetAsync($"/todos?userId={userId}", ct),
            $"the todos for user {userId}",
            cancellationToken);

        return JsonSerializer.Deserialize<List<Todo>>(body, JsonOptions)
            ?? throw new McpException(
                $"The upstream returned no todo list for user {userId} — not an empty list, but no list "
                + "at all. Retry; if it persists, confirm the user id exists.");
    }

    /// <summary>
    /// Create a new todo item for a user.
    /// </summary>
    public async Task<Todo> CreateTodoAsync(
        int userId,
        string title,
        bool completed = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Title cannot be empty", nameof(title));

        var todo = new Todo
        {
            UserId = userId,
            Title = title,
            Completed = completed
        };

        using var payload = JsonBody(todo);
        var response = await SendAsync(
            ct => _httpClient.PostAsync("/todos", payload, ct), "the new todo", cancellationToken);

        return JsonSerializer.Deserialize<Todo>(response, JsonOptions)
            ?? throw new McpException(
                "The upstream accepted the todo but returned no record of it. The todo may or may not "
                + "have been created: read the user's todos back before retrying.");
    }
}
