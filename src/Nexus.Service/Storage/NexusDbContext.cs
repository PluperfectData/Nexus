using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Nexus.Service.Storage;

public sealed class NexusDbContext(DbContextOptions<NexusDbContext> options) : DbContext(options)
{
    public static readonly MySqlServerVersion ServerVersion = new(new Version(8, 4, 0));

    // Databases drop DateTimeKind, so every stored time is read back as UTC.
    private static readonly ValueConverter<DateTime, DateTime> Utc = new(
        value => value,
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> NullableUtc = new(
        value => value,
        value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null);

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<App> Apps => Set<App>();

    public DbSet<Destination> Destinations => Set<Destination>();

    public DbSet<Connection> Connections => Set<Connection>();

    public DbSet<Sync> Syncs => Set<Sync>();

    public DbSet<Run> Runs => Set<Run>();

    public DbSet<User> Users => Set<User>();

    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<ConnectSession> ConnectSessions => Set<ConnectSession>();

    public DbSet<Pairing> Pairings => Set<Pairing>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Pairing>(pairing =>
        {
            pairing.ToTable("pairings");
            pairing.HasIndex(p => p.PublicId).IsUnique();
            pairing.Property(p => p.PublicId).HasMaxLength(32);
            pairing.Property(p => p.VerifierHash).HasMaxLength(64);
            pairing.Property(p => p.AppName).HasMaxLength(App.MaxNameLength);
            pairing.Property(p => p.IngestUrl).HasMaxLength(2000);
            pairing.Property(p => p.CallbackUrl).HasMaxLength(2000);
            pairing.Property(p => p.ConnectReturnUrl).HasMaxLength(2000);
            pairing.Property(p => p.CreatedAt).HasConversion(Utc);
            pairing.Property(p => p.ExpiresAt).HasConversion(Utc);
            pairing.Property(p => p.ApprovedAt).HasConversion(NullableUtc);
            pairing.Property(p => p.DeclinedAt).HasConversion(NullableUtc);
            pairing.Property(p => p.ClaimedAt).HasConversion(NullableUtc);
        });

        modelBuilder.Entity<ConnectSession>(session =>
        {
            session.ToTable("connect_sessions");
            session.HasIndex(s => s.TokenHash).IsUnique();
            session.Property(s => s.TokenHash).HasMaxLength(64);
            session.Property(s => s.Connector).HasMaxLength(32);
            session.Property(s => s.ReturnUrl).HasMaxLength(2000);
            session.Property(s => s.CreatedAt).HasConversion(Utc);
            session.Property(s => s.ExpiresAt).HasConversion(Utc);
            session.Property(s => s.EndedAt).HasConversion(NullableUtc);
            session.HasOne(s => s.App).WithMany().HasForeignKey(s => s.AppId).OnDelete(DeleteBehavior.Cascade);
            session.HasOne(s => s.Destination).WithMany().HasForeignKey(s => s.DestinationId).OnDelete(DeleteBehavior.Cascade);
            session.HasOne(s => s.Connection).WithMany().HasForeignKey(s => s.ConnectionId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("users");
            user.HasIndex(u => u.Email).IsUnique();
            user.Property(u => u.Email).HasMaxLength(User.MaxEmailLength);
            user.Property(u => u.PasswordHash).HasMaxLength(255);
            user.Property(u => u.LinkStamp).HasMaxLength(64);
            user.Property(u => u.CreatedAt).HasConversion(Utc);
            user.Property(u => u.SignedInAt).HasConversion(NullableUtc);
        });

        modelBuilder.Entity<WorkspaceMember>(member =>
        {
            member.ToTable("workspace_members");
            member.HasKey(m => new { m.WorkspaceId, m.UserId });
            member.HasOne(m => m.Workspace).WithMany().HasForeignKey(m => m.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
            member.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefreshToken>(token =>
        {
            token.ToTable("refresh_tokens");
            token.HasIndex(t => t.TokenHash).IsUnique();
            token.HasIndex(t => t.UserId);
            token.Property(t => t.UserId).HasMaxLength(32);
            token.Property(t => t.Email).HasMaxLength(User.MaxEmailLength);
            token.Property(t => t.TokenHash).HasMaxLength(64);
            token.Property(t => t.IssuedForIp).HasMaxLength(64);
            token.Property(t => t.RevokedReason).HasMaxLength(200);
        });

        modelBuilder.Entity<Workspace>(workspace =>
        {
            workspace.ToTable("workspaces");
            workspace.Property(w => w.Name).HasMaxLength(Workspace.MaxNameLength);
            workspace.Property(w => w.CreatedAt).HasConversion(Utc);
        });

        modelBuilder.Entity<App>(app =>
        {
            app.ToTable("apps");
            app.HasIndex(a => a.KeyHash).IsUnique();
            app.Property(a => a.Name).HasMaxLength(App.MaxNameLength);
            app.Property(a => a.KeyHash).HasMaxLength(64);
            app.Property(a => a.KeyPrefix).HasMaxLength(16);
            app.Property(a => a.KeyCreatedAt).HasConversion(NullableUtc);
            app.Property(a => a.CreatedAt).HasConversion(Utc);
            app.HasOne(a => a.Workspace).WithMany().HasForeignKey(a => a.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Destination>(destination =>
        {
            destination.ToTable("destinations");
            destination.Property(d => d.Url).HasMaxLength(2000);
            destination.Property(d => d.CreatedAt).HasConversion(Utc);
            destination.Property(d => d.KeyName).HasMaxLength(Destination.MaxKeyNameLength);
            destination.Property(d => d.KeyPrefix).HasMaxLength(Destination.MaxKeyPrefixLength);
            destination.Property(d => d.KeyCheckedAt).HasConversion(NullableUtc);
            destination.Property(d => d.KeyProblem).HasMaxLength(Destination.MaxKeyProblemLength);
            destination.HasOne(d => d.App).WithMany().HasForeignKey(d => d.AppId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Connection>(connection =>
        {
            connection.ToTable("connections");
            connection.Property(c => c.Name).HasMaxLength(Connection.MaxNameLength);
            connection.Property(c => c.Connector).HasMaxLength(32);
            connection.Property(c => c.CreatedAt).HasConversion(Utc);
            connection.HasOne(c => c.Workspace).WithMany().HasForeignKey(c => c.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Sync>(sync =>
        {
            sync.ToTable("syncs");
            sync.Property(s => s.Stream).HasMaxLength(255);
            sync.Property(s => s.Table).HasMaxLength(Sync.MaxTableLength);
            sync.Property(s => s.Mode).HasConversion<string>().HasMaxLength(16);
            sync.Property(s => s.CursorField).HasMaxLength(255);
            sync.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
            sync.Property(s => s.CreatedAt).HasConversion(Utc);
            sync.HasOne(s => s.Connection).WithMany().HasForeignKey(s => s.ConnectionId).OnDelete(DeleteBehavior.Cascade);
            sync.HasOne(s => s.Destination).WithMany().HasForeignKey(s => s.DestinationId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Run>(run =>
        {
            run.ToTable("runs");
            run.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            run.Property(r => r.ErrorKind).HasConversion<string>().HasMaxLength(16);
            run.Property(r => r.Error).HasMaxLength(Run.MaxErrorLength);
            run.Property(r => r.StartedAt).HasConversion(Utc);
            run.Property(r => r.EndedAt).HasConversion(NullableUtc);
            run.HasOne(r => r.Sync).WithMany().HasForeignKey(r => r.SyncId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
