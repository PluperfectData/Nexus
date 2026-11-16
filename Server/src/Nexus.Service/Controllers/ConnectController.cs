using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Storage;
using Pluperfect.Jwt;

namespace Nexus.Service.Controllers;

/// <summary>
/// What a connect page shows: who asked, the connector and its settings, and the connection once made.
/// <paramref name="Renewing"/> says the page signs <paramref name="Connection"/> in again.
/// </summary>
public sealed record ConnectView(
    string AppName,
    string WorkspaceName,
    ConnectorView Connector,
    string DestinationUrl,
    DateTime ExpiresAt,
    ConnectionView? Connection,
    bool Renewing);

public sealed record ConnectSignInRequest(string? Name, IReadOnlyDictionary<string, string?>? Settings);

/// <summary>The provider's sign-in page, for the browser to go to.</summary>
public sealed record SignInPage(string Url);

public sealed record ConnectConnectionRequest(string? Name, IReadOnlyDictionary<string, string?>? Settings);

public sealed record ConnectSyncsRequest(IReadOnlyList<SyncStreamRequest>? Streams);

/// <summary>Where the page sends the person once it is done.</summary>
public sealed record ConnectEnd(string ReturnUrl);

/// <summary>
/// The connect page's routes. The page's token, sent as <c>Authorization: Bearer nxc_...</c>, is the only credential:
/// the person on it needs no user in the instance. It makes one connection, adds its syncs, and returns to the app.
/// </summary>
[ApiController]
[Route("connect")]
[AllowAnonymous]
[SkipAntiforgery]
public sealed class ConnectController(
    NexusDbContext db,
    SourceConnectors connectors,
    ConnectionMaker connections,
    ConnectionFiles files,
    SyncMaker syncs,
    OAuthApps apps,
    SignIns signIns,
    TimeProvider time) : ControllerBase
{
    private const string Ended = "This connect page has expired or is finished. Go back to the app and start again.";

    [HttpGet]
    public async Task<ActionResult<ConnectView>> Get(CancellationToken ct) =>
        await SessionAsync(ct) is { } session ? View(session) : Problem(Ended, statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>Makes the page's one connection, once its settings connect.</summary>
    [HttpPost("connection")]
    [Consumes("application/json")]
    public Task<ActionResult<ConnectView>> Connect(ConnectConnectionRequest request, CancellationToken ct) =>
        ConnectAsync(request.Name, request.Settings, null, ct);

    /// <summary>Makes the page's one connection from an upload, for a connector that reads a file, such as CSV.</summary>
    [HttpPost("connection")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ConnectionFiles.MaxBytes + ConnectionFiles.FormOverheadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = ConnectionFiles.MaxBytes + ConnectionFiles.FormOverheadBytes)]
    public async Task<ActionResult<ConnectView>> ConnectWithFile([FromForm] FileConnectionForm form, CancellationToken ct)
    {
        if (form.ParsedSettings() is not { } settings)
        {
            return Problem("Send the settings as a JSON object of text.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (await SessionAsync(ct) is null)
        {
            return Problem(Ended, statusCode: StatusCodes.Status401Unauthorized);
        }

        var (file, refused) = await files.SaveAsync(form.File, ct);
        return file is null ? Problem(refused, statusCode: StatusCodes.Status400BadRequest) : await ConnectAsync(form.Name, settings, file, ct);
    }

    private async Task<ActionResult<ConnectView>> ConnectAsync(string? name, IReadOnlyDictionary<string, string?>? settings, UploadedFile? file, CancellationToken ct)
    {
        if (await SessionAsync(ct) is not { } session)
        {
            files.Delete(file?.StoredFile);
            return Problem(Ended, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (session.ConnectionId is not null)
        {
            files.Delete(file?.StoredFile);
            return Problem("This page has made its connection. Pick its streams next.", statusCode: StatusCodes.Status409Conflict);
        }

        var (connection, problem) = await connections.AddAsync(session.App!.WorkspaceId, name, session.Connector, settings, file, ct);
        if (connection is null)
        {
            return Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        session.ConnectionId = connection.Id;
        session.Connection = connection;
        await db.SaveChangesAsync(ct);
        return View(session);
    }

    /// <summary>
    /// Starts the page's sign-in to the connector's provider, for a new connection from the name and settings given, or
    /// for the connection the page signs in again. Answers the provider's sign-in page.
    /// </summary>
    [HttpPost("sign-in")]
    public async Task<ActionResult<SignInPage>> SignIn(ConnectSignInRequest request, CancellationToken ct)
    {
        if (await SessionAsync(ct) is not { } session)
        {
            return Problem(Ended, statusCode: StatusCodes.Status401Unauthorized);
        }

        var token = Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        PendingSignIn pending;
        if (session.Renews)
        {
            if (apps.ProblemFor(session.Connector) is { } missing)
            {
                return Problem(missing, statusCode: StatusCodes.Status400BadRequest);
            }

            pending = new PendingSignIn(session.Connector, session.App!.WorkspaceId, null, null, session.ConnectionId, session.Id, token);
        }
        else if (session.ConnectionId is not null)
        {
            return Problem("This page has made its connection. Pick its streams next.", statusCode: StatusCodes.Status409Conflict);
        }
        else
        {
            var (name, settings, problem) = connections.CheckSignIn(request.Name, session.Connector, request.Settings);
            if (problem is not null)
            {
                return Problem(problem, statusCode: StatusCodes.Status400BadRequest);
            }

            pending = new PendingSignIn(session.Connector, session.App!.WorkspaceId, name, settings, null, session.Id, token);
        }

        return new SignInPage(apps.SignInPage(session.Connector, signIns.Start(pending)).ToString());
    }

    [HttpGet("streams")]
    public async Task<ActionResult<IEnumerable<StreamView>>> Streams(CancellationToken ct)
    {
        if (await SessionAsync(ct) is not { Connection: { } connection })
        {
            return Problem(Ended, statusCode: StatusCodes.Status401Unauthorized);
        }

        var (catalog, problem) = await connections.DiscoverAsync(connection, ct);
        return catalog is null ? Problem(problem, statusCode: StatusCodes.Status400BadRequest) : StreamView.From(catalog).ToList();
    }

    /// <summary>Adds the picked streams' syncs into the app's destination, ends the page, and answers where to return.</summary>
    [HttpPost("syncs")]
    public async Task<ActionResult<ConnectEnd>> Finish(ConnectSyncsRequest request, CancellationToken ct)
    {
        if (await SessionAsync(ct) is not { Connection: { } connection } session)
        {
            return Problem(Ended, statusCode: StatusCodes.Status401Unauthorized);
        }

        var (made, problem) = await syncs.AddAsync(connection, session.Destination!, request.Streams, ct);
        if (made is null)
        {
            return Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        await EndAsync(session, ct);
        return new ConnectEnd(ReturnTo(session, "connection", connection.Id.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Ends the page without adding syncs, and answers where to return. A connection it made stays.</summary>
    [HttpPost("cancel")]
    public async Task<ActionResult<ConnectEnd>> Cancel(CancellationToken ct)
    {
        if (await SessionAsync(ct) is not { } session)
        {
            return Problem(Ended, statusCode: StatusCodes.Status401Unauthorized);
        }

        await EndAsync(session, ct);
        return new ConnectEnd(ReturnTo(session, "error", "cancelled"));
    }

    // The open, unexpired session the request's token names, with what its routes read.
    private async Task<ConnectSession?> SessionAsync(CancellationToken ct)
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer " + AppKeys.ConnectLead, StringComparison.Ordinal))
        {
            return null;
        }

        var hash = AppKeys.HashOf(header["Bearer ".Length..].Trim());
        var now = time.GetUtcNow().UtcDateTime;
        return await db.ConnectSessions
            .Include(s => s.App).ThenInclude(a => a!.Workspace)
            .Include(s => s.Destination)
            .Include(s => s.Connection)
            .FirstOrDefaultAsync(s => s.TokenHash == hash && s.EndedAt == null && s.ExpiresAt > now, ct);
    }

    private async Task EndAsync(ConnectSession session, CancellationToken ct)
    {
        session.EndedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    private ConnectView View(ConnectSession session)
    {
        var connector = connectors.Find(session.Connector) ?? throw new InvalidOperationException($"No connector '{session.Connector}'.");
        return new ConnectView(
            session.App!.Name,
            session.App.Workspace!.Name,
            ConnectorView.Of(connector),
            session.Destination!.Url,
            session.ExpiresAt,
            session.Connection is { } connection ? connections.ViewOf(connection) : null,
            session.Renews);
    }

    /// <summary>The app's return address for the session, with one more query value.</summary>
    internal static string ReturnTo(ConnectSession session, string key, string value)
    {
        var join = session.ReturnUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{session.ReturnUrl}{join}{key}={Uri.EscapeDataString(value)}";
    }
}
