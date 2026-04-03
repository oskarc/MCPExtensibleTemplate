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
        var post = JsonSerializer.Deserialize<Post>(content)
            ?? throw new InvalidOperationException($"Failed to deserialize post {postId}");

        return post;
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

        var json = JsonSerializer.Serialize(post);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("/posts", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var createdPost = JsonSerializer.Deserialize<Post>(responseContent)
            ?? throw new InvalidOperationException("Failed to deserialize created post");

        return createdPost;
    }

    /// <summary>
    /// Retrieve comments for a specific post.
    /// </summary>
    public async Task<List<Comment>> GetPostCommentsAsync(int postId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"/posts/{postId}/comments", cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var comments = JsonSerializer.Deserialize<List<Comment>>(content)
            ?? new List<Comment>();

        return comments;
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

        var json = JsonSerializer.Serialize(comment);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("/comments", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var createdComment = JsonSerializer.Deserialize<Comment>(responseContent)
            ?? throw new InvalidOperationException("Failed to deserialize created comment");

        return createdComment;
    }

    /// <summary>
    /// Retrieve todos for a specific user.
    /// </summary>
    public async Task<List<Todo>> GetUserTodosAsync(int userId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"/todos?userId={userId}", cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var todos = JsonSerializer.Deserialize<List<Todo>>(content)
            ?? new List<Todo>();

        return todos;
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

        var json = JsonSerializer.Serialize(todo);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync("/todos", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var createdTodo = JsonSerializer.Deserialize<Todo>(responseContent)
            ?? throw new InvalidOperationException("Failed to deserialize created todo");

        return createdTodo;
    }
}
