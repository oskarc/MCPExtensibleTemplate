namespace McpServerTemplate.Providers.JsonPlaceholder.Models;

/// <summary>
/// Represents a comment on a post from JSONPlaceholder API.
/// </summary>
public class Comment
{
    public int PostId { get; set; }
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}
