namespace McpServerTemplate.Providers.JsonPlaceholder.Models;

/// <summary>
/// Represents a blog post from JSONPlaceholder API.
/// </summary>
public class Post
{
    public int UserId { get; set; }
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}
