namespace Nexus;

/// <summary>What the host does about a connector failure.</summary>
public enum ConnectorErrorKind
{
    /// <summary>The user fixes the settings.</summary>
    ConfigInvalid,

    /// <summary>The user signs in again.</summary>
    AuthExpired,

    /// <summary>The host waits for <see cref="ConnectorException.RetryAfter"/>, then retries.</summary>
    RateLimited,

    /// <summary>The host retries later.</summary>
    Transient,

    /// <summary>The source returned something the connector cannot use.</summary>
    SourceError,
}

/// <summary>A connector failure, with a message safe to show the user.</summary>
public sealed class ConnectorException : Exception
{
    /// <summary>Creates a failure of the given kind.</summary>
    public ConnectorException(
        ConnectorErrorKind kind,
        string message,
        Exception? innerException = null,
        TimeSpan? retryAfter = null)
        : base(message, innerException)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }

    /// <summary>What the host does about it.</summary>
    public ConnectorErrorKind Kind { get; }

    /// <summary>How long the source asked the host to wait, when it said.</summary>
    public TimeSpan? RetryAfter { get; }
}
