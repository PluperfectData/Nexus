namespace Nexus;

/// <summary>The outcome of an action that changed something on a service.</summary>
/// <param name="RefreshedCredentials">The newest tokens the action produced, for the host to store, or null.</param>
public record ActionOutcome(OAuthTokens? RefreshedCredentials);

/// <summary>The outcome of an action, with the value the service returned.</summary>
/// <typeparam name="T">The value's type.</typeparam>
/// <param name="Value">What the service returned.</param>
/// <param name="RefreshedCredentials">The newest tokens the action produced, for the host to store, or null.</param>
public sealed record ActionOutcome<T>(T Value, OAuthTokens? RefreshedCredentials) : ActionOutcome(RefreshedCredentials);
