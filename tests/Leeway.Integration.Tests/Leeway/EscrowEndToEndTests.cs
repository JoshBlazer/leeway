using System.Net.Http.Json;
using Leeway.Core;
using Leeway.Datasync.Client;
using Microsoft.EntityFrameworkCore;

namespace Leeway.Integration.Tests.Leeway;

/// <summary>M3: escrow end to end, over real HTTP, real Datasync, and real SQLite on both sides.</summary>
public class EscrowEndToEndTests
{
    private static async Task<(Gate A, Gate B)> TwoGatesWithLeasesAsync(TestServer server)
    {
        Gate a = await Gate.OpenAsync(server, "gate-a");
        Gate b = await Gate.OpenAsync(server, "gate-b");
        await a.Client.RequestLeaseAsync<Zone>("north", z => z.RemainingCapacity);
        await b.Client.RequestLeaseAsync<Zone>("north", z => z.RemainingCapacity);
        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
        Assert.Equal(SyncState.Synced, (await b.Client.SyncAsync()).State);
        return (a, b);
    }

    /// <summary>The M3 exit check, first part.</summary>
    [Fact]
    public async Task Two_offline_gates_spend_within_their_leases_reconnect_and_the_invariant_holds()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 25);
        (Gate a, Gate b) = await TwoGatesWithLeasesAsync(server);
        using (a)
        using (b)
        {
            Assert.Equal(10, (await a.Client.WalletAsync<Zone>("north", z => z.RemainingCapacity)).Available);

            a.Fault.Offline = true;
            b.Fault.Offline = true;
            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(SpendStatus.Confirmed, (await a.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 1)).Status);
                Assert.Equal(SpendStatus.Confirmed, (await b.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 1)).Status);
            }

            Assert.Equal(15, (await a.ZoneAsync()).RemainingCapacity); // 25, less its own 10.
            Assert.Equal(0, (await a.Client.WalletAsync<Zone>("north", z => z.RemainingCapacity)).Available);
            Assert.Equal(25, (await server.ZoneAsync()).RemainingCapacity); // The server has heard nothing yet.

            a.Fault.Offline = false;
            b.Fault.Offline = false;
            Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
            Assert.Equal(SyncState.Synced, (await b.Client.SyncAsync()).State);
            Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);

            EscrowLedger ledger = await server.LedgerAsync();
            Assert.Equal(20, ledger.Consumed);
            Assert.True(ledger.IsConserved);
            Assert.Equal(0, ledger.Accounts.Values.Sum(x => x.Overdraft));
            Assert.Equal(5, (await server.ZoneAsync()).RemainingCapacity);
            Assert.Equal(5, (await a.ZoneAsync()).RemainingCapacity);
            Assert.Equal(5, (await b.ZoneAsync()).RemainingCapacity);
        }
    }

    /// <summary>The M3 exit check, second part: accepted while the pool has quantity.</summary>
    [Fact]
    public async Task Overspending_offline_becomes_provisional_and_is_accepted_while_the_pool_can_fund_it()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 25);
        (Gate a, Gate b) = await TwoGatesWithLeasesAsync(server);
        using (a)
        using (b)
        {
            a.Fault.Offline = true;
            for (int i = 0; i < 10; i++)
            {
                await a.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 1);
            }

            SpendResult over = await a.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 3);
            Assert.Equal(SpendStatus.Provisional, over.Status);
            Assert.Null(await a.Client.OutcomeOfAsync(over.OperationId));

            // Provisional spends do not reduce what the gate shows as certain.
            Assert.Equal(15, (await a.ZoneAsync()).RemainingCapacity);

            a.Fault.Offline = false;
            await a.Client.SyncAsync();

            OperationOutcome? outcome = await a.Client.OutcomeOfAsync(over.OperationId);
            Assert.Equal(OutcomeStatus.ProvisionalAccepted, outcome!.Status);
            EscrowLedger ledger = await server.LedgerAsync();
            Assert.Equal(13, ledger.Consumed);
            Assert.Equal(2, ledger.Unleased);
            Assert.True(ledger.IsConserved);
            Assert.Equal(12, (await a.ZoneAsync()).RemainingCapacity);
        }
    }

    /// <summary>The M3 exit check, second part: rejected when it cannot be funded, with nothing changed.</summary>
    [Fact]
    public async Task Overspending_offline_is_rejected_when_the_pool_cannot_fund_it_and_the_rule_still_holds()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 20);
        (Gate a, Gate b) = await TwoGatesWithLeasesAsync(server);
        using (a)
        using (b)
        {
            a.Fault.Offline = true;
            for (int i = 0; i < 10; i++)
            {
                await a.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 1);
            }

            SpendResult over = await a.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 1);
            Assert.Equal(SpendStatus.Provisional, over.Status);

            a.Fault.Offline = false;
            await a.Client.SyncAsync();

            Assert.Equal(OutcomeStatus.ProvisionalRejected, (await a.Client.OutcomeOfAsync(over.OperationId))!.Status);
            EscrowLedger ledger = await server.LedgerAsync();
            Assert.Equal(10, ledger.Consumed);
            Assert.Equal(10, ledger.Remaining);
            Assert.True(ledger.IsConserved);

            // B's lease is untouched, and B can still spend all of it.
            Assert.Equal(10, (await b.Client.WalletAsync<Zone>("north", z => z.RemainingCapacity)).Available);
            Assert.Equal(10, (await a.ZoneAsync()).RemainingCapacity);
        }
    }

    [Fact]
    public async Task A_lost_response_does_not_apply_a_spend_twice_and_recovers_the_original_outcome()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 10);
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        await a.Client.RequestLeaseAsync<Zone>("north", z => z.RemainingCapacity, 5);
        await a.Client.SyncAsync();

        SpendResult within = await a.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 2);
        SpendResult over = await a.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 4);

        a.Fault.AfterSend = (_, _) => throw new HttpRequestException("response lost");
        Assert.Equal(SyncState.Offline, (await a.Client.SyncAsync()).State);
        a.Fault.AfterSend = null;

        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);

        EscrowLedger ledger = await server.LedgerAsync();
        Assert.Equal(2, ledger.Accounts["gate-a"].Spent);
        Assert.Equal(4, ledger.Accounts["gate-a"].ProvisionalAccepted);
        Assert.True(ledger.IsConserved);
        Assert.Equal(OutcomeStatus.SpendConfirmed, (await a.Client.OutcomeOfAsync(within.OperationId))!.Status);
        Assert.Equal(OutcomeStatus.ProvisionalAccepted, (await a.Client.OutcomeOfAsync(over.OperationId))!.Status);
    }

    /// <summary>Probes C24 and C25: on a poor signal, a push can report success having done nothing, or having done everything.</summary>
    [Fact]
    public async Task A_timeout_is_reported_as_offline_and_loses_nothing_whether_or_not_the_server_applied_it()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 10);
        using Gate a = await Gate.OpenAsync(server, "gate-a", timeout: TimeSpan.FromSeconds(3));
        await a.Client.RequestLeaseAsync<Zone>("north", z => z.RemainingCapacity, 5);
        await a.Client.SyncAsync();

        // Never reaches the server.
        await a.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 1);
        a.Fault.Hang = true;
        Assert.Equal(SyncState.Offline, (await a.Client.SyncAsync()).State);
        a.Fault.Hang = false;
        Assert.Equal(0, (await server.LedgerAsync()).Consumed);

        // Applied by the server, but the client gives up waiting.
        a.Fault.AfterSend = async (_, _) => await Task.Delay(TimeSpan.FromSeconds(8));
        Assert.Equal(SyncState.Offline, (await a.Client.SyncAsync()).State);
        a.Fault.AfterSend = null;
        Assert.Equal(1, (await server.LedgerAsync()).Consumed);
        Assert.Equal(1, await a.Client.PendingCountAsync());

        // The stuck self-conflict from C25 cannot happen here.
        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
        Assert.Equal(0, await a.Client.PendingCountAsync());
        Assert.Equal(1, (await server.LedgerAsync()).Consumed);
    }

    /// <summary>
    /// Probe C24 on its own. Pulls still work here, so only the queue check can show that the push did nothing.
    /// A sabotage run proved the test above could not: its fault also failed the pull.
    /// </summary>
    [Fact]
    public async Task A_push_that_times_out_is_reported_as_offline_even_when_pulls_work()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 10);
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        await a.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 1));

        a.Fault.HangPosts = true;
        Assert.Equal(SyncState.Offline, (await a.Client.SyncAsync()).State);
        Assert.Equal(1, await a.Client.PendingCountAsync());

        a.Fault.HangPosts = false;
        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
        Assert.Equal(1, (await server.ZoneAsync()).CheckIns);
    }

    /// <summary>
    /// Upstream fixed #578 by making a timed-out push throw TimeoutException, in a release after the pinned 10.1.2.
    /// Sync must report that as offline, not crash, so upgrading cannot break this contract.
    /// </summary>
    [Fact]
    public async Task A_push_that_throws_a_timeout_exception_is_reported_as_offline()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 10);
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        await a.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 1));

        a.Fault.BeforeSend = request => request.Method == HttpMethod.Post ? throw new TimeoutException("as in the upstream fix") : Task.CompletedTask;
        Assert.Equal(SyncState.Offline, (await a.Client.SyncAsync()).State);
        a.Fault.BeforeSend = null;

        Assert.Equal(1, await a.Client.PendingCountAsync());
        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
        Assert.Equal(1, (await server.ZoneAsync()).CheckIns);
    }

    /// <summary>An acknowledged spend must stay in the view until a pull includes it, or the next spend would forget it.</summary>
    [Fact]
    public async Task The_remaining_figure_keeps_an_acknowledged_spend_until_a_pull_includes_it()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 10);
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        await a.Client.RequestLeaseAsync<Zone>("north", z => z.RemainingCapacity, 5);
        await a.Client.SyncAsync();

        await a.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 1);
        Assert.Equal(SyncState.Synced, (await a.Client.PushAsync()).State);
        Assert.Equal(0, await a.Client.PendingCountAsync());

        await a.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 1);
        Assert.Equal(8, (await a.ZoneAsync()).RemainingCapacity);

        await a.Client.SyncAsync();
        Assert.Equal(8, (await a.ZoneAsync()).RemainingCapacity);
        Assert.Equal(8, (await server.ZoneAsync()).RemainingCapacity);
    }

    /// <summary>Without a resolver, a replay would be a stuck 409 (probe C15). This must end, and it must recover the outcome.</summary>
    [Fact]
    public async Task A_replayed_operation_after_a_lost_response_is_not_left_stuck_in_the_queue()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 10);
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        string operation = await a.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 1));

        a.Fault.AfterSend = (_, _) => throw new HttpRequestException("response lost");
        Assert.Equal(SyncState.Offline, (await a.Client.PushAsync()).State);
        a.Fault.AfterSend = null;

        Assert.Equal(SyncState.Synced, (await a.Client.PushAsync()).State);
        await using GateDb db = a.Client.CreateContext();
        Assert.Empty(await db.DatasyncOperationsQueue.ToListAsync());
        Assert.Equal(global::Leeway.Core.OutcomeStatus.Merged, (await a.Client.OutcomeOfAsync(operation))!.Status);
    }

    /// <summary>Probe C23: a spend must not wait for a push that is stuck on the network.</summary>
    [Fact]
    public async Task A_spend_completes_while_a_push_is_stuck_on_the_network()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 10);
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        await a.Client.RequestLeaseAsync<Zone>("north", z => z.RemainingCapacity, 5);
        await a.Client.SyncAsync();
        await a.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 1);

        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Fault.BeforeSend = async _ =>
        {
            arrived.TrySetResult();
            await release.Task;
        };

        Task<SyncReport> sync = a.Client.SyncAsync();
        await arrived.Task;

        SpendResult spend = await a.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 1).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SpendStatus.Confirmed, spend.Status);

        a.Fault.BeforeSend = null;
        release.SetResult();
        await sync;
        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
        Assert.Equal(2, (await server.LedgerAsync()).Accounts["gate-a"].Spent);
    }

    [Fact]
    public async Task Concurrent_spends_can_never_overdraw_a_lease()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 100);
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        await a.Client.RequestLeaseAsync<Zone>("north", z => z.RemainingCapacity, 10);
        await a.Client.SyncAsync();

        a.Fault.Offline = true;
        SpendResult[] results = await Task.WhenAll(Enumerable.Range(0, 30)
            .Select(_ => Task.Run(() => a.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 1))));

        Assert.Equal(10, results.Count(r => r.Status == SpendStatus.Confirmed));
        Assert.Equal(20, results.Count(r => r.Status == SpendStatus.Provisional));
        Assert.Equal(0, (await a.Client.WalletAsync<Zone>("north", z => z.RemainingCapacity)).Available);
    }

    [Fact]
    public async Task The_wallet_and_sequence_survive_an_app_restart()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 10);
        using Gate before = await Gate.OpenAsync(server, "gate-a");
        await before.Client.RequestLeaseAsync<Zone>("north", z => z.RemainingCapacity, 5);
        await before.Client.SyncAsync();
        before.Fault.Offline = true;
        SpendResult first = await before.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 3);

        // A new client on the same file, as after a restart.
        using Gate after = new(server, "gate-a", existingPath: before.Path_);
        await after.Client.InitializeAsync();

        Assert.Equal(2, (await after.Client.WalletAsync<Zone>("north", z => z.RemainingCapacity)).Available);
        SpendResult second = await after.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 2);
        Assert.Equal(SpendStatus.Confirmed, second.Status);
        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(SpendStatus.Provisional, (await after.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 1)).Status);

        Assert.Equal(SyncState.Synced, (await after.Client.SyncAsync()).State);
        Assert.Equal(5, (await server.LedgerAsync()).Accounts["gate-a"].Spent);
    }

    [Fact]
    public async Task A_returned_lease_can_be_granted_to_another_gate()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 10);
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        using Gate b = await Gate.OpenAsync(server, "gate-b");
        await a.Client.RequestLeaseAsync<Zone>("north", z => z.RemainingCapacity);
        await a.Client.SyncAsync();

        await b.Client.RequestLeaseAsync<Zone>("north", z => z.RemainingCapacity);
        await b.Client.SyncAsync();
        Assert.Equal(0, (await b.Client.WalletAsync<Zone>("north", z => z.RemainingCapacity)).Available);

        Assert.Equal(6, await a.Client.ReturnLeaseAsync<Zone>("north", z => z.RemainingCapacity, 6));
        Assert.Equal(4, (await a.Client.WalletAsync<Zone>("north", z => z.RemainingCapacity)).Available);
        await a.Client.SyncAsync();

        await b.Client.RequestLeaseAsync<Zone>("north", z => z.RemainingCapacity);
        await b.Client.SyncAsync();
        Assert.Equal(6, (await b.Client.WalletAsync<Zone>("north", z => z.RemainingCapacity)).Available);
        Assert.True((await server.LedgerAsync()).IsConserved);
    }

    /// <summary>ADR-0005: a write-off can break the rule, and when it does the server says so.</summary>
    [Fact]
    public async Task A_spend_from_a_written_off_gate_is_reported_as_an_overdraft()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 10);
        using Gate lost = await Gate.OpenAsync(server, "lost");
        await lost.Client.RequestLeaseAsync<Zone>("north", z => z.RemainingCapacity);
        await lost.Client.SyncAsync();

        lost.Fault.Offline = true;
        SpendResult spend = await lost.Client.TrySpendAsync<Zone>("north", z => z.RemainingCapacity, 4);
        Assert.Equal(SpendStatus.Confirmed, spend.Status);

        await server.WithAuthorityAsync((_, authority) => authority.WriteOffAsync(MergeSchema.EscrowResource("Zone", "north", nameof(ServerZone.RemainingCapacity)), "lost"));

        lost.Fault.Offline = false;
        await lost.Client.SyncAsync();

        Assert.Equal(OutcomeStatus.Overdraft, (await lost.Client.OutcomeOfAsync(spend.OperationId))!.Status);
        EscrowLedger ledger = await server.LedgerAsync();
        Assert.Equal(4, ledger.Accounts["lost"].Overdraft);
        Assert.True(ledger.IsConserved);
    }

    [Fact]
    public async Task A_spend_on_an_unknown_resource_fails_visibly_and_does_not_block_later_work()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");

        a.Fault.Offline = true;
        await a.Client.RequestLeaseAsync<Zone>("no-such-zone", z => z.RemainingCapacity);
        a.Fault.Offline = false;

        SyncReport report = await a.Client.SyncAsync();
        Assert.Equal(SyncState.Failed, report.State);
        Assert.Equal(1, report.RejectedOperations);

        // Pulls of entities are not blocked, unlike Datasync's default (C06).
        await server.WithAuthorityAsync((_, authority) => authority.CreateEntityAsync(new ServerZone { Id = "south", RemainingCapacity = 3 }));
        await a.Client.SyncAsync();
        Assert.Equal(3, (await a.ZoneAsync("south")).RemainingCapacity);
    }

    [Fact]
    public async Task Clients_cannot_write_projected_rows_or_rewrite_recorded_operations()
    {
        await using TestServer server = await TestServer.StartAsync();
        using HttpClient http = new() { BaseAddress = server.BaseAddress };

        using HttpResponseMessage putZone = await http.PutAsync("tables/zone/north", JsonContent.Create(new { id = "north", remainingCapacity = 9999 }));
        Assert.Equal(401, (int)putZone.StatusCode);
        using HttpResponseMessage deleteZone = await http.DeleteAsync("tables/zone/north");
        Assert.Equal(401, (int)deleteZone.StatusCode);
        using HttpResponseMessage postZone = await http.PostAsync("tables/zone", JsonContent.Create(new { id = "fake" }));
        Assert.Equal(401, (int)postZone.StatusCode);
        Assert.Equal(25, (await server.ZoneAsync()).RemainingCapacity);
    }
}
