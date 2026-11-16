using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Push;
using Nexus.Service.Auth;
using Nexus.Service.Storage;
using Pluperfect.Jwt;

namespace Nexus.Service.Controllers;

public sealed record PairingRequest(string? AppName, string? IngestUrl, string? CallbackUrl, string? ConnectReturnUrl, string? VerifierHash);

public sealed record PairingOpened(string Id, string ApproveUrl, DateTime ExpiresAt);

/// <summary>A pairing as its approval page shows it.</summary>
public sealed record PairingView(string Id, string AppName, string IngestUrl, string ConnectReturnUrl, DateTime ExpiresAt);

public sealed record ApproveRequest(int WorkspaceId);

/// <summary>Where the approval page sends the admin back to the app.</summary>
public sealed record PairingEnd(string ReturnUrl);

public sealed record ClaimRequest(string? Verifier, string? PushKey);

/// <summary>What an app learns from claiming its pairing. <paramref name="AppKey"/> is shown only this once.</summary>
public sealed record PairingClaimed(string AppKey, int DestinationId, string AppName, string WorkspaceName);

/// <summary>
/// Links an app without copying keys by hand. The app opens a pairing, an admin approves it into a workspace, and the
/// app claims it with the verifier only it holds, sending a push key for its destination and taking its app key back.
/// </summary>
[ApiController]
[Route("pairings")]
public sealed class PairingsController(
    NexusDbContext db,
    PushDestination push,
    Secrets secrets,
    Instance instance,
    OperatorLog log,
    TimeProvider time) : ControllerBase
{
    private const string Gone = "This pairing has expired or is finished. Start again from the app.";

    /// <summary>Opens a pairing. Server to server, with no cookie, so there is nothing for antiforgery to protect.</summary>
    [HttpPost]
    [AllowAnonymous]
    [SkipAntiforgery]
    public async Task<ActionResult<PairingOpened>> Open(PairingRequest request, CancellationToken ct)
    {
        var name = request.AppName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > App.MaxNameLength)
        {
            return Problem($"An app needs a name of {App.MaxNameLength} characters or fewer.", statusCode: StatusCodes.Status400BadRequest);
        }

        string?[] addresses = [request.IngestUrl, request.CallbackUrl, request.ConnectReturnUrl];
        if (addresses.Any(url => url is null || !AppsController.IsWebAddress(url)))
        {
            return Problem("A pairing needs http or https addresses for the app's push API, its callback, and its connect pages' return.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.VerifierHash is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
        {
            return Problem("A pairing needs the SHA-256 hash of its verifier, as 64 hex characters.", statusCode: StatusCodes.Status400BadRequest);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var pairing = new Pairing
        {
            PublicId = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)),
            VerifierHash = hash.ToLowerInvariant(),
            AppName = name,
            IngestUrl = request.IngestUrl!,
            CallbackUrl = request.CallbackUrl!,
            ConnectReturnUrl = request.ConnectReturnUrl!,
            CreatedAt = now,
            ExpiresAt = now + Pairing.Lifetime,
        };
        db.Pairings.Add(pairing);
        await db.SaveChangesAsync(ct);
        return StatusCode(
            StatusCodes.Status201Created,
            new PairingOpened(pairing.PublicId, $"{instance.Client}/pair?id={pairing.PublicId}", pairing.ExpiresAt));
    }

    [HttpGet("{id}")]
    [Authorize(Policy = NexusClaims.AdminPolicy)]
    public async Task<ActionResult<PairingView>> Get(string id, CancellationToken ct) =>
        await OpenAsync(id, ct) is { } pairing
            ? new PairingView(pairing.PublicId, pairing.AppName, pairing.IngestUrl, pairing.ConnectReturnUrl, pairing.ExpiresAt)
            : Problem(Gone, statusCode: StatusCodes.Status404NotFound);

    [HttpPost("{id}/approve")]
    [Authorize(Policy = NexusClaims.AdminPolicy)]
    public async Task<ActionResult<PairingEnd>> Approve(string id, ApproveRequest request, CancellationToken ct)
    {
        if (await OpenAsync(id, ct) is not { } pairing)
        {
            return Problem(Gone, statusCode: StatusCodes.Status404NotFound);
        }

        if (await db.Workspaces.FirstOrDefaultAsync(w => w.Id == request.WorkspaceId, ct) is not { } workspace)
        {
            return Problem("Pick a workspace for the app.", statusCode: StatusCodes.Status400BadRequest);
        }

        log.Info($"{pairing.AppName} asked to link, and was approved into workspace {workspace.Name}.", LogPlace.In(workspace.Id));
        pairing.WorkspaceId = request.WorkspaceId;
        pairing.ApprovedById = User.UserId();
        pairing.ApprovedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return new PairingEnd(ReturnTo(pairing, "pairing", pairing.PublicId));
    }

    [HttpPost("{id}/decline")]
    [Authorize(Policy = NexusClaims.AdminPolicy)]
    public async Task<ActionResult<PairingEnd>> Decline(string id, CancellationToken ct)
    {
        if (await OpenAsync(id, ct) is not { } pairing)
        {
            return Problem(Gone, statusCode: StatusCodes.Status404NotFound);
        }

        pairing.DeclinedAt = time.GetUtcNow().UtcDateTime;
        log.Info($"{pairing.AppName} asked to link, and was declined.", LogPlace.Instance);
        await db.SaveChangesAsync(ct);
        return new PairingEnd(ReturnTo(pairing, "error", "declined"));
    }

    /// <summary>
    /// Claims an approved pairing with its verifier: makes the app with its key, and its destination once the push key
    /// works. Server to server, so antiforgery does not apply.
    /// </summary>
    [HttpPost("{id}/claim")]
    [AllowAnonymous]
    [SkipAntiforgery]
    public async Task<ActionResult<PairingClaimed>> Claim(string id, ClaimRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var pairing = await db.Pairings.FirstOrDefaultAsync(p => p.PublicId == id && p.ClaimedAt == null && p.DeclinedAt == null && p.ExpiresAt > now, ct);
        if (pairing is null || request.Verifier is null || AppKeys.HashOf(request.Verifier) != pairing.VerifierHash)
        {
            return Problem(Gone, statusCode: StatusCodes.Status404NotFound);
        }

        if (pairing.ApprovedAt is null)
        {
            return Problem("An admin has not approved this pairing yet.", statusCode: StatusCodes.Status409Conflict);
        }

        var key = request.PushKey?.Trim() ?? string.Empty;
        PushKey named;
        try
        {
            named = await push.IdentifyAsync(DestinationsController.Config(pairing.IngestUrl, key), ct);
        }
        catch (ConnectorException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        var (appKey, hash, prefix) = AppKeys.Make();
        var app = new App
        {
            WorkspaceId = pairing.WorkspaceId!.Value,
            Name = pairing.AppName,
            KeyHash = hash,
            KeyPrefix = prefix,
            KeyCreatedAt = now,
            ReturnUrls = JsonSerializer.Serialize(new[] { pairing.ConnectReturnUrl }),
            CreatedAt = now,
        };
        var destination = new Destination { App = app, Url = pairing.IngestUrl, Key = secrets.ProtectText(key), CreatedAt = now };
        destination.Checked(named, null, now);
        db.Destinations.Add(destination);
        pairing.ClaimedAt = now;
        log.Info(
            $"App {app.Name} was linked, with key {prefix}… and destination {destination.Url}, sending key {named.Prefix}…, named {named.Name}.",
            LogPlace.In(app.WorkspaceId),
            OperatorLog.AppActor(app.Name));
        await db.SaveChangesAsync(ct);
        pairing.AppId = app.Id;
        await db.SaveChangesAsync(ct);

        var workspace = await db.Workspaces.SingleAsync(w => w.Id == app.WorkspaceId, ct);
        return new PairingClaimed(appKey, destination.Id, app.Name, workspace.Name);
    }

    private async Task<Pairing?> OpenAsync(string id, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return await db.Pairings.FirstOrDefaultAsync(
            p => p.PublicId == id && p.ApprovedAt == null && p.DeclinedAt == null && p.ClaimedAt == null && p.ExpiresAt > now,
            ct);
    }

    private static string ReturnTo(Pairing pairing, string key, string value)
    {
        var join = pairing.CallbackUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{pairing.CallbackUrl}{join}{key}={Uri.EscapeDataString(value)}";
    }
}
