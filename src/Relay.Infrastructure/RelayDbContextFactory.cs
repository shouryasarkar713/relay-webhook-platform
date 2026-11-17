using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Relay.Infrastructure;

public sealed class RelayDbContextFactory : IDesignTimeDbContextFactory<RelayDbContext>
{
    public RelayDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<RelayDbContext>();
        optionsBuilder.UseSqlServer("Server=localhost,1433;Database=RelayDb;User Id=sa;Password=Your_Strong_Password123!;TrustServerCertificate=True;MultipleActiveResultSets=True;", sqlOptions =>
        {
            sqlOptions.MigrationsAssembly(typeof(RelayDbContext).Assembly.FullName);
        });

        return new RelayDbContext(optionsBuilder.Options);
    }
}
