namespace Nexus;

/// <summary>How a connector authenticates.</summary>
public enum AuthKind
{
    /// <summary>Settings fields such as a host, a user name, and a password.</summary>
    ConnectionFields,

    /// <summary>A single API key.</summary>
    ApiKey,

    /// <summary>An OAuth sign-in that the host runs.</summary>
    OAuth,

    /// <summary>A file the user uploads.</summary>
    FileUpload,
}

/// <summary>How the host renders a settings field.</summary>
public enum SettingKind
{
    /// <summary>Plain text.</summary>
    Text,

    /// <summary>Text the host masks and never logs.</summary>
    Secret,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A yes or no switch.</summary>
    Boolean,

    /// <summary>One value from <see cref="SettingField.Choices"/>.</summary>
    Choice,
}

/// <summary>One setting the host asks the user for.</summary>
/// <param name="Name">The key in <see cref="ConnectorConfig.Values"/>.</param>
/// <param name="Label">The text shown beside the field.</param>
/// <param name="Kind">How the field is rendered.</param>
/// <param name="Required">Whether the user must fill it in.</param>
/// <param name="Choices">The allowed values, when <paramref name="Kind"/> is <see cref="SettingKind.Choice"/>.</param>
public sealed record SettingField(
    string Name,
    string Label,
    SettingKind Kind,
    bool Required,
    IReadOnlyList<string>? Choices = null);

/// <summary>The OAuth endpoints and scopes the host uses to sign the user in.</summary>
/// <param name="AuthorizeUrl">Where the host sends the user to sign in.</param>
/// <param name="TokenUrl">Where the host exchanges the sign-in code for tokens.</param>
/// <param name="Scopes">The scopes the connector needs.</param>
public sealed record OAuthSpec(Uri AuthorizeUrl, Uri TokenUrl, IReadOnlyList<string> Scopes);

/// <summary>What a connector is and the settings it needs.</summary>
/// <param name="Id">A stable identifier, such as <c>mysql</c>.</param>
/// <param name="DisplayName">The name shown to users.</param>
/// <param name="Version">The connector's version.</param>
/// <param name="Auth">How the connector authenticates.</param>
/// <param name="Fields">The settings the host asks for.</param>
/// <param name="OAuth">The OAuth endpoints, when <paramref name="Auth"/> is <see cref="AuthKind.OAuth"/>.</param>
public sealed record ConnectorSpec(
    string Id,
    string DisplayName,
    string Version,
    AuthKind Auth,
    IReadOnlyList<SettingField> Fields,
    OAuthSpec? OAuth = null);
