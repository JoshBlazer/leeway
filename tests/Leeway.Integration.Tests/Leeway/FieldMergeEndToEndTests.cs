using System.Text.Json;
using CommunityToolkit.Datasync.Client.Offline;
using Leeway.Datasync.Client;
using Microsoft.EntityFrameworkCore;

namespace Leeway.Integration.Tests.Leeway;

/// <summary>M4: per-field merging wired into Datasync. Compare with C05 and C06, the whole-row baseline on the same transport.</summary>
public class FieldMergeEndToEndTests
{
    private static HashSet<string> Flags(string? json) => json is null ? [] : JsonSerializer.Deserialize<HashSet<string>>(json)!;

    private static async Task SyncAllAsync(params Gate[] gates)
    {
        foreach (Gate gate in gates.Concat(gates))
        {
            Assert.Equal(SyncState.Synced, (await gate.Client.SyncAsync()).State);
        }
    }

    /// <summary>The M4 exit check.</summary>
    [Fact]
    public async Task Concurrent_offline_edits_to_one_row_merge_per_field_instead_of_whole_row_wins()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        using Gate b = await Gate.OpenAsync(server, "gate-b");

        a.Fault.Offline = true;
        b.Fault.Offline = true;
        await a.Client.EditAsync<Zone>("north", e => e.Set(z => z.Notes, "Turnstile 3 jammed").Add(z => z.CheckIns, 5).AddToSet(z => z.FlagsJson, "vip"));
        await b.Client.EditAsync<Zone>("north", e => e.Set(z => z.Steward, "Ada").Add(z => z.CheckIns, 7).AddToSet(z => z.FlagsJson, "medical"));
        a.Fault.Offline = false;
        b.Fault.Offline = false;

        await SyncAllAsync(a, b);

        foreach (Zone zone in new[] { await a.ZoneAsync(), await b.ZoneAsync() })
        {
            Assert.Equal("Turnstile 3 jammed", zone.Notes);
            Assert.Equal("Ada", zone.Steward);
            Assert.Equal(12, zone.CheckIns);
            Assert.Equal(["medical", "vip"], Flags(zone.FlagsJson).Order());
        }

        ServerZone stored = await server.ZoneAsync();
        Assert.Equal("Turnstile 3 jammed", stored.Notes);
        Assert.Equal("Ada", stored.Steward);
        Assert.Equal(12, stored.CheckIns);
        Assert.Equal(["medical", "vip"], Flags(stored.FlagsJson).Order());
    }

    [Fact]
    public async Task There_is_no_stuck_conflict_and_no_failed_queue_row_where_the_default_would_have_one()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        using Gate b = await Gate.OpenAsync(server, "gate-b");

        await a.Client.EditAsync<Zone>("north", e => e.Set(z => z.Notes, "a"));
        await b.Client.EditAsync<Zone>("north", e => e.Set(z => z.Notes, "b"));

        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
        Assert.Equal(SyncState.Synced, (await b.Client.SyncAsync()).State);
        Assert.Equal(0, await b.Client.PendingCountAsync());

        await using GateDb db = b.Client.CreateContext();
        Assert.Empty(await db.DatasyncOperationsQueue.ToListAsync());
    }

    [Fact]
    public async Task A_value_written_offline_shows_locally_at_once_and_survives_a_pull_before_it_is_pushed()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        using Gate b = await Gate.OpenAsync(server, "gate-b");

        // A's push is down, but its pull still works: only POSTs to the op table fail.
        a.Fault.BeforeSend = request => request.Method == HttpMethod.Post ? throw new HttpRequestException("push lost") : Task.CompletedTask;
        await a.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 3).Set(z => z.Notes, "local"));
        Assert.Equal(3, (await a.ZoneAsync()).CheckIns);
        Assert.Equal("local", (await a.ZoneAsync()).Notes);

        await b.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 10).Set(z => z.Steward, "Ada"));
        await b.Client.SyncAsync();

        await a.Client.PullAsync();

        Zone zone = await a.ZoneAsync();
        Assert.Equal(13, zone.CheckIns);
        Assert.Equal("Ada", zone.Steward);
        Assert.Equal("local", zone.Notes);

        a.Fault.BeforeSend = null;
        await SyncAllAsync(a, b);
        Assert.Equal(13, (await server.ZoneAsync()).CheckIns);
        Assert.Equal("local", (await server.ZoneAsync()).Notes);
    }

    /// <summary>The bug the simulator found: an acknowledged add missing from the view makes the next add lose an increment.</summary>
    [Fact]
    public async Task An_add_after_a_push_and_before_a_pull_does_not_lose_an_increment()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");

        await a.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 5));
        await a.Client.PushAsync();
        Assert.Equal(5, (await a.ZoneAsync()).CheckIns);

        await a.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 2));
        Assert.Equal(7, (await a.ZoneAsync()).CheckIns);

        await SyncAllAsync(a);
        Assert.Equal(7, (await server.ZoneAsync()).CheckIns);
        Assert.Equal(7, (await a.ZoneAsync()).CheckIns);
    }

    [Fact]
    public async Task A_concurrent_add_beats_a_remove_and_a_remove_that_saw_the_add_wins()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        using Gate b = await Gate.OpenAsync(server, "gate-b");

        await a.Client.EditAsync<Zone>("north", e => e.AddToSet(z => z.FlagsJson, "vip"));
        await SyncAllAsync(a, b);
        Assert.Contains("vip", Flags((await b.ZoneAsync()).FlagsJson));

        // B removes what it saw, while A concurrently adds it again.
        await b.Client.EditAsync<Zone>("north", e => e.RemoveFromSet(z => z.FlagsJson, "vip"));
        await a.Client.EditAsync<Zone>("north", e => e.AddToSet(z => z.FlagsJson, "vip"));
        Assert.Empty(Flags((await b.ZoneAsync()).FlagsJson));
        await SyncAllAsync(a, b);
        Assert.Contains("vip", Flags((await server.ZoneAsync()).FlagsJson));

        // Now everyone has seen it, so a remove sticks.
        await b.Client.EditAsync<Zone>("north", e => e.RemoveFromSet(z => z.FlagsJson, "vip"));
        await SyncAllAsync(a, b);
        Assert.Empty(Flags((await server.ZoneAsync()).FlagsJson));
        Assert.Empty(Flags((await a.ZoneAsync()).FlagsJson));
    }

    [Fact]
    public async Task A_device_with_a_slow_clock_still_beats_a_value_it_has_seen()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate fine = await Gate.OpenAsync(server, "fine");
        using Gate slow = await Gate.OpenAsync(server, "slow");
        slow.Clock.Ms -= 86_400_000;

        await fine.Client.EditAsync<Zone>("north", e => e.Set(z => z.Notes, "first"));
        await SyncAllAsync(fine, slow);

        await slow.Client.EditAsync<Zone>("north", e => e.Set(z => z.Notes, "second, having seen first"));
        await SyncAllAsync(slow, fine);

        Assert.Equal("second, having seen first", (await server.ZoneAsync()).Notes);
        Assert.Equal("second, having seen first", (await fine.ZoneAsync()).Notes);
    }

    /// <summary>
    /// A value clamped at the drift limit is ahead of the server's clock. A write that saw it must still win, and only the
    /// operation's Observed stamp can ensure that, because the writer's own stamp is clamped to the same limit.
    /// </summary>
    [Fact]
    public async Task A_write_that_saw_a_value_at_the_drift_limit_still_beats_it()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate fast = await Gate.OpenAsync(server, "zzz-fast");
        using Gate other = await Gate.OpenAsync(server, "aaa-other");
        fast.Clock.Ms += 86_400_000;

        // Several operations, so that the fast device's sequence is higher and would win a tie at the limit.
        for (int i = 0; i < 5; i++)
        {
            await fast.Client.EditAsync<Zone>("north", e => e.Set(z => z.Notes, "from the future"));
        }

        await fast.Client.SyncAsync();
        await other.Client.SyncAsync();
        Assert.Equal("from the future", (await other.ZoneAsync()).Notes);

        await other.Client.EditAsync<Zone>("north", e => e.Set(z => z.Notes, "having seen it"));
        await other.Client.SyncAsync();

        Assert.Equal("having seen it", (await server.ZoneAsync()).Notes);
    }

    [Fact]
    public async Task A_device_with_a_fast_clock_cannot_win_every_merge()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate fast = await Gate.OpenAsync(server, "fast");
        using Gate fine = await Gate.OpenAsync(server, "fine");
        fast.Clock.Ms += 86_400_000;

        await fast.Client.EditAsync<Zone>("north", e => e.Set(z => z.Notes, "from the future"));
        await fast.Client.SyncAsync();
        Assert.Equal("from the future", (await server.ZoneAsync()).Notes);

        // Beyond the drift limit, an honest write that never saw it still wins. A raw wall clock would lose for a day.
        server.Clock.Ms += 400_000;
        fine.Clock.Ms += 400_000;
        fine.Fault.BeforeSend = request => request.Method == HttpMethod.Get ? throw new HttpRequestException("no pull") : Task.CompletedTask;
        await fine.Client.EditAsync<Zone>("north", e => e.Set(z => z.Notes, "honest"));
        await fine.Client.PushAsync();

        Assert.Equal("honest", (await server.ZoneAsync()).Notes);
    }

    /// <summary>ADR-0013 and probe C19: with the server's clock frozen, every update lands in one millisecond.</summary>
    [Fact]
    public async Task Rows_updated_in_the_same_millisecond_are_all_pulled()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        using Gate b = await Gate.OpenAsync(server, "gate-b");

        for (int i = 1; i <= 5; i++)
        {
            await a.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 1));
            await a.Client.SyncAsync();
            await b.Client.SyncAsync();
            Assert.Equal(i, (await b.ZoneAsync()).CheckIns);
        }

        ServerZone stored = await server.ZoneAsync();
        Assert.True(stored.UpdatedAt!.Value.ToUnixTimeMilliseconds() > server.Clock.Ms);
    }

    [Fact]
    public async Task Simultaneous_pushes_from_many_devices_lose_nothing()
    {
        await using TestServer server = await TestServer.StartAsync();
        List<Gate> gates = [];
        for (int i = 0; i < 4; i++)
        {
            gates.Add(await Gate.OpenAsync(server, $"gate-{i}"));
        }

        foreach (Gate gate in gates)
        {
            for (int n = 0; n < 5; n++)
            {
                await gate.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 1).AddToSet(z => z.FlagsJson, $"{gate.Name}-{n}"));
            }
        }

        await Task.WhenAll(gates.Select(g => Task.Run(() => g.Client.SyncAsync())));
        await SyncAllAsync([.. gates]);

        ServerZone stored = await server.ZoneAsync();
        Assert.Equal(20, stored.CheckIns);
        Assert.Equal(20, Flags(stored.FlagsJson).Count);
        foreach (Gate gate in gates)
        {
            Assert.Equal(20, (await gate.ZoneAsync()).CheckIns);
            gate.Dispose();
        }
    }

    /// <summary>
    /// Datasync's process-wide lock has a race when contexts overlap (see the integration map), so clients must not overlap.
    /// </summary>
    [Fact]
    public async Task Clients_in_one_process_sync_one_at_a_time()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        using Gate b = await Gate.OpenAsync(server, "gate-b");
        await a.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 1));
        await b.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 1));

        TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Fault.BeforeSend = async _ =>
        {
            arrived.TrySetResult();
            await release.Task;
        };
        b.Fault.Requests.Clear();

        Task<SyncReport> syncA = a.Client.SyncAsync();
        await arrived.Task;
        Task<SyncReport> syncB = b.Client.SyncAsync();
        await Task.Delay(500);
        Assert.Empty(b.Fault.Requests);
        Assert.False(syncB.IsCompleted);

        // Local writes do not wait, even while both clients' syncs are held up.
        await b.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 1)).WaitAsync(TimeSpan.FromSeconds(5));

        a.Fault.BeforeSend = null;
        release.SetResult();
        Assert.Equal(SyncState.Synced, (await syncA).State);
        Assert.Equal(SyncState.Synced, (await syncB).State);
    }

    [Fact]
    public async Task A_connection_drop_mid_batch_loses_nothing_and_applies_nothing_twice()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        for (int i = 0; i < 6; i++)
        {
            await a.Client.EditAsync<Zone>("north", e => e.Add(z => z.CheckIns, 1));
        }

        int posts = 0;
        a.Fault.BeforeSend = request => request.Method == HttpMethod.Post && ++posts == 4 ? throw new HttpRequestException("signal lost") : Task.CompletedTask;
        Assert.Equal(SyncState.Offline, (await a.Client.SyncAsync()).State);
        a.Fault.BeforeSend = null;

        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
        Assert.Equal(6, (await server.ZoneAsync()).CheckIns);
        Assert.Equal(6, (await a.ZoneAsync()).CheckIns);
        Assert.Equal(0, await a.Client.PendingCountAsync());
    }

    [Fact]
    public async Task An_edit_can_create_an_entity_the_server_has_not_seen()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        using Gate b = await Gate.OpenAsync(server, "gate-b");

        await a.Client.EditAsync<Zone>("new-zone", e => e.Set(z => z.Notes, "created offline").Add(z => z.CheckIns, 1));
        Assert.Equal("created offline", (await a.ZoneAsync("new-zone")).Notes);

        await SyncAllAsync(a, b);
        Assert.Equal("created offline", (await b.ZoneAsync("new-zone")).Notes);
        Assert.Equal(1, (await server.ZoneAsync("new-zone")).CheckIns);
    }

    /// <summary>An ordinary EF edit would queue a whole-row write the server refuses, and block pulls (C06), so it must fail loudly.</summary>
    [Fact]
    public async Task Editing_a_merged_entity_directly_through_ef_is_refused()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");

        await using GateDb db = a.Client.CreateContext();
        Zone zone = await db.Zones.SingleAsync(z => z.Id == "north");
        zone.Notes = "bypassing Leeway";

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("EditAsync", refused.Message);
    }

    [Fact]
    public async Task Editing_a_field_with_the_wrong_kind_of_change_is_refused()
    {
        await using TestServer server = await TestServer.StartAsync();
        using Gate a = await Gate.OpenAsync(server, "gate-a");

        await Assert.ThrowsAsync<ArgumentException>(() => a.Client.EditAsync<Zone>("north", e => e.Set(z => z.CheckIns, 3)));
        await Assert.ThrowsAsync<ArgumentException>(() => a.Client.EditAsync<Zone>("north", e => e.Add(z => z.Notes, 3)));
        await Assert.ThrowsAsync<ArgumentException>(() => a.Client.EditAsync<Zone>("north", e => e.Set(z => z.RemainingCapacity, 3)));
        Assert.Equal(0, await a.Client.PendingCountAsync());
    }
}
