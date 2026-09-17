using System.Text.Json.Serialization;

namespace McpServerTemplate.Providers.JsonPlaceholder.Models;

/// <summary>
/// Represents a todo item from JSONPlaceholder API.
/// Fields are bound explicitly; see <see cref="Post"/> for why.
/// </summary>
public class Todo
{
    [JsonPropertyName("userId")]
    public int UserId { get; set; }

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("completed")]
    public bool Completed { get; set; }
}
