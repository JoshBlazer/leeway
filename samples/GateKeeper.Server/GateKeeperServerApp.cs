using System.Text.Json;
using Leeway.Core;
using Leeway.Datasync.Server;
using Microsoft.EntityFrameworkCore;

namespace GateKeeper.Server;

/// <summary>Builds the server, so that Program.cs and the end-to-end tests run exactly the same app.</summary>
public static class GateKeeperServerApp
{
    public static WebApplication Build(string[] args, string? connectionString = null, string? url = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        connectionString ??= builder.Configuration.GetConnectionString("GateKeeper") ?? "Data Source=gatekeeper-server.db";
        if (url is not null)
        {
            builder.WebHost.UseUrls(url);
        }

        builder.Services.AddDbContext<GateKeeperDbContext>(options => options.UseSqlite(connectionString));
        builder.Services.AddControllers().AddApplicationPart(typeof(ZonesController).Assembly);
        builder.Services.AddLeewayServer<GateKeeperDbContext>(options =>
        {
            options.Entity<Zone>();
            options.Entity<Ticket>();
        });

        WebApplication app = builder.Build();
        app.MapControllers();
        app.MapGet("/admin/state", GetStateAsync);
        return app;
    }

    /// <summary>Creates the schema and seeds the venue the first time the database is used.</summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        using IServiceScope scope = app.Services.CreateScope();
        GateKeeperDbContext context = scope.ServiceProvider.GetRequiredService<GateKeeperDbContext>();
        await context.Database.EnsureCreatedAsync();
        if (await context.Zones.AnyAsync())
        {
            return;
        }

        LeewayAuthority<GateKeeperDbContext> authority = scope.ServiceProvider.GetRequiredService<LeewayAuthority<GateKeeperDbContext>>();
        await authority.CreateEntityAsync(new Zone { Id = "zone-north", Name = "North Stand", Capacity = 10, RemainingCapacity = 10 });
        await authority.CreateEntityAsync(new Zone { Id = "zone-south", Name = "South Stand", Capacity = 100, RemainingCapacity = 100 });
        foreach (int number in Enumerable.Range(1001, 30))
        {
            await authority.CreateEntityAsync(new Ticket { Id = $"T-{number}", ZoneId = "zone-north" });
        }

        foreach (int number in Enumerable.Range(2001, 30))
        {
            await authority.CreateEntityAsync(new Ticket { Id = $"S-{number}", ZoneId = "zone-south" });
        }
    }

    /// <summary>What the dashboard shows. A sample only: a real one would need authentication.</summary>
    private static async Task<IResult> GetStateAsync(GateKeeperDbContext context, LeewayAuthority<GateKeeperDbContext> authority)
    {
        List<object> zones = [];
        foreach (Zone zone in await context.Zones.AsNoTracking().OrderBy(z => z.Name).ToListAsync())
        {
            EscrowLedger ledger = (await authority.GetLedgerAsync(MergeSchema.EscrowResource(nameof(Zone), zone.Id, nameof(Zone.RemainingCapacity))))!;
            zones.Add(new
            {
                id = zone.Id,
                name = zone.Name,
                capacity = ledger.Total,
                consumed = ledger.Consumed,
                remaining = ledger.Remaining,
                unleased = ledger.Unleased,
                held = ledger.Accounts.Values.Sum(a => a.Held),
                conserved = ledger.IsConserved,
                checkIns = zone.CheckIns,
                notes = zone.Notes,
                flags = zone.Flags is null ? [] : JsonSerializer.Deserialize<string[]>(zone.Flags),
                gates = ledger.Accounts.Select(a => new
                {
                    gate = a.Key,
                    granted = a.Value.Granted,
                    spent = a.Value.Spent,
                    held = a.Value.Held,
                    returned = a.Value.Returned,
                    provisional = a.Value.ProvisionalAccepted,
                    overdraft = a.Value.Overdraft,
                }),
            });
        }

        // A sample's scale. A real server would index this rather than scanning every ticket.
        List<object> duplicates = [];
        foreach (Ticket ticket in await context.Tickets.AsNoTracking().Where(t => t.ScannedAt != null).OrderBy(t => t.Id).ToListAsync())
        {
            string[] gates = JsonSerializer.Deserialize<string[]>(ticket.ScannedAt!) ?? [];
            if (gates.Length > 1)
            {
                duplicates.Add(new { ticket = ticket.Id, gates });
            }
        }

        var activity = (await context.Set<LeewayOperationRow>().AsNoTracking().OrderByDescending(o => o.UpdatedAt).Take(10).ToListAsync())
            .Select(row => new { device = row.Device, kind = Operation.FromJson(row.Payload).Kind.ToString(), outcome = row.OutcomeStatus, amount = row.OutcomeAmount });

        return Results.Json(new { zones, duplicates, activity });
    }
}
