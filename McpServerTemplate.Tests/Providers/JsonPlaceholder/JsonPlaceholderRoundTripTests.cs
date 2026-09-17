using System.Net;
using System.Text;
using McpServerTemplate.Providers.JsonPlaceholder;

namespace McpServerTemplate.Tests.Providers.JsonPlaceholder;

/// <summary>
/// contract-001 · T-5 (G-4) — a tool returns the actual record.
///
/// Feeds the provider payloads recorded from the real upstream, which answers in camelCase,
/// and fails if a tool returns an id of 0, an empty title, or an empty collection reported
/// as success. This is the red test for contract-001: G-4 is broken in the tree today.
/// </summary>
public class JsonPlaceholderRoundTripTests
{
    // Recorded from jsonplaceholder.typicode.com. The upstream answers in camelCase.
    private const string PostJson =
        """{"userId":1,"id":1,"title":"sunt aut facere repellat provident","body":"quia et suscipit suscipit"}""";

    private const string CommentsJson =
        """[{"postId":1,"id":1,"name":"id labore ex et quam","email":"Eliseo@gardner.biz","body":"laudantium enim quasi"}]""";

    private const string TodosJson =
        """[{"userId":1,"id":1,"title":"delectus aut autem","completed":false}]""";

    /// The real API echoes the posted body verbatim and appends its own lowercase "id".
    private sealed class RecordedUpstream : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string body;

            if (request.Method == HttpMethod.Post)
            {
                var sent = await request.Content!.ReadAsStringAsync(ct);
                body = sent.TrimEnd().TrimEnd('}') + ",\"id\":101}";
            }
            else if (path == "/posts/1") body = PostJson;
            else if (path == "/posts/1/comments") body = CommentsJson;
            else if (path == "/todos") body = TodosJson;
            else body = "{}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static JsonPlaceholderApiClient Client() =>
        new(new HttpClient(new RecordedUpstream()) { BaseAddress = new Uri("https://jsonplaceholder.typicode.com") });

    [Fact]
    public async Task T5_GetPost_returns_the_real_record()
    {
        var post = await Client().GetPostAsync(1);

        Assert.Equal(1, post.Id);
        Assert.False(string.IsNullOrWhiteSpace(post.Title), "title came back empty");
        Assert.False(string.IsNullOrWhiteSpace(post.Body), "body came back empty");
        Assert.Equal(1, post.UserId);
    }

    [Fact]
    public async Task T5_GetPostComments_returns_populated_comments()
    {
        var comments = await Client().GetPostCommentsAsync(1);

        var comment = Assert.Single(comments);
        Assert.Equal(1, comment.Id);
        Assert.Equal(1, comment.PostId);
        Assert.False(string.IsNullOrWhiteSpace(comment.Name), "comment name came back empty");
        Assert.False(string.IsNullOrWhiteSpace(comment.Email), "comment email came back empty");
    }

    [Fact]
    public async Task T5_GetUserTodos_returns_populated_todos()
    {
        var todos = await Client().GetUserTodosAsync(1);

        var todo = Assert.Single(todos);
        Assert.Equal(1, todo.Id);
        Assert.False(string.IsNullOrWhiteSpace(todo.Title), "todo title came back empty");
    }

    [Fact]
    public async Task T5_CreatePost_returns_the_id_the_upstream_assigned()
    {
        var created = await Client().CreatePostAsync(1, "Hello", "World");

        Assert.Equal(101, created.Id);
        Assert.Equal("Hello", created.Title);
    }

    [Fact]
    public async Task T5_CreateComment_returns_the_id_the_upstream_assigned()
    {
        var created = await Client().CreateCommentAsync(1, "Ada", "ada@example.com", "Body");

        Assert.Equal(101, created.Id);
        Assert.Equal(1, created.PostId);
        Assert.Equal("Ada", created.Name);
        Assert.Equal("ada@example.com", created.Email);
    }

    [Fact]
    public async Task T5_CreateTodo_returns_the_id_the_upstream_assigned()
    {
        var created = await Client().CreateTodoAsync(1, "Write the test", completed: true);

        Assert.Equal(101, created.Id);
        Assert.Equal(1, created.UserId);
        Assert.Equal("Write the test", created.Title);
        Assert.True(created.Completed, "completed did not round-trip");
    }
}
