using System.Text.Json;
using McpServerTemplate.Providers.JsonPlaceholder.Models;

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
    /// Retrieve a single post by ID.
    /// </summary>
    public async Task<Post> GetPostAsync(int postId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"/posts/{postId}", cancellationToken);
        response.EnsureSuccessStatusCode();

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
        response.EnsureSuccessStatusCode();

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
        response.EnsureSuccessStatusCode();

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
        response.EnsureSuccessStatusCode();

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
        response.EnsureSuccessStatusCode();

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
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<Todo>(responseContent, JsonOptions)
            ?? throw new InvalidOperationException(
                "The upstream accepted the todo but returned no record of it. The todo may or may not " +
                "have been created: read the user's todos back before retrying.");
    }
}
