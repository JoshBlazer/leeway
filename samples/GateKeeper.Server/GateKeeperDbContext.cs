using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GateKeeper.Server;

public class GateKeeperDbContext(DbContextOptions<GateKeeperDbContext> options) : DbContext(options)
{
    public DbSet<Zone> Zones => Set<Zone>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQLite cannot compare or order DateTimeOffset, and the pull query does both on UpdatedAt.
        // Store Unix milliseconds. See docs/design/datasync-integration.md, "SQLite on the server".
        ValueConverter<DateTimeOffset, long> unixMilliseconds = new(
            v => v.ToUnixTimeMilliseconds(),
            v => DateTimeOffset.FromUnixTimeMilliseconds(v));

        modelBuilder.Entity<Zone>(zone =>
        {
            zone.Property(z => z.UpdatedAt).HasConversion(unixMilliseconds);

            // BaseEntityTableData declares no concurrency token, so without this the version check in
            // EntityTableRepository is check-then-write and two simultaneous requests can both pass it.
            zone.Property(z => z.Version).IsConcurrencyToken();
        });
    }
}
