using McpServerTemplate.Infrastructure;
using McpServerTemplate.Infrastructure.Frame;

namespace McpServerTemplate.Tests.Frame;

/// <summary>
/// contract-005 verify phase (SEC-V-01) — a Limits:Redis connection string the client cannot read is refused as
/// configuration, naming the key and showing none of the value: a password holding ',X=' split into a keyword reached the
/// fatal log with exit 70.
/// </summary>
public class RedisConnectionStringTests
{
    [Theory]
    [InlineData("redis:6379,password=Ab,Cd9Xy=", "Cd9Xy")]
    [InlineData("redis:6379,password=S3cretPW,bogusKey=1", "S3cretPW")]
    [InlineData("redis:6379,connectTimeout=abc,password=S3cretPW", "S3cretPW")]
    public void An_unreadable_connection_string_is_configuration_and_shows_none_of_it(string connectionString, string secret)
    {
        var refusal = Assert.Throws<ConfigurationException>(() => RedisLimitStore.Connect(connectionString));

        Assert.Contains("Limits:Redis", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bogusKey", refusal.Message, StringComparison.Ordinal);
    }
}
