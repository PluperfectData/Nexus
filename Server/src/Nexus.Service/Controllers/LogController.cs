using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Auth;
using Nexus.Service.Storage;

namespace Nexus.Service.Controllers;

/// <summary>An operator log entry. <c>WorkspaceId</c> is null for an entry about the whole instance.</summary>
public sealed record LogEntryView(
    long Id,
    DateTime At,
    EntryLevel Level,
    string Text,
    string? Actor,
    int? WorkspaceId,
    string? WorkspaceName,
    int? ConnectionId,
    int? SyncId);

/// <summary>
/// A page of entries, newest first. <c>Next</c> is the <c>before</c> that reads the older ones, or null at the end.
/// <c>Subject</c> names the connection or sync filtered by.
/// </summary>
public sealed record LogPage(IReadOnlyList<LogEntryView> Entries, long? Next, string? Subject);

/// <summary>
/// The operator log, for signed-in users: an admin reads every entry, and a member the entries of their own workspaces.
/// </summary>
[ApiController]
[Route("log")]
public sealed class LogController(NexusDbContext db, Access access, SourceConnectors connectors) : ControllerBase
{
    public const int PageSize = 100;

    /// <summary>
    /// Entries newest first. <paramref name="instance"/> picks the entries about the whole instance, for admins.
    /// <paramref name="level"/> Warning reads warnings and errors.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<LogPage>> Get(
        int? workspace,
        int? connection,
        int? sync,
        EntryLevel? level,
        long? before,
        bool? instance,
        CancellationToken ct)
    {
        var entries = db.LogEntries.AsNoTracking();
        if (!User.IsAdmin())
        {
            if (instance == true)
            {
                return NotFound();
            }

            var userId = User.UserId();
            var mine = db.WorkspaceMembers.Where(m => m.UserId == userId).Select(m => (int?)m.WorkspaceId);
            entries = entries.Where(e => mine.Contains(e.WorkspaceId));
        }

        if (workspace is { } workspaceId)
        {
            if (!await access.CanSeeAsync(User, workspaceId, ct))
            {
                return NotFound();
            }

            entries = entries.Where(e => e.WorkspaceId == workspaceId);
        }

        if (instance == true)
        {
            entries = entries.Where(e => e.WorkspaceId == null);
        }

        string? subject = null;
        if (connection is { } connectionId)
        {
            var found = await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId, ct);
            if (found is null || !await access.CanSeeAsync(User, found.WorkspaceId, ct))
            {
                return NotFound();
            }

            subject = $"Connection {LogWords.Connection(found, connectors)}";
            entries = entries.Where(e => e.ConnectionId == connectionId);
        }

        if (sync is { } syncId)
        {
            var found = await db.Syncs.AsNoTracking().Include(s => s.Connection).FirstOrDefaultAsync(s => s.Id == syncId, ct);
            if (found is null || !await access.CanSeeAsync(User, found.Connection!.WorkspaceId, ct))
            {
                return NotFound();
            }

            subject = $"Sync {found.Table}";
            entries = entries.Where(e => e.SyncId == syncId);
        }

        entries = level switch
        {
            EntryLevel.Error => entries.Where(e => e.Level == EntryLevel.Error),
            EntryLevel.Warning => entries.Where(e => e.Level != EntryLevel.Information),
            _ => entries,
        };

        if (before is { } cursor)
        {
            entries = entries.Where(e => e.Id < cursor);
        }

        var page = await entries
            .OrderByDescending(e => e.Id)
            .Take(PageSize + 1)
            .Select(e => new LogEntryView(
                e.Id,
                e.At,
                e.Level,
                e.Text,
                e.Actor,
                e.WorkspaceId,
                e.Workspace == null ? null : e.Workspace.Name,
                e.ConnectionId,
                e.SyncId))
            .ToListAsync(ct);
        var more = page.Count > PageSize;
        if (more)
        {
            page.RemoveAt(PageSize);
        }

        return new LogPage(page, more ? page[^1].Id : null, subject);
    }
}
