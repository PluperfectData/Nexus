namespace Nexus;

/// <summary>Whether a connector's settings work.</summary>
public sealed record CheckResult
{
    private CheckResult(ConnectorErrorKind? errorKind, string? message)
    {
        ErrorKind = errorKind;
        Message = message;
    }

    /// <summary>Settings that work.</summary>
    public static CheckResult Success { get; } = new(null, null);

    /// <summary>Settings that fail, with a message safe to show the user.</summary>
    public static CheckResult Failure(ConnectorErrorKind kind, string message) => new(kind, message);

    /// <summary>Whether the settings work.</summary>
    public bool Succeeded => ErrorKind is null;

    /// <summary>What the user does about a failure.</summary>
    public ConnectorErrorKind? ErrorKind { get; }

    /// <summary>The failure, in words safe to show the user.</summary>
    public string? Message { get; }
}
