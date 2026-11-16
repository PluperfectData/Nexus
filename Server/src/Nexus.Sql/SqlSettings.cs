namespace Nexus.Sql;

/// <summary>The settings keys every Nexus SQL source shares.</summary>
public static class SqlSettings
{
    /// <summary>The server's host name or address.</summary>
    public const string Host = "host";

    /// <summary>The server's port. Blank uses the driver's default.</summary>
    public const string Port = "port";

    /// <summary>The database to read.</summary>
    public const string Database = "database";

    /// <summary>The login's user name.</summary>
    public const string User = "user";

    /// <summary>The login's password.</summary>
    public const string Password = "password";

    /// <summary>The TLS mode, one of the source's choices. Blank uses the driver's default.</summary>
    public const string Tls = "tls";
}
