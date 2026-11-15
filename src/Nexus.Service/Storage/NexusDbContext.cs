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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
            app.Property(a => a.CreatedAt).HasConversion(Utc);
            app.HasOne(a => a.Workspace).WithMany().HasForeignKey(a => a.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Destination>(destination =>
        {
            destination.ToTable("destinations");
            destination.Property(d => d.Url).HasMaxLength(2000);
            destination.Property(d => d.CreatedAt).HasConversion(Utc);
            destination.HasOne(d => d.App).WithMany().HasForeignKey(d => d.AppId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Connection>(connection =>
        {
            connection.ToTable("connections");
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
            sync.HasOne(s => s.Destination).WithMany().HasForeignKey(s => s.DestinationId).OnDelete(DeleteBehavior.Cascade);
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
