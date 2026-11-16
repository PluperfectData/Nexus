using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Auth;
using Nexus.Service.Storage;

namespace Nexus.Service;

/// <summary>Which workspaces a caller may see: an admin sees every one, a member theirs, and an app its own.</summary>
public sealed class Access(NexusDbContext db)
{
    public async Task<bool> CanSeeAsync(ClaimsPrincipal user, int workspaceId, CancellationToken ct)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId, ct))
        {
            return false;
        }

        if (user.AppId() is { } appId)
        {
            return await db.Apps.AnyAsync(a => a.Id == appId && a.WorkspaceId == workspaceId, ct);
        }

        return user.IsAdmin() || await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == user.UserId(), ct);
    }
}

/// <summary>Makes app keys and connect session tokens, each kept only as its hash.</summary>
public static class AppKeys
{
    public const string Lead = "nexus_";

    public const string ConnectLead = "nxc_";

    // The lead and six characters of the random part: enough to tell keys apart, far too little to guess one.
    private const int PrefixLength = 12;

    public static (string Key, string Hash, string Prefix) Make()
    {
        var key = Lead + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (key, HashOf(key), key[..PrefixLength]);
    }

    public static (string Token, string Hash) MakeConnectToken()
    {
        var token = ConnectLead + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return (token, HashOf(token));
    }

    public static string HashOf(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
