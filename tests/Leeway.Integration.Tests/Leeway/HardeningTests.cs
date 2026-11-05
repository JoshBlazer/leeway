using System.Net.Http.Json;
using System.Text.Json;
using CommunityToolkit.Datasync.Client.Offline;
using Leeway.Core;
using Leeway.Datasync.Client;
using Leeway.Datasync.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OperationKind = Leeway.Core.OperationKind;

namespace Leeway.Integration.Tests.Leeway;

/// <summary>The "before real use" list. See docs/design/hardening.md.</summary>
public class HardeningTests
{
    private const string Header = "X-Test-Device";

    private static string? FromHeader(HttpContext http) => http.Request.Headers[Header].FirstOrDefault();

    private static async Task<Gate> OpenAuthenticatedAsync(TestServer server, string name)
    {
        Gate gate = new(server, name);
        gate.Fault.BeforeSend = request =>
        {
            request.Headers.Add(Header, name);
            return Task.CompletedTask;
        };
        await gate.Client.InitializeAsync();
        Assert.Equal(SyncState.Synced, (await gate.Client.SyncAsync()).State);
        return gate;
    }

    private static HttpClient RawClient(TestServer server, string? device)
    {
        HttpClient http = new() { BaseAddress = server.BaseAddress };
        if (device is not null)
        {
            http.DefaultRequestHeaders.Add(Header, device);
        }

        return http;
    }

    private static string SpendPayload(string id, string device, long amount) => new Operation(
        id, device, 1, new HybridTimestamp(1, 0, device), default, OperationKind.Spend, "Zone", "north",
        Resource: MergeSchema.EscrowResource("Zone", "north", nameof(ServerZone.RemainingCapacity)), Amount: amount).ToJson();

    // ---------- Authentication ----------

    [Fact]
    public void Startup_refuses_a_server_with_no_decision_about_device_authentication()
    {
        ServiceCollection services = new();
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => services.AddLeewayServer<TestServerDb>(o => o.Entity<ServerZone>("Zone")));
        Assert.Contains("AuthenticateDevice", refused.Message);
    }

    [Fact]
    public async Task With_no_identity_every_table_refuses()
    {
        await using TestServer server = await TestServer.StartAsync(authenticate: FromHeader);
        using HttpClient anonymous = RawClient(server, null);

        foreach (string path in new[] { "tables/zone", "tables/leewayleases" })
        {
            using HttpResponseMessage read = await anonymous.GetAsync(path);
            Assert.Equal(401, (int)read.StatusCode);
        }

        using HttpResponseMessage write = await anonymous.PostAsync("tables/leewayoperations", JsonContent.Create(new { id = "a1", device = "x", payload = SpendPayload("a1", "x", 1) }));
        Assert.Equal(401, (int)write.StatusCode);
    }

    [Fact]
    public async Task A_device_cannot_send_an_operation_as_another_device()
    {
        await using TestServer server = await TestServer.StartAsync(authenticate: FromHeader);
        using Gate victim = await OpenAuthenticatedAsync(server, "victim");
        await victim.Client.RequestRemainingCapacityLeaseAsync("north", 5);
        await victim.Client.SyncAsync();

        using HttpClient mallory = RawClient(server, "mallory");
        using HttpResponseMessage forged = await mallory.PostAsync("tables/leewayoperations", JsonContent.Create(new { id = "f1", device = "victim", payload = SpendPayload("f1", "victim", 5) }));

        Assert.Equal(401, (int)forged.StatusCode);
        Assert.Equal(0, (await server.LedgerAsync()).Accounts["victim"].Spent);
    }

    [Fact]
    public async Task A_device_cannot_read_another_devices_operation_by_replaying_its_id()
    {
        await using TestServer server = await TestServer.StartAsync(authenticate: FromHeader);
        using HttpClient honest = RawClient(server, "honest");
        using HttpClient mallory = RawClient(server, "mallory");
        using HttpResponseMessage first = await honest.PostAsync("tables/leewayoperations", JsonContent.Create(new { id = "s1", device = "honest", payload = SpendPayload("s1", "honest", 1) }));
        Assert.Equal(201, (int)first.StatusCode);

        using HttpResponseMessage replay = await mallory.PostAsync("tables/leewayoperations", JsonContent.Create(new { id = "s1", device = "mallory", payload = SpendPayload("s1", "mallory", 1) }));

        Assert.Equal(400, (int)replay.StatusCode);
        Assert.DoesNotContain("honest", await replay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_device_sees_only_its_own_leases()
    {
        await using TestServer server = await TestServer.StartAsync(authenticate: FromHeader);
        using Gate a = await OpenAuthenticatedAsync(server, "gate-a");
        using Gate b = await OpenAuthenticatedAsync(server, "gate-b");
        await a.Client.RequestRemainingCapacityLeaseAsync("north");
        await b.Client.RequestRemainingCapacityLeaseAsync("north");
        await a.Client.SyncAsync();
        await b.Client.SyncAsync();

        using HttpClient http = RawClient(server, "gate-b");
        string leases = await http.GetStringAsync("tables/leewayleases");

        Assert.Contains("gate-b", leases);
        Assert.DoesNotContain("gate-a", leases);
    }

    [Fact]
    public async Task Everything_works_end_to_end_with_authentication_on()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 10, authenticate: FromHeader);
        using Gate a = await OpenAuthenticatedAsync(server, "gate-a");
        await a.Client.RequestRemainingCapacityLeaseAsync("north", 2);
        await a.Client.SyncAsync();

        Assert.Equal(SpendStatus.Confirmed, (await a.Client.TrySpendRemainingCapacityAsync("north", 1)).Status);
        await a.Client.AddCheckInsAsync("north", 1);

        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
        Assert.Equal(1, (await server.LedgerAsync()).Consumed);
        Assert.Equal(1, (await a.ZoneAsync()).CheckIns);
    }

    // ---------- Discard ----------

    [Fact]
    public async Task A_refused_spend_can_be_discarded_which_gives_the_wallet_back()
    {
        await using TestServer server = await TestServer.StartAsync(authenticate: null);
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        await a.Client.RequestLeaseAsync<Zone>("no-such-zone", z => z.RemainingCapacity);

        SyncReport report = await a.Client.SyncAsync();
        Assert.Equal(SyncState.Failed, report.State);

        RefusedOperation refused = Assert.Single(await a.Client.RefusedOperationsAsync());
        Assert.Equal(400, refused.StatusCode);
        Assert.Equal(OperationKind.LeaseRequest, refused.Kind);

        await a.Client.DiscardRefusedAsync(refused.OperationId);

        Assert.Empty(await a.Client.RefusedOperationsAsync());
        Assert.Equal(0, await a.Client.PendingCountAsync());
        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
        await using GateDb db = a.Client.CreateContext();
        Assert.Empty(await db.DatasyncOperationsQueue.ToListAsync());
    }

    [Fact]
    public async Task Discarding_a_spend_undoes_it_in_the_wallet_and_the_visible_figure()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 10);
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        await a.Client.RequestRemainingCapacityLeaseAsync("north", 5);
        await a.Client.SyncAsync();

        await a.Client.TrySpendRemainingCapacityAsync("north", 2);
        Assert.Equal(8, (await a.ZoneAsync()).RemainingCapacity);
        Assert.Equal(3, (await a.Client.WalletAsync<Zone>("north", z => z.RemainingCapacity)).Available);

        // The server refuses it, as it would for a device that had lost its permission.
        a.Fault.ReplacePostStatus = 403;
        Assert.Equal(SyncState.Failed, (await a.Client.SyncAsync()).State);
        a.Fault.ReplacePostStatus = null;

        RefusedOperation refused = Assert.Single(await a.Client.RefusedOperationsAsync());
        await a.Client.DiscardRefusedAsync(refused.OperationId);

        Assert.Equal(5, (await a.Client.WalletAsync<Zone>("north", z => z.RemainingCapacity)).Available);
        Assert.Equal(10, (await a.ZoneAsync()).RemainingCapacity);
        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
        Assert.Equal(0, (await server.LedgerAsync()).Consumed);
    }

    /// <summary>A 5xx may have been applied, so only a clear refusal can be discarded.</summary>
    [Fact]
    public async Task An_operation_that_may_have_been_applied_cannot_be_discarded()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        string operation = await a.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 1));

        a.Fault.ReplacePostStatus = 500;
        Assert.Equal(SyncState.Failed, (await a.Client.SyncAsync()).State);
        a.Fault.ReplacePostStatus = null;

        Assert.Empty(await a.Client.RefusedOperationsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Client.DiscardRefusedAsync(operation));

        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
        Assert.Equal(1, (await server.ZoneAsync()).CheckIns);
    }

    // ---------- Deletion ----------

    [Fact]
    public async Task A_delete_made_offline_is_immediate_here_and_reaches_every_device()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        using Gate b = await Gate.OpenAsync(server, "gate-b");

        a.Fault.Offline = true;
        await a.Client.DeleteAsync<Zone>("north");
        await using (GateDb local = a.Client.CreateContext())
        {
            Assert.False(await local.Zones.AnyAsync(z => z.Id == "north"));
        }

        a.Fault.Offline = false;
        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
        Assert.Equal(SyncState.Synced, (await b.Client.SyncAsync()).State);

        Assert.True((await server.ZoneAsync()).Deleted);
        await using GateDb other = b.Client.CreateContext();
        Assert.False(await other.Zones.AnyAsync(z => z.Id == "north"));

        // An ordinary read of the table no longer lists it.
        using HttpClient http = RawClient(server, null);
        Assert.DoesNotContain("north", await http.GetStringAsync("tables/zone"));
    }

    [Fact]
    public async Task A_delete_wins_over_a_concurrent_edit_in_either_order()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        using Gate b = await Gate.OpenAsync(server, "gate-b");

        await a.Client.DeleteAsync<Zone>("north");
        await b.Client.EditAsync<Zone>("north", e => e.Set(z => z.Notes, "edited while it was being deleted"));
        await b.Client.SyncAsync();
        await a.Client.SyncAsync();
        await b.Client.SyncAsync();

        Assert.True((await server.ZoneAsync()).Deleted);
        await using GateDb other = b.Client.CreateContext();
        Assert.False(await other.Zones.AnyAsync(z => z.Id == "north"));
        Assert.Equal(0, await b.Client.PendingCountAsync());
    }

    // ---------- Retention ----------

    private static Task<int> ServerRowsAsync(TestServer server)
    {
        int count = 0;
        return server.WithAuthorityAsync(async (db, _) => count = await db.Set<global::Leeway.Datasync.Server.LeewayOperationRow>().CountAsync()).ContinueWith(_ => count);
    }

    [Fact]
    public async Task Pruning_removes_only_operations_the_device_knows_were_acknowledged()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");

        for (int i = 0; i < 4; i++)
        {
            await a.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 1));
            await a.Client.SyncAsync();
        }

        int before = await ServerRowsAsync(server);
        int pruned = 0;
        await server.WithAuthorityAsync(async (_, authority) => pruned = await authority.PruneOperationsAsync());

        // The newest operation's own report says everything below it is settled, but it cannot speak for itself.
        Assert.Equal(3, pruned);
        Assert.Equal(before - 3, await ServerRowsAsync(server));
        Assert.Equal(4, (await server.ZoneAsync()).CheckIns);
    }

    /// <summary>The whole risk of retention: a retry after a prune must still not apply twice.</summary>
    [Fact]
    public async Task A_retry_of_an_unacknowledged_operation_is_still_harmless_after_a_prune()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        await a.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 1));
        await a.Client.SyncAsync();

        // Applied by the server, and the device never heard.
        await a.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 1));
        a.Fault.AfterSend = (_, _) => throw new HttpRequestException("response lost");
        await a.Client.PushAsync();
        a.Fault.AfterSend = null;
        Assert.Equal(2, (await server.ZoneAsync()).CheckIns);

        await server.WithAuthorityAsync((_, authority) => authority.PruneOperationsAsync());

        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
        Assert.Equal(2, (await server.ZoneAsync()).CheckIns);
        Assert.Equal(0, await a.Client.PendingCountAsync());
    }

    [Fact]
    public async Task Client_pruning_keeps_recent_outcomes_and_everything_unsettled()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        List<string> done = [];
        for (int i = 0; i < 5; i++)
        {
            done.Add(await a.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 1)));
        }

        await a.Client.SyncAsync();
        string unsent = await a.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 1));

        int removed = await a.Client.PruneAsync(keepLatest: 2);

        Assert.Equal(3, removed);
        Assert.Null(await a.Client.OutcomeOfAsync(done[0]));
        Assert.NotNull(await a.Client.OutcomeOfAsync(done[4]));
        Assert.Equal(1, await a.Client.PendingCountAsync());
        Assert.Equal(6, (await a.ZoneAsync()).CheckIns);

        // And a later add still counts correctly.
        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
        Assert.Equal(6, (await server.ZoneAsync()).CheckIns);
        Assert.NotNull(await a.Client.OutcomeOfAsync(unsent));
    }

    // ---------- Ordering ----------

    [Fact]
    public async Task A_devices_provisional_spends_are_first_come_first_served()
    {
        for (int round = 0; round < 3; round++)
        {
            await using TestServer server = await TestServer.StartAsync(capacity: 2);
            using Gate a = await Gate.OpenAsync(server, "gate-a");

            List<SpendResult> spends = [];
            for (int i = 0; i < 5; i++)
            {
                spends.Add(await a.Client.TrySpendRemainingCapacityAsync("north", 1));
            }

            Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);

            OutcomeStatus[] outcomes = await Task.WhenAll(spends.Select(async s => (await a.Client.OutcomeOfAsync(s.OperationId))!.Status));
            Assert.Equal(
                [OutcomeStatus.ProvisionalAccepted, OutcomeStatus.ProvisionalAccepted, OutcomeStatus.ProvisionalRejected, OutcomeStatus.ProvisionalRejected, OutcomeStatus.ProvisionalRejected],
                outcomes);
        }
    }

    [Fact]
    public async Task Sending_stops_at_the_first_operation_with_no_answer_so_later_ones_cannot_overtake_it()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 1);
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        SpendResult first = await a.Client.TrySpendRemainingCapacityAsync("north", 1);
        SpendResult second = await a.Client.TrySpendRemainingCapacityAsync("north", 1);

        int posts = 0;
        a.Fault.BeforeSend = request => request.Method == HttpMethod.Post && ++posts == 1 ? throw new HttpRequestException("signal lost") : Task.CompletedTask;
        Assert.Equal(SyncState.Offline, (await a.Client.PushAsync()).State);
        Assert.Equal(1, posts);
        a.Fault.BeforeSend = null;

        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
        Assert.Equal(OutcomeStatus.ProvisionalAccepted, (await a.Client.OutcomeOfAsync(first.OperationId))!.Status);
        Assert.Equal(OutcomeStatus.ProvisionalRejected, (await a.Client.OutcomeOfAsync(second.OperationId))!.Status);
    }
}
