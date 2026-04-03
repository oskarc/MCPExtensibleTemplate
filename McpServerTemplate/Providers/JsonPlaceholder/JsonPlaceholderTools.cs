using System.ComponentModel;
using ModelContextProtocol.Server;
using McpServerTemplate.Providers.JsonPlaceholder.Models;

namespace McpServerTemplate.Providers.JsonPlaceholder;

/// <summary>
/// MCP Tools for the JSONPlaceholder fake REST API provider.
///
/// TEMPLATE GUIDANCE — TOOL DESIGN PRINCIPLES:
///
///   1. NAME: Verb + Noun — "CreateBlogPost", not "Post" or "CreateData"
///
///   2. DESCRIPTION: Say WHEN to use, not just what it does.
///      Bad:  "Creates a post"
///      Good: "Use when the user wants to create a blog post for demonstration..."
///
///   3. PARAMETERS: [Description] must include examples, valid ranges, units.
///      Bad:  int userId
///      Good: [Description("User ID (1-10 for demo data, e.g. 1 for John)")]
///
///   4. OUTPUT: Formatted text optimized for LLM consumption.
///      Use emojis and structured text, not raw JSON.
///
///   5. ERRORS: Throw McpException with recovery hints.
///
///   6. GRANULARITY: One tool per coherent user action.
///
/// DI NOTE: The SDK automatically resolves registered services as method parameters.
/// <c>JsonPlaceholderApiClient</c> is injected because it was registered in <c>JsonPlaceholderServiceRegistration</c>.
/// </summary>
[McpServerToolType]
public static class JsonPlaceholderTools
{
    [McpServerTool, Description(
        "Use when you need to retrieve a specific blog post to read its content. " +
        "JSONPlaceholder provides 100 sample posts (IDs 1-100) for demonstration purposes. " +
        "Returns the post title, author, and full body text.")]
    public static async Task<string> GetBlogPost(
        JsonPlaceholderApiClient client,
        [Description("The ID of the post to retrieve (valid range 1-100, e.g. 1, 42, 100).")]
        int postId,
        CancellationToken cancellationToken = default)
    {
        if (postId < 1 || postId > 100)
            throw new ArgumentException("Post ID must be between 1 and 100.", nameof(postId));

        var post = await client.GetPostAsync(postId, cancellationToken);
        return JsonPlaceholderFormatters.FormatPost(post);
    }

    [McpServerTool, Description(
        "Use when you want to create a new blog post. This demonstrates POST request functionality. " +
        "The post will be created in the JSONPlaceholder API simulation. " +
        "Returns the created post with an ID assigned by the API.")]
    public static async Task<string> CreateBlogPost(
        JsonPlaceholderApiClient client,
        [Description("User ID for the post author (valid range 1-10, e.g. 1 for user John).")]
        int userId,
        [Description("Title of the blog post (e.g. 'My First Post', 'Tips for Developers').")]
        string title,
        [Description("Main content/body of the blog post (e.g. 'This is about...').")]
        string body,
        CancellationToken cancellationToken = default)
    {
        if (userId < 1 || userId > 10)
            throw new ArgumentException("User ID must be between 1 and 10.", nameof(userId));

        var post = await client.CreatePostAsync(userId, title, body, cancellationToken);
        return JsonPlaceholderFormatters.FormatPostCreated(post);
    }

    [McpServerTool, Description(
        "Use when you need to retrieve comments on a specific blog post. " +
        "Each post can have multiple comments left by different users. " +
        "Returns a summary of all comments with author names and content.")]
    public static async Task<string> GetPostComments(
        JsonPlaceholderApiClient client,
        [Description("The ID of the post to get comments for (valid range 1-100, e.g. 1, 42, 100).")]
        int postId,
        CancellationToken cancellationToken = default)
    {
        if (postId < 1 || postId > 100)
            throw new ArgumentException("Post ID must be between 1 and 100.", nameof(postId));

        var comments = await client.GetPostCommentsAsync(postId, cancellationToken);
        return JsonPlaceholderFormatters.FormatComments(comments, postId);
    }

    [McpServerTool, Description(
        "Use when you want to add a comment to a blog post. This demonstrates POST request functionality. " +
        "The comment will be added to the post in the JSONPlaceholder API simulation.")]
    public static async Task<string> AddPostComment(
        JsonPlaceholderApiClient client,
        [Description("The ID of the post to comment on (valid range 1-100, e.g. 1, 42, 100).")]
        int postId,
        [Description("Name of the comment author (e.g. 'Jane Smith', 'Developer Bot').")]
        string name,
        [Description("Email address of the comment author (e.g. 'jane@example.com').")]
        string email,
        [Description("The comment text (e.g. 'Great post!', 'I learned a lot from this.').")]
        string body,
        CancellationToken cancellationToken = default)
    {
        if (postId < 1 || postId > 100)
            throw new ArgumentException("Post ID must be between 1 and 100.", nameof(postId));

        var comment = await client.CreateCommentAsync(postId, name, email, body, cancellationToken);
        return JsonPlaceholderFormatters.FormatCommentCreated(comment);
    }

    [McpServerTool, Description(
        "Use when you need to see a user's todo list. " +
        "Each user has a list of tasks they need to complete. " +
        "Returns a summary showing pending and completed todos.")]
    public static async Task<string> GetUserTodos(
        JsonPlaceholderApiClient client,
        [Description("User ID to get todos for (valid range 1-10, e.g. 1, 5, 10).")]
        int userId,
        CancellationToken cancellationToken = default)
    {
        if (userId < 1 || userId > 10)
            throw new ArgumentException("User ID must be between 1 and 10.", nameof(userId));

        var todos = await client.GetUserTodosAsync(userId, cancellationToken);
        return JsonPlaceholderFormatters.FormatTodos(todos, userId);
    }

    [McpServerTool, Description(
        "Use when you want to create a new todo item for a user. " +
        "This demonstrates POST request functionality and task management. " +
        "Returns the created todo with an ID assigned by the API.")]
    public static async Task<string> CreateUserTodo(
        JsonPlaceholderApiClient client,
        [Description("User ID for the todo (valid range 1-10, e.g. 1, 5, 10).")]
        int userId,
        [Description("Title/description of the todo task (e.g. 'Buy groceries', 'Fix bug #123').")]
        string title,
        [Description("Whether this todo is already completed (true/false, default: false).")]
        bool completed = false,
        CancellationToken cancellationToken = default)
    {
        if (userId < 1 || userId > 10)
            throw new ArgumentException("User ID must be between 1 and 10.", nameof(userId));

        var todo = await client.CreateTodoAsync(userId, title, completed, cancellationToken);
        return JsonPlaceholderFormatters.FormatTodoCreated(todo);
    }
}
