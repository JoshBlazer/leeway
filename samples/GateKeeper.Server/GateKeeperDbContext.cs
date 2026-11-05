using Leeway.Datasync.Server;
using Microsoft.EntityFrameworkCore;

namespace GateKeeper.Server;

public class GateKeeperDbContext(DbContextOptions<GateKeeperDbContext> options) : DbContext(options)
{
    public DbSet<Zone> Zones => Set<Zone>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyLeewayServer();
        modelBuilder.LeewayEntity<Zone>();
        modelBuilder.LeewayEntity<Ticket>();
    }
}
