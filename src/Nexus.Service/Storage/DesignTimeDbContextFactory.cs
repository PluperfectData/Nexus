using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nexus.Service.Storage;

/// <summary>Lets <c>dotnet ef</c> build the model without a database or the service's configuration.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<NexusDbContext>
{
    public NexusDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<NexusDbContext>()
            .UseMySql("Server=localhost;Database=nexus", NexusDbContext.ServerVersion)
            .Options);
}
