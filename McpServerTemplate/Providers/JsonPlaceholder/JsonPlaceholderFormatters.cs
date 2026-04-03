using System.Text;
using McpServerTemplate.Providers.JsonPlaceholder.Models;

namespace McpServerTemplate.Providers.JsonPlaceholder;

/// <summary>
/// Output formatters for JSONPlaceholder data.
/// 
/// Transforms raw API responses into human-readable text optimized for LLM consumption.
/// </summary>
public static class JsonPlaceholderFormatters
{
    public static string FormatPost(Post post)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"📝 Post #{post.Id}");
        sb.AppendLine($"Author: User {post.UserId}");
        sb.AppendLine($"Title: {post.Title}");
        sb.AppendLine();
        sb.AppendLine(post.Body);
        return sb.ToString();
    }

    public static string FormatPostCreated(Post post)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"✅ Post Created Successfully");
        sb.AppendLine($"Post ID: {post.Id}");
        sb.AppendLine($"Author: User {post.UserId}");
        sb.AppendLine($"Title: {post.Title}");
        sb.AppendLine($"Body: {post.Body}");
        return sb.ToString();
    }

    public static string FormatComments(List<Comment> comments, int postId)
    {
        if (comments.Count == 0)
            return $"No comments found for post {postId}.";

        var sb = new StringBuilder();
        sb.AppendLine($"💬 Comments for Post #{postId} ({comments.Count} total)");
        sb.AppendLine();

        foreach (var comment in comments)
        {
            sb.AppendLine($"Comment #{comment.Id}");
            sb.AppendLine($"  By: {comment.Name} ({comment.Email})");
            sb.AppendLine($"  {comment.Body}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string FormatCommentCreated(Comment comment)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"✅ Comment Created Successfully");
        sb.AppendLine($"Comment ID: {comment.Id}");
        sb.AppendLine($"Post ID: {comment.PostId}");
        sb.AppendLine($"Author: {comment.Name} ({comment.Email})");
        sb.AppendLine($"Body: {comment.Body}");
        return sb.ToString();
    }

    public static string FormatTodos(List<Todo> todos, int userId)
    {
        if (todos.Count == 0)
            return $"No todos found for user {userId}.";

        var sb = new StringBuilder();
        var completed = todos.Count(t => t.Completed);
        var pending = todos.Count(t => !t.Completed);

        sb.AppendLine($"✅ Todos for User #{userId}");
        sb.AppendLine($"  Completed: {completed}/{todos.Count}");
        sb.AppendLine($"  Pending: {pending}/{todos.Count}");
        sb.AppendLine();

        var pending_todos = todos.Where(t => !t.Completed).ToList();
        if (pending_todos.Count > 0)
        {
            sb.AppendLine("📋 Pending:");
            foreach (var todo in pending_todos)
            {
                sb.AppendLine($"  ☐ [{todo.Id}] {todo.Title}");
            }
            sb.AppendLine();
        }

        var completed_todos = todos.Where(t => t.Completed).ToList();
        if (completed_todos.Count > 0)
        {
            sb.AppendLine("✔️ Completed:");
            foreach (var todo in completed_todos)
            {
                sb.AppendLine($"  ☑ [{todo.Id}] {todo.Title}");
            }
        }

        return sb.ToString();
    }

    public static string FormatTodoCreated(Todo todo)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"✅ Todo Created Successfully");
        sb.AppendLine($"Todo ID: {todo.Id}");
        sb.AppendLine($"User ID: {todo.UserId}");
        sb.AppendLine($"Title: {todo.Title}");
        sb.AppendLine($"Status: {(todo.Completed ? "✔️ Completed" : "☐ Pending")}");
        return sb.ToString();
    }
}
