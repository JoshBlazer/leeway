using CommunityToolkit.Datasync.Client.Offline;
using Microsoft.EntityFrameworkCore;

namespace GateKeeper.Desktop.Data;

public enum SyncState
{
    Synced,
    Offline,
    Conflict,
}

/// <param name="State">The overall outcome.</param>
/// <param name="ConflictedOperations">Queued operations the server rejected with 409 or 412.</param>
public sealed record SyncOutcome(SyncState State, int ConflictedOperations = 0);

/// <summary>Everything the gate does to its local store. Uses one short-lived DbContext per call.</summary>
public sealed class GateStore
{
    private readonly DbContextOptions<GateDbContext> options;
    private readonly HttpClient httpClient;

    public GateStore(string connectionString, HttpClient httpClient)
    {
        this.options = new DbContextOptionsBuilder<GateDbContext>().UseSqlite(connectionString).Options;
        this.httpClient = httpClient;
    }

    private GateDbContext NewContext() => new(this.options, this.httpClient);

    public async Task InitializeAsync()
    {
        await using GateDbContext context = NewContext();
        await context.Database.EnsureCreatedAsync();
    }

    public async Task<List<Zone>> GetZonesAsync()
    {
        await using GateDbContext context = NewContext();
        return await context.Zones.AsNoTracking().OrderBy(z => z.Name).ToListAsync();
    }

    public async Task<int> CountPendingAsync()
    {
        await using GateDbContext context = NewContext();
        return await context.DatasyncOperationsQueue.CountAsync();
    }

    /// <summary>Writes locally only. Needs no network: the change is queued for the next sync.</summary>
    public async Task SaveNotesAsync(string zoneId, string? notes)
    {
        await using GateDbContext context = NewContext();
        Zone zone = await context.Zones.SingleAsync(z => z.Id == zoneId);
        zone.Notes = notes;
        await context.SaveChangesAsync();
    }

    /// <summary>Pushes queued changes, then pulls. Push must succeed first: Datasync refuses to pull over a non-empty queue.</summary>
    public async Task<SyncOutcome> SyncAsync()
    {
        await using GateDbContext context = NewContext();
        try
        {
            PushResult push = await context.PushAsync();
            if (!push.IsSuccessful)
            {
                return new SyncOutcome(SyncState.Conflict, push.FailedRequests.Count);
            }

            // IsSuccessful is not enough: a push whose requests time out reports success with the
            // operations still queued (probe C24), and pulling over a non-empty queue throws.
            if (await context.DatasyncOperationsQueue.AnyAsync())
            {
                return new SyncOutcome(SyncState.Offline);
            }

            PullResult pull = await context.PullAsync();
            return pull.IsSuccessful ? new SyncOutcome(SyncState.Synced) : new SyncOutcome(SyncState.Offline);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            return new SyncOutcome(SyncState.Offline);
        }
    }
}
