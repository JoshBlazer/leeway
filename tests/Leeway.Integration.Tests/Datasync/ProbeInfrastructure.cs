using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using CommunityToolkit.Datasync.Client.Offline;
using CommunityToolkit.Datasync.Server;
using CommunityToolkit.Datasync.Server.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Leeway.Integration.Tests.Datasync;

// ---------- Server side ----------

public class ServerItem : RepositoryControlledEntityTableData
{
    [StringLength(200)]
    public string? A { get; set; }

    public string? B { get; set; }
}

/// <summary>An append-only table with client-assigned IDs: the candidate transport for Leeway's operation log.</summary>
public class ServerOp : RepositoryControlledEntityTableData
{
    public string? Payload { get; set; }
}

public class ProbeServerDb(DbContextOptions<ProbeServerDb> options) : DbContext(options)
{
    public DbSet<ServerItem> Items => Set<ServerItem>();

    public DbSet<ServerOp> Ops => Set<ServerOp>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ValueConverter<DateTimeOffset, long> ms = new(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));
        modelBuilder.Entity<ServerItem>().Property(x => x.UpdatedAt).HasConversion(ms);
        modelBuilder.Entity<ServerOp>().Property(x => x.UpdatedAt).HasConversion(ms);
    }
}

/// <summary>Records what the server's extension points observed, and optionally misbehaves on request.</summary>
public sealed class ServerProbe
{
    public List<string> Log { get; } = [];

    public Action<ServerItem>? PreCommit { get; set; }

    public bool ThrowInPostCommit { get; set; }

    public bool StripIfMatchInController { get; set; }

    public List<string?> DeviceHeaders { get; } = [];
}

public sealed class ProbeAccessControl(ServerProbe probe) : AccessControlProvider<ServerItem>
{
    public override ValueTask PreCommitHookAsync(TableOperation operation, ServerItem entity, CancellationToken cancellationToken = default)
    {
        probe.Log.Add($"pre:{operation}");
        probe.PreCommit?.Invoke(entity);
        return ValueTask.CompletedTask;
    }

    public override ValueTask PostCommitHookAsync(TableOperation operation, ServerItem entity, CancellationToken cancellationToken = default)
    {
        probe.Log.Add($"post:{operation}");
        return probe.ThrowInPostCommit ? throw new InvalidOperationException("post-commit failure") : ValueTask.CompletedTask;
    }
}

/// <summary>Decorates the real repository so a probe can see which writes reach the storage layer.</summary>
public sealed class ProbeRepository(ProbeServerDb db, ServerProbe probe) : EntityTableRepository<ServerItem>(db)
{
    public override ValueTask ReplaceAsync(ServerItem entity, byte[]? version = null, CancellationToken cancellationToken = default)
    {
        probe.Log.Add($"repo:replace:version={(version?.Length > 0 ? "present" : "absent")}");
        return base.ReplaceAsync(entity, version, cancellationToken);
    }
}

[Route("tables/items")]
public class ItemsController(ProbeServerDb db, ServerProbe probe)
    : TableController<ServerItem>(new ProbeRepository(db, probe), new ProbeAccessControl(probe), new TableControllerOptions { EnableSoftDelete = true })
{
    public override Task<IActionResult> ReplaceAsync([FromRoute] string id, CancellationToken cancellationToken = default)
    {
        if (probe.StripIfMatchInController)
        {
            probe.Log.Add("controller:replace-override");
            Request.Headers.Remove("If-Match");
        }

        return base.ReplaceAsync(id, cancellationToken);
    }
}

[Route("tables/ops")]
public class OpsController(ProbeServerDb db)
    : TableController<ServerOp>(new EntityTableRepository<ServerOp>(db), new TableControllerOptions { EnableSoftDelete = true });

/// <summary>A minimal Datasync server on a real socket and a private SQLite database.</summary>
public sealed class ProbeServer : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly WebApplication app;

    private ProbeServer(WebApplication app, SqliteConnection connection, Uri baseAddress, ServerProbe probe)
    {
        this.app = app;
        this.connection = connection;
        BaseAddress = baseAddress;
        Probe = probe;
    }

    public Uri BaseAddress { get; }

    public ServerProbe Probe { get; }

    public static async Task<ProbeServer> StartAsync()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Uri baseAddress = new($"http://127.0.0.1:{port}/");

        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();

        ServerProbe probe = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(baseAddress.ToString());
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(probe);
        builder.Services.AddDbContext<ProbeServerDb>(o => o.UseSqlite(connection));
        builder.Services.AddDatasyncServices();
        builder.Services.AddControllers().AddApplicationPart(typeof(ItemsController).Assembly);

        WebApplication app = builder.Build();
        app.Use((HttpContext context, RequestDelegate next) =>
        {
            probe.DeviceHeaders.Add(context.Request.Headers["X-Leeway-Device"].FirstOrDefault());
            return next(context);
        });
        app.MapControllers();

        using (IServiceScope scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ProbeServerDb>().Database.EnsureCreatedAsync();
        }

        await app.StartAsync();
        return new ProbeServer(app, connection, baseAddress, probe);
    }

    /// <summary>Runs code directly against the server's store, bypassing HTTP.</summary>
    public async Task<T> WithDbAsync<T>(Func<ProbeServerDb, Task<T>> action)
    {
        using IServiceScope scope = this.app.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ProbeServerDb>());
    }

    public Task<ServerItem> SeedAsync(string id, string? a = null, string? b = null) => WithDbAsync(async db =>
    {
        ServerItem item = new() { Id = id, A = a, B = b };
        await new EntityTableRepository<ServerItem>(db).CreateAsync(item);
        return item;
    });

    public async ValueTask DisposeAsync()
    {
        await this.app.StopAsync();
        await this.app.DisposeAsync();
        await this.connection.DisposeAsync();
    }
}

// ---------- Client side ----------

public class ClientItem
{
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset? UpdatedAt { get; set; }

    public string? Version { get; set; }

    public bool Deleted { get; set; }

    public string? A { get; set; }

    public string? B { get; set; }

    /// <summary>Client-only. Datasync must neither send it nor overwrite it.</summary>
    [JsonIgnore]
    public string? LocalOnly { get; set; }
}

public class ClientOp
{
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset? UpdatedAt { get; set; }

    public string? Version { get; set; }

    public bool Deleted { get; set; }

    public string? Payload { get; set; }
}

/// <summary>A client-local table that must never be synchronized: the candidate home for the op log and wallet.</summary>
public class LocalLedgerRow
{
    public string Id { get; set; } = string.Empty;

    public string? Note { get; set; }
}

public class ProbeClientDb(DbContextOptions<ProbeClientDb> options, ProbeClientConfig config) : OfflineDbContext(options)
{
    public DbSet<ClientItem> Items => Set<ClientItem>();

    public DbSet<ClientOp> Ops => Set<ClientOp>();

    [DoNotSynchronize]
    public DbSet<LocalLedgerRow> LocalLedger => Set<LocalLedgerRow>();

    protected override void OnDatasyncInitialization(DatasyncOfflineOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseHttpClient(config.Http);
        optionsBuilder.Entity<ClientItem>(cfg =>
        {
            cfg.Endpoint = new Uri("/tables/items", UriKind.Relative);
            cfg.ConflictResolver = config.ItemResolver;
        });
        optionsBuilder.Entity<ClientOp>(cfg =>
        {
            cfg.Endpoint = new Uri("/tables/ops", UriKind.Relative);
            cfg.ConflictResolver = config.OpResolver;
        });
    }
}

public sealed record RecordedRequest(string Method, string Path, string Query, string? IfMatch);

/// <summary>Client-side HTTP pipeline handler used to record traffic and inject faults at exact moments.</summary>
public sealed class FaultHandler : DelegatingHandler
{
    public bool Offline { get; set; }

    /// <summary>Requests never complete, as on a very poor signal, until HttpClient.Timeout cancels them.</summary>
    public bool Hang { get; set; }

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Runs before the request is sent. Throw from here to simulate a request that never arrived.</summary>
    public Func<HttpRequestMessage, Task>? BeforeSend { get; set; }

    /// <summary>Runs after a response arrives. Throw from here to simulate a lost response: the server did the work.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage, Task>? AfterSend { get; set; }

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

        Requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!.AbsolutePath, Uri.UnescapeDataString(request.RequestUri.Query), request.Headers.IfMatch.FirstOrDefault()?.Tag));
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

public sealed class ProbeClientConfig
{
    public required HttpClient Http { get; init; }

    public IConflictResolver? ItemResolver { get; set; }

    public IConflictResolver? OpResolver { get; set; }
}

/// <summary>A device: one SQLite store that outlives individual DbContexts, so a test can simulate an app restart.</summary>
public sealed class ProbeClient : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly List<ProbeClientDb> contexts = [];

    public ProbeClient(ProbeServer server, TimeSpan? timeout = null)
    {
        Fault = new FaultHandler { InnerHandler = new SocketsHttpHandler() };
        Config = new ProbeClientConfig { Http = new HttpClient(Fault) { BaseAddress = server.BaseAddress, Timeout = timeout ?? TimeSpan.FromSeconds(30) } };
        this.connection.Open();
        using ProbeClientDb db = NewContextUntracked();
        db.Database.EnsureCreated();
    }

    public FaultHandler Fault { get; }

    public ProbeClientConfig Config { get; }

    private ProbeClientDb NewContextUntracked()
        => new(new DbContextOptionsBuilder<ProbeClientDb>().UseSqlite(this.connection).Options, Config);

    /// <summary>A fresh context, as after an app restart: only what was committed to SQLite survives.</summary>
    public ProbeClientDb NewContext()
    {
        ProbeClientDb db = NewContextUntracked();
        this.contexts.Add(db);
        return db;
    }

    public async Task EditAsync(string id, Action<ClientItem> edit)
    {
        ProbeClientDb db = NewContext();
        ClientItem item = await db.Items.SingleAsync(x => x.Id == id);
        edit(item);
        await db.SaveChangesAsync();
    }

    public async Task<ClientItem> ReadAsync(string id)
        => await NewContext().Items.AsNoTracking().SingleAsync(x => x.Id == id);

    public async ValueTask DisposeAsync()
    {
        foreach (ProbeClientDb db in this.contexts)
        {
            await db.DisposeAsync();
        }

        await this.connection.DisposeAsync();
    }
}

/// <summary>Merges each field by a caller-supplied rule, and records what Datasync passed in.</summary>
public sealed class RecordingMergeResolver(Func<ClientItem, ClientItem, ClientItem> merge) : AbstractConflictResolver<ClientItem>
{
    public int Calls { get; private set; }

    public ClientItem? SeenClient { get; private set; }

    public ClientItem? SeenServer { get; private set; }

    public override Task<ConflictResolution> ResolveConflictAsync(ClientItem? clientObject, ClientItem? serverObject, CancellationToken cancellationToken = default)
    {
        Calls++;
        SeenClient = clientObject;
        SeenServer = serverObject;
        return Task.FromResult(new ConflictResolution { Result = ConflictResolutionResult.Client, Entity = merge(clientObject!, serverObject!) });
    }
}
