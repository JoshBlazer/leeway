using Leeway.Core;
using Leeway.Datasync.Client;
using Leeway.Datasync.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Leeway.Integration.Tests.Leeway;

// ---------- Server side: the same attributes as the client ----------

public class ServerZone : LeewayEntity
{
    [Escrow(Min = 0, Lease = 10)]
    public int RemainingCapacity { get; set; }

    [Counter]
    public int CheckIns { get; set; }

    [LastWriterWins]
    public string? Notes { get; set; }

    [LastWriterWins]
    public string? Steward { get; set; }

    [AddWinsSet]
    public string? FlagsJson { get; set; }
}

public class TestServerDb(DbContextOptions<TestServerDb> options) : DbContext(options)
{
    public DbSet<ServerZone> Zones => Set<ServerZone>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyLeewayServer();
        modelBuilder.LeewayEntity<ServerZone>();
    }
}

[Route("tables/leewayoperations")]
public class TestOperationsController(TestServerDb db, LeewayAuthority<TestServerDb> authority) : LeewayOperationsController<TestServerDb>(db, authority);

[Route("tables/leewayleases")]
public class TestLeasesController(TestServerDb db) : LeewayLeasesController<TestServerDb>(db);

[Route("tables/zone")]
public class TestZonesController(TestServerDb db) : LeewayEntityController<ServerZone, TestServerDb>(db);

/// <summary>A clock tests can set, so that same-millisecond stamps and skew are deterministic.</summary>
public sealed class TestClock(long startMs = 1_800_000_000_000) : TimeProvider
{
    public long Ms { get; set; } = startMs;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Ms);
}

public sealed class TestServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly string directory;

    private TestServer(WebApplication app, Uri baseAddress, string directory, TestClock clock)
    {
        this.app = app;
        BaseAddress = baseAddress;
        this.directory = directory;
        Clock = clock;
    }

    public Uri BaseAddress { get; }

    public TestClock Clock { get; }

    public static async Task<TestServer> StartAsync(int capacity = 25)
    {
        string directory = Directory.CreateTempSubdirectory("leeway-it-").FullName;
        TestClock clock = new();

        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // Port 0 lets the OS choose at bind time. Reserving a port first would race with tests running in parallel.
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<TestServerDb>(o => o.UseSqlite($"Data Source={Path.Combine(directory, "server.db")}"));
        builder.Services.AddControllers().AddApplicationPart(typeof(TestOperationsController).Assembly);
        builder.Services.AddLeewayServer<TestServerDb>(o =>
        {
            o.Entity<ServerZone>("Zone");
            o.TimeProvider = clock;
        });

        WebApplication app = builder.Build();
        app.MapControllers();

        using (IServiceScope scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<TestServerDb>().Database.EnsureCreatedAsync();
            await scope.ServiceProvider.GetRequiredService<LeewayAuthority<TestServerDb>>()
                .CreateEntityAsync(new ServerZone { Id = "north", RemainingCapacity = capacity, CheckIns = 0, Notes = "seeded" });
        }

        await app.StartAsync();
        return new TestServer(app, new Uri(app.Urls.First() + "/"), directory, clock);
    }

    public async Task WithAuthorityAsync(Func<TestServerDb, LeewayAuthority<TestServerDb>, Task> action)
    {
        using IServiceScope scope = this.app.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<TestServerDb>(), scope.ServiceProvider.GetRequiredService<LeewayAuthority<TestServerDb>>());
    }

    public async Task<ServerZone> ZoneAsync(string id = "north")
    {
        using IServiceScope scope = this.app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TestServerDb>().Zones.AsNoTracking().SingleAsync(z => z.Id == id);
    }

    public async Task<EscrowLedger> LedgerAsync(string id = "north")
    {
        EscrowLedger? ledger = null;
        await WithAuthorityAsync(async (_, authority) => ledger = await authority.GetLedgerAsync(MergeSchema.EscrowResource("Zone", id, nameof(ServerZone.RemainingCapacity))));
        return ledger!;
    }

    public async ValueTask DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(this.directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp directory cleanup is best effort.
        }
    }
}

// ---------- Client side ----------

public class Zone : OfflineEntity
{
    [Escrow(Min = 0, Lease = 10)]
    public int RemainingCapacity { get; set; }

    [Counter]
    public int CheckIns { get; set; }

    [LastWriterWins]
    public string? Notes { get; set; }

    [LastWriterWins]
    public string? Steward { get; set; }

    [AddWinsSet]
    public string? FlagsJson { get; set; }
}

public class GateDb(DbContextOptions<GateDb> options, LeewayClientOptions leeway) : LeewayDbContext(options, leeway)
{
    public DbSet<Zone> Zones => Set<Zone>();
}

public sealed class NetworkFault : DelegatingHandler
{
    public bool Offline { get; set; }

    public bool Hang { get; set; }

    /// <summary>Only pushes time out, so that a pull cannot hide what a push reported.</summary>
    public bool HangPosts { get; set; }

    public List<string> Requests { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage, Task>? AfterSend { get; set; }

    public Func<HttpRequestMessage, Task>? BeforeSend { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Offline)
        {
            throw new HttpRequestException("offline");
        }

        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (HangPosts && request.Method == HttpMethod.Post)
        {
            await Task.Delay(50, cancellationToken);
            throw new TaskCanceledException("Fails the way HttpClient.Timeout does. Probes C24 and C25 pin the real thing.");
        }

        Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
        if (BeforeSend is not null)
        {
            await BeforeSend(request);
        }

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
        if (AfterSend is not null)
        {
            await AfterSend(request, response);
        }

        return response;
    }
}

/// <summary>A device: a real SQLite file, a real LeewayClient, and an HTTP handler for injecting faults.</summary>
public sealed class Gate : IDisposable
{
    private readonly string path;
    private readonly bool ownsFile;

    public Gate(TestServer server, string name, TimeSpan? timeout = null, string? existingPath = null)
    {
        Name = name;
        this.ownsFile = existingPath is null;
        this.path = existingPath ?? Path.Combine(Path.GetTempPath(), $"leeway-gate-{Guid.NewGuid():N}.db");
        Fault = new NetworkFault { InnerHandler = new SocketsHttpHandler() };
        Clock = new TestClock(server.Clock.Ms);
        HttpClient http = new(Fault) { BaseAddress = server.BaseAddress, Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        LeewayClientOptions options = new() { DeviceId = name, HttpClient = http, TimeProvider = Clock };
        DbContextOptions<GateDb> contextOptions = new DbContextOptionsBuilder<GateDb>().UseSqlite($"Data Source={this.path}").Options;
        Client = new LeewayClient<GateDb>(() => new GateDb(contextOptions, options), options);
        Path_ = this.path;
    }

    public string Name { get; }

    public string Path_ { get; }

    public NetworkFault Fault { get; }

    public TestClock Clock { get; }

    public LeewayClient<GateDb> Client { get; }

    public static async Task<Gate> OpenAsync(TestServer server, string name, TimeSpan? timeout = null)
    {
        Gate gate = new(server, name, timeout);
        await gate.Client.InitializeAsync();
        Assert.Equal(SyncState.Synced, (await gate.Client.SyncAsync()).State);
        return gate;
    }

    public Task<Zone> ZoneAsync(string id = "north") => Client.ReadAsync<Zone>(id);

    public void Dispose()
    {
        if (this.ownsFile)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(this.path);
            }
            catch (IOException)
            {
                // Temp file cleanup is best effort.
            }
        }
    }
}
