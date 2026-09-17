using System.Text.Json.Serialization;

namespace McpServerTemplate.Providers.JsonPlaceholder.Models;

/// <summary>
/// Represents a blog post from JSONPlaceholder API.
///
/// Every field is bound explicitly. The upstream answers in camelCase, and the default
/// System.Text.Json matcher is case-sensitive: without these attributes a response
/// deserializes into a fully-defaulted record and the tool reports success on empty data.
/// </summary>
public class Post
{
    [JsonPropertyName("userId")]
    public int UserId { get; set; }

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;
}
