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

    /// <summary>
    /// Retrieve a single post by ID.
    /// </summary>
    public async Task<Post> GetPostAsync(int postId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"/posts/{postId}", cancellationToken);
        EnsureSuccess(response, $"post {postId}");

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<Post>(content, JsonOptions)
            ?? throw new InvalidOperationException(
                $"The upstream returned no post for id {postId}. Retry; if it persists, confirm " +
                "JsonPlaceholder:BaseUrl points at the JSONPlaceholder API and that the id exists.");
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

        var json = JsonSerializer.Serialize(post, JsonOptions);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("/posts", content, cancellationToken);
        EnsureSuccess(response, "the new post");

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<Post>(responseContent, JsonOptions)
            ?? throw new InvalidOperationException(
                "The upstream accepted the post but returned no record of it. The post may or may not " +
                "have been created: read it back before retrying, or the retry will duplicate it.");
    }

    /// <summary>
    /// Retrieve comments for a specific post.
    /// </summary>
    public async Task<List<Comment>> GetPostCommentsAsync(int postId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"/posts/{postId}/comments", cancellationToken);
        EnsureSuccess(response, $"the comments on post {postId}");

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<Comment>>(content, JsonOptions)
            ?? throw new InvalidOperationException(
                $"The upstream returned no comment list for post {postId} — not an empty list, but no " +
                "list at all. Retry; if it persists, confirm the post id exists.");
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

        var json = JsonSerializer.Serialize(comment, JsonOptions);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("/comments", content, cancellationToken);
        EnsureSuccess(response, "the new comment");

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<Comment>(responseContent, JsonOptions)
            ?? throw new InvalidOperationException(
                "The upstream accepted the comment but returned no record of it. The comment may or may " +
                "not have been created: read the post's comments back before retrying.");
    }

    /// <summary>
    /// Retrieve todos for a specific user.
    /// </summary>
    public async Task<List<Todo>> GetUserTodosAsync(int userId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"/todos?userId={userId}", cancellationToken);
        EnsureSuccess(response, $"the todos for user {userId}");

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<Todo>>(content, JsonOptions)
            ?? throw new InvalidOperationException(
                $"The upstream returned no todo list for user {userId} — not an empty list, but no list " +
                "at all. Retry; if it persists, confirm the user id exists.");
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

        var json = JsonSerializer.Serialize(todo, JsonOptions);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("/todos", content, cancellationToken);
        EnsureSuccess(response, "the new todo");

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<Todo>(responseContent, JsonOptions)
            ?? throw new InvalidOperationException(
                "The upstream accepted the todo but returned no record of it. The todo may or may not " +
                "have been created: read the user's todos back before retrying.");
    }
}
