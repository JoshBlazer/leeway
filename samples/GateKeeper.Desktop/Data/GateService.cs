using System.Text.Json;
using Leeway.Core;
using Leeway.Datasync.Client;
using Microsoft.EntityFrameworkCore;

namespace GateKeeper.Desktop.Data;

public enum ScanOutcome
{
    Admitted,
    Provisional,
    UnknownTicket,
    WrongZone,
    AlreadyScanned,
}

public sealed record ScanResult(ScanOutcome Outcome, string? ScannedAt = null);

public sealed record DuplicateTicket(string TicketId, IReadOnlyList<string> Gates);

/// <summary>A gate's work, in terms of Leeway. See docs/design/gatekeeper.md.</summary>
public sealed class GateService
{
    private readonly LeewayClient<GateDb> client;

    public GateService(string gateName, string connectionString, HttpClient httpClient, TimeProvider? timeProvider = null)
    {
        GateName = gateName;
        LeewayClientOptions options = new() { DeviceId = gateName, HttpClient = httpClient, TimeProvider = timeProvider ?? TimeProvider.System };
        DbContextOptions<GateDb> contextOptions = new DbContextOptionsBuilder<GateDb>().UseSqlite(connectionString).Options;
        this.client = new LeewayClient<GateDb>(() => new GateDb(contextOptions, options), options);
    }

    public string GateName { get; }

    public Task InitializeAsync() => this.client.InitializeAsync();

    public async Task<List<Zone>> GetZonesAsync()
    {
        await using GateDb context = this.client.CreateContext();
        return await context.Zones.AsNoTracking().OrderBy(z => z.Name).ToListAsync();
    }

    public Task<Wallet> GetWalletAsync(string zoneId) => this.client.RemainingCapacityWalletAsync(zoneId);

    public Task RequestLeaseAsync(string zoneId) => this.client.RequestRemainingCapacityLeaseAsync(zoneId);

    public Task SaveNotesAsync(string zoneId, string? notes) => this.client.SetNotesAsync(zoneId, notes);

    public Task AddFlagAsync(string zoneId, string flag) => this.client.AddToFlagsAsync(zoneId, flag);

    public async Task<ScanResult> ScanAsync(string zoneId, string ticketId)
    {
        Ticket? ticket;
        await using (GateDb context = this.client.CreateContext())
        {
            ticket = await context.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == ticketId);
        }

        if (ticket is null)
        {
            return new ScanResult(ScanOutcome.UnknownTicket);
        }

        if (ticket.ZoneId != zoneId)
        {
            return new ScanResult(ScanOutcome.WrongZone);
        }

        string[] scannedAt = ParseSet(ticket.ScannedAt);
        if (scannedAt.Length > 0)
        {
            return new ScanResult(ScanOutcome.AlreadyScanned, string.Join(", ", scannedAt));
        }

        // Spend first: if a crash splits these, capacity is spent rather than created.
        SpendResult spend = await this.client.TrySpendRemainingCapacityAsync(zoneId, 1);
        await this.client.AddToScannedAtAsync(ticketId, GateName);
        await this.client.AddCheckInsAsync(zoneId, 1);

        bool confirmed = spend.Status == SpendStatus.Confirmed;
        await using (GateDb context = this.client.CreateContext())
        {
            long next = (await context.Scans.MaxAsync(s => (long?)s.Order) ?? 0) + 1;
            context.Scans.Add(new GateScan
            {
                Id = Guid.NewGuid().ToString("N"),
                Order = next,
                TicketId = ticketId,
                ZoneId = zoneId,
                State = confirmed ? ScanState.Admitted : ScanState.Provisional,
                SpendOperationId = spend.OperationId,
            });
            await context.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false);
        }

        return new ScanResult(confirmed ? ScanOutcome.Admitted : ScanOutcome.Provisional);
    }

    /// <summary>Syncs, then settles provisional scans. A rejected one means undoing its scan and count, and syncing that too.</summary>
    public async Task<SyncReport> SyncAsync()
    {
        SyncReport report = await this.client.SyncAsync();
        if (report.State == SyncState.Synced && await SettleProvisionalScansAsync() > 0)
        {
            report = await this.client.SyncAsync();
        }

        return report;
    }

    private async Task<int> SettleProvisionalScansAsync()
    {
        await using GateDb context = this.client.CreateContext();
        int turnedAway = 0;
        foreach (GateScan scan in await context.Scans.Where(s => s.State == ScanState.Provisional).ToListAsync())
        {
            OperationOutcome? outcome = await this.client.OutcomeOfAsync(scan.SpendOperationId);
            if (outcome?.Status == OutcomeStatus.ProvisionalAccepted)
            {
                scan.State = ScanState.Admitted;
            }
            else if (outcome?.Status == OutcomeStatus.ProvisionalRejected)
            {
                scan.State = ScanState.TurnedAway;
                await this.client.RemoveFromScannedAtAsync(scan.TicketId, GateName);
                await this.client.AddCheckInsAsync(scan.ZoneId, -1);
                turnedAway++;
            }
        }

        await context.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false);
        return turnedAway;
    }

    public async Task<List<GateScan>> GetScansAsync()
    {
        await using GateDb context = this.client.CreateContext();
        return await context.Scans.AsNoTracking().OrderByDescending(s => s.Order).ToListAsync();
    }

    /// <summary>Tickets more than one gate has scanned. Each gate learns of this when it pulls.</summary>
    public async Task<List<DuplicateTicket>> GetDuplicatesAsync()
    {
        await using GateDb context = this.client.CreateContext();
        List<Ticket> scanned = await context.Tickets.AsNoTracking().Where(t => t.ScannedAt != null && t.ScannedAt != "[]").OrderBy(t => t.Id).ToListAsync();
        return scanned.Select(t => new DuplicateTicket(t.Id, ParseSet(t.ScannedAt))).Where(d => d.Gates.Count > 1).ToList();
    }

    public static string[] ParseSet(string? json) => json is null ? [] : JsonSerializer.Deserialize<string[]>(json) ?? [];
}
