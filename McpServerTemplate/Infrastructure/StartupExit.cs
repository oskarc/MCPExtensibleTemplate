namespace McpServerTemplate.Infrastructure;

/// <summary>
/// Process exit codes, following the BSD sysexits convention that orchestrators and
/// init systems already understand.
///
/// contract-001 · G-2 — a failure never leaves this process with code 0. A supervisor
/// that reads 0 as success will happily keep a dead server in rotation.
/// </summary>
public static class ExitCode
{
    /// <summary>Normal shutdown.</summary>
    public const int Ok = 0;

    /// <summary>An unhandled internal failure (EX_SOFTWARE).</summary>
    public const int Software = 70;

    /// <summary>The server was asked to run in a configuration it cannot honour (EX_CONFIG).</summary>
    public const int Configuration = 78;
}

/// <summary>
/// Thrown when configuration asks for something the server will not do — an unknown transport,
/// stdio outside Development, a setting it would ignore, a provider whose policy does not match
/// what it serves. Distinguished from every other failure so the
/// process can exit <see cref="ExitCode.Configuration"/> rather than <see cref="ExitCode.Software"/>:
/// the operator needs to know whether to fix the deployment or read a stack trace.
/// </summary>
public sealed class ConfigurationException : Exception
{
    public ConfigurationException(string message) : base(message) { }

    public ConfigurationException(string message, Exception innerException)
        : base(message, innerException) { }

    public ConfigurationException() { }
}
