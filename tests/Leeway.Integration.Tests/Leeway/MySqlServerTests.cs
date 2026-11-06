using System.Net.Http.Json;
using Leeway.Core;
using Leeway.Datasync.Client;

namespace Leeway.Integration.Tests.Leeway;

/// <summary>
/// What SQLite cannot show: several server processes writing at once. Skipped unless LEEWAY_TEST_MYSQL is set. With it
/// set, every other end-to-end test also runs against MySQL. See docs/design/hardening.md and ADR-0019.
/// </summary>
public class MySqlServerTests
{
    private static void RequireMySql() => Assert.SkipWhen(TestServer.MySqlServer is null, "LEEWAY_TEST_MYSQL is not set.");

    private static Operation Merge(string id, string device, long sequence, MergeDocument delta) => new(
        id, device, sequence, new HybridTimestamp(1_800_000_000_000, 0, device), default, OperationKind.Merge, "Zone", "north", delta);

    private static Operation Escrow(string id, string device, long sequence, OperationKind kind, long amount) => new(
        id, device, sequence, new HybridTimestamp(1_800_000_000_000, 0, device), default, kind, "Zone", "north",
        Resource: MergeSchema.EscrowResource("Zone", "north", nameof(ServerZone.RemainingCapacity)), Amount: amount);

    private static Task<HttpResponseMessage> PostAsync(HttpClient http, Operation op)
        => http.PostAsync("tables/leewayoperations", JsonContent.Create(new { id = op.Id, device = op.Device, payload = op.ToJson() }));

    /// <summary>The in-process write gate cannot help here, so only the database lock can.</summary>
    [Fact]
    public async Task Two_servers_writing_at_once_lose_nothing()
    {
        RequireMySql();
        await using TestServer one = await TestServer.StartAsync();
        await using TestServer two = await one.StartAnotherServerAsync();
        using HttpClient httpOne = new() { BaseAddress = one.BaseAddress };
        using HttpClient httpTwo = new() { BaseAddress = two.BaseAddress };

        const int Writers = 40;
        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, Writers).Select(i =>
        {
            string device = $"device-{i}";
            MergeDocument delta = MergeDocument.Empty.WithCounter(nameof(ServerZone.CheckIns), PnCounter.Empty.AddDelta(device, 1));
            return PostAsync(i % 2 == 0 ? httpOne : httpTwo, Merge($"op{i:D4}", device, 1, delta));
        }));

        Assert.All(responses, r => Assert.Equal(201, (int)r.StatusCode));
        Assert.Equal(Writers, (await one.ZoneAsync()).CheckIns);
    }

    /// <summary>The headline rule, across servers: concurrent grants and spends must never exceed the total.</summary>
    [Fact]
    public async Task Two_servers_granting_at_once_cannot_exceed_the_total()
    {
        RequireMySql();
        const int Capacity = 25;
        await using TestServer one = await TestServer.StartAsync(capacity: Capacity);
        await using TestServer two = await one.StartAnotherServerAsync();
        using HttpClient httpOne = new() { BaseAddress = one.BaseAddress };
        using HttpClient httpTwo = new() { BaseAddress = two.BaseAddress };

        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            PostAsync(i % 2 == 0 ? httpOne : httpTwo, Escrow($"lease{i:D4}", $"device-{i}", 1, OperationKind.LeaseRequest, 3))));

        Assert.All(responses, r => Assert.Equal(201, (int)r.StatusCode));
        EscrowLedger ledger = await one.LedgerAsync();
        Assert.True(ledger.IsConserved);
        Assert.Equal(0, ledger.Unleased);
        Assert.Equal(Capacity, ledger.Accounts.Values.Sum(a => a.Granted));
    }

    /// <summary>
    /// ADR-0013's limit: a row committed late with an earlier stamp would be missed by a pull that had moved on.
    /// With every write serialized, stamp order is commit order, so a device reading throughout still ends up complete.
    /// </summary>
    [Fact]
    public async Task A_device_pulling_throughout_concurrent_writes_misses_nothing()
    {
        RequireMySql();
        await using TestServer one = await TestServer.StartAsync();
        await using TestServer two = await one.StartAnotherServerAsync();
        using HttpClient httpOne = new() { BaseAddress = one.BaseAddress };
        using HttpClient httpTwo = new() { BaseAddress = two.BaseAddress };
        using Gate reader = await Gate.OpenAsync(one, "reader");

        const int Writers = 60;
        Task<HttpResponseMessage[]> writers = Task.WhenAll(Enumerable.Range(0, Writers).Select(i =>
        {
            string device = $"device-{i}";
            MergeDocument delta = MergeDocument.Empty.WithCounter(nameof(ServerZone.CheckIns), PnCounter.Empty.AddDelta(device, 1));
            return PostAsync(i % 2 == 0 ? httpOne : httpTwo, Merge($"op{i:D4}", device, 1, delta));
        }));

        while (!writers.IsCompleted)
        {
            await reader.Client.PullAsync();
        }

        // Without this, a failed write would look like a missed row.
        Assert.All(await writers, r => Assert.Equal(201, (int)r.StatusCode));
        await reader.Client.PullAsync();
        Assert.Equal(Writers, (await reader.ZoneAsync()).CheckIns);
    }

    /// <summary>With MySQL's default collation, gate-a and GATE-A would match in the access checks, so this must fail loudly.</summary>
    [Fact]
    public async Task A_database_with_a_case_insensitive_collation_is_refused()
    {
        RequireMySql();
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => TestServer.StartAsync(mySqlCollation: "utf8mb4_0900_ai_ci"));
        Assert.Contains("collation", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Runs on either database.</summary>
    [Fact]
    public async Task Device_ids_that_differ_only_by_case_are_different_devices()
    {
        await using TestServer server = await TestServer.StartAsync(authenticate: http => http.Request.Headers["X-Test-Device"].FirstOrDefault());
        using HttpClient lower = new() { BaseAddress = server.BaseAddress };
        using HttpClient upper = new() { BaseAddress = server.BaseAddress };
        lower.DefaultRequestHeaders.Add("X-Test-Device", "gate-a");
        upper.DefaultRequestHeaders.Add("X-Test-Device", "GATE-A");

        using HttpResponseMessage lease = await PostAsync(lower, Escrow("lease1", "gate-a", 1, OperationKind.LeaseRequest, 3));
        Assert.Equal(201, (int)lease.StatusCode);

        Assert.DoesNotContain("gate-a", await upper.GetStringAsync("tables/leewayleases"));
        Assert.Contains("gate-a", await lower.GetStringAsync("tables/leewayleases"));
    }
}
