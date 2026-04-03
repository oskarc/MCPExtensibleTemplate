namespace McpServerTemplate.Providers.JsonPlaceholder.Models;

/// <summary>
/// Represents a todo item from JSONPlaceholder API.
/// </summary>
public class Todo
{
    public int UserId { get; set; }
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool Completed { get; set; }
}
