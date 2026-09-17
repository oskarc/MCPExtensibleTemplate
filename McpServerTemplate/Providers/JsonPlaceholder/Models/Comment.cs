using System.Text.Json.Serialization;

namespace McpServerTemplate.Providers.JsonPlaceholder.Models;

/// <summary>
/// Represents a comment on a post from JSONPlaceholder API.
/// Fields are bound explicitly; see <see cref="Post"/> for why.
/// </summary>
public class Comment
{
    [JsonPropertyName("postId")]
    public int PostId { get; set; }

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;
}
