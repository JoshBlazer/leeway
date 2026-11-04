using CommunityToolkit.Datasync.Client.Offline;
using Microsoft.EntityFrameworkCore;

namespace GateKeeper.Desktop.Data;

/// <summary>
/// The gate's offline store. M0 deliberately configures no conflict resolver: this is Datasync's
/// default behaviour, the baseline Leeway improves on.
/// </summary>
public class GateDbContext(DbContextOptions<GateDbContext> options, HttpClient httpClient) : OfflineDbContext(options)
{
    public DbSet<Zone> Zones => Set<Zone>();

    protected override void OnDatasyncInitialization(DatasyncOfflineOptionsBuilder optionsBuilder)
        => optionsBuilder.UseHttpClient(httpClient);
}
