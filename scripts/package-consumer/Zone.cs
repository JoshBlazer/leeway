using Leeway.Core;
using Leeway.Datasync.Client;
using Microsoft.EntityFrameworkCore;

namespace Consumer;

public class Zone : OfflineEntity
{
    [Escrow(Min = 0, Lease = 10)] public int RemainingCapacity { get; set; }
    [Counter] public int CheckIns { get; set; }
    [LastWriterWins] public string? Notes { get; set; }
    public int Unprotected { get; set; }
}

public class GateDb(DbContextOptions<GateDb> options, LeewayClientOptions leeway) : LeewayDbContext(options, leeway)
{
    public DbSet<Zone> Zones => Set<Zone>();
}

public static class Use
{
    // Only compiles if the generated API arrived through the package.
    public static async Task Typed(LeewayClient<GateDb> client)
    {
        await client.TrySpendRemainingCapacityAsync("north", 1);
        await client.AddCheckInsAsync("north", 1);
        await client.SetNotesAsync("north", "packaged");
    }

    // Should produce LEE001 and LEE002 through the package.
    public static void Wrong(Zone zone)
    {
        zone.Unprotected--;
        zone.Notes = "direct";
    }

    public static bool Generated => MergeSchema.For(typeof(Zone)).IsGenerated;
}
