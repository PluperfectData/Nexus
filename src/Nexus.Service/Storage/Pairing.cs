namespace Nexus.Service.Storage;

/// <summary>
/// An app asking to link to the instance. An admin approves it into a workspace, then the app claims it with the
/// verifier only it holds, and the service makes the app, its key, and its destination.
/// </summary>
public sealed class Pairing
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public long Id { get; set; }

    /// <summary>The id in the approval page's address, which is not a secret: the verifier is.</summary>
    public required string PublicId { get; set; }

    /// <summary>The SHA-256 hash of the verifier the app keeps to claim the pairing.</summary>
    public required string VerifierHash { get; set; }

    public required string AppName { get; set; }

    /// <summary>The app's push API, which becomes its destination.</summary>
    public required string IngestUrl { get; set; }

    /// <summary>Where the approval page sends the admin back to the app.</summary>
    public required string CallbackUrl { get; set; }

    /// <summary>The address the app's connect pages return to, which becomes its return address.</summary>
    public required string ConnectReturnUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public int? WorkspaceId { get; set; }

    public int? ApprovedById { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public DateTime? DeclinedAt { get; set; }

    public int? AppId { get; set; }

    public DateTime? ClaimedAt { get; set; }
}
