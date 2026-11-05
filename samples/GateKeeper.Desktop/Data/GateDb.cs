using CommunityToolkit.Datasync.Client.Offline;
using Leeway.Datasync.Client;
using Microsoft.EntityFrameworkCore;

namespace GateKeeper.Desktop.Data;

public class GateDb(DbContextOptions<GateDb> options, LeewayClientOptions leeway) : LeewayDbContext(options, leeway)
{
    public DbSet<Zone> Zones => Set<Zone>();

    public DbSet<Ticket> Tickets => Set<Ticket>();

    [DoNotSynchronize]
    public DbSet<GateScan> Scans => Set<GateScan>();
}
