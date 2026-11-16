namespace Nexus.Push;

/// <summary>A push API key as its push API names it: the name it was given there, and the start of the key.</summary>
public sealed record PushKey(string Name, string Prefix);
