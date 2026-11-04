using System.Net.Http.Json;
using System.Text.Json;
using CommunityToolkit.Datasync.Client;
using CommunityToolkit.Datasync.Client.Offline;
using CommunityToolkit.Datasync.Client.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Leeway.Integration.Tests.Datasync;

/// <summary>
/// Characterization tests pinning the behaviour of Datasync 10.1.2 that Leeway's design relies on.
/// Each test ID is cited by docs/design/datasync-integration.md. If an upgrade breaks one of these,
/// a design assumption has changed: re-read the map rather than just fixing the test.
/// </summary>
public class ClientProbes
{
    private static async Task PullFreshAsync(ProbeClient client)
    {
        PullResult pull = await client.NewContext().PullAsync([typeof(ClientItem)], new PullOptions());
        Assert.True(pull.IsSuccessful);
    }

    [Fact]
    public async Task C01_a_local_write_needs_no_network_and_queues_one_operation()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1", a: "server");
        await using ProbeClient client = new(server);
        await PullFreshAsync(client);

        client.Fault.Offline = true;
        await client.EditAsync("i1", x => x.A = "offline");

        DatasyncOperation op = await client.NewContext().DatasyncOperationsQueue.SingleAsync();
        Assert.Equal(OperationKind.Replace, op.Kind);
        Assert.Equal(OperationState.Pending, op.State);
        Assert.Equal("offline", (await client.ReadAsync("i1")).A);
    }

    [Fact]
    public async Task C02_pushing_with_no_network_throws_rather_than_reporting_failure_and_keeps_the_queue()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        await using ProbeClient client = new(server);
        await PullFreshAsync(client);
        await client.EditAsync("i1", x => x.A = "offline");

        client.Fault.Offline = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.NewContext().PushAsync());

        DatasyncOperation op = await client.NewContext().DatasyncOperationsQueue.SingleAsync();
        Assert.Equal(OperationState.Pending, op.State);
    }

    [Fact]
    public async Task C03_pull_with_no_network_reports_a_local_exception_instead_of_throwing()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await using ProbeClient client = new(server);

        client.Fault.Offline = true;
        PullResult pull = await client.NewContext().PullAsync([typeof(ClientItem)], new PullOptions());

        Assert.False(pull.IsSuccessful);
        Assert.Empty(pull.FailedRequests);
        Assert.IsType<HttpRequestException>(Assert.Single(pull.LocalExceptions).Value);
    }

    [Fact]
    public async Task C04_edit_offline_reconnect_push_and_another_device_pulls_the_change()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1", a: "original");
        await using ProbeClient device1 = new(server);
        await using ProbeClient device2 = new(server);
        await PullFreshAsync(device1);
        await PullFreshAsync(device2);

        device1.Fault.Offline = true;
        await device1.EditAsync("i1", x => x.A = "edited offline");
        device1.Fault.Offline = false;

        PushResult push = await device1.NewContext().PushAsync();
        Assert.True(push.IsSuccessful);
        Assert.Equal(1, push.CompletedOperations);
        Assert.Empty(await device1.NewContext().DatasyncOperationsQueue.ToListAsync());

        await PullFreshAsync(device2);
        Assert.Equal("edited offline", (await device2.ReadAsync("i1")).A);
    }

    [Fact]
    public async Task C05_default_conflict_marks_the_operation_failed_with_412_and_keeps_the_local_value()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1", a: "original");
        await using ProbeClient device1 = new(server);
        await using ProbeClient device2 = new(server);
        await PullFreshAsync(device1);
        await PullFreshAsync(device2);

        await device1.EditAsync("i1", x => x.A = "one");
        await device2.EditAsync("i1", x => x.A = "two");
        Assert.True((await device1.NewContext().PushAsync()).IsSuccessful);

        PushResult push = await device2.NewContext().PushAsync();

        Assert.False(push.IsSuccessful);
        Assert.Equal(412, Assert.Single(push.FailedRequests).Value.StatusCode);
        DatasyncOperation op = await device2.NewContext().DatasyncOperationsQueue.SingleAsync();
        Assert.Equal(OperationState.Failed, op.State);
        Assert.Equal(412, op.HttpStatusCode);
        Assert.Equal("two", (await device2.ReadAsync("i1")).A);
    }

    [Fact]
    public async Task C06_a_failed_operation_blocks_every_pull_of_that_entity_type()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        await server.SeedAsync("i2");
        await using ProbeClient device1 = new(server);
        await using ProbeClient device2 = new(server);
        await PullFreshAsync(device1);
        await PullFreshAsync(device2);
        await device1.EditAsync("i1", x => x.A = "one");
        await device2.EditAsync("i1", x => x.A = "two");
        await device1.NewContext().PushAsync();
        await device2.NewContext().PushAsync();

        DatasyncException ex = await Assert.ThrowsAsync<DatasyncException>(() => device2.NewContext().PullAsync([typeof(ClientItem)], new PullOptions()));
        Assert.Contains("pending operations", ex.Message);

        // The block covers the whole type, so i2 cannot be refreshed either. Other entity types still pull.
        PullResult otherType = await device2.NewContext().PullAsync([typeof(ClientOp)], new PullOptions());
        Assert.True(otherType.IsSuccessful);
    }

    [Fact]
    public async Task C07_several_edits_to_a_row_are_compacted_to_one_whole_row_operation()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        await using ProbeClient client = new(server);
        await PullFreshAsync(client);

        await client.EditAsync("i1", x => x.A = "first");
        await client.EditAsync("i1", x => x.B = "second");
        await client.EditAsync("i1", x => x.A = "third");

        DatasyncOperation op = await client.NewContext().DatasyncOperationsQueue.SingleAsync();
        Assert.Equal(2, op.Version);
        ClientItem body = JsonSerializer.Deserialize<ClientItem>(op.Item, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("third", body.A);
        Assert.Equal("second", body.B);

        // Nothing in the queue records that there were three edits, or which fields changed.
        Assert.DoesNotContain("first", op.Item);
    }

    [Fact]
    public async Task C08_a_resolver_receives_client_and_server_rows_and_a_merged_result_is_written_to_the_server()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1", a: "a0", b: "b0");
        await using ProbeClient device1 = new(server);
        await using ProbeClient device2 = new(server);
        await PullFreshAsync(device1);
        await PullFreshAsync(device2);

        RecordingMergeResolver resolver = new((client, serverRow) => new ClientItem
        {
            Id = client.Id,
            A = serverRow.A, // Field from the server.
            B = client.B,    // Field from the client.
        });
        device2.Config.ItemResolver = resolver;

        await device1.EditAsync("i1", x => x.A = "a1");
        await device2.EditAsync("i1", x => x.B = "b2");
        Assert.True((await device1.NewContext().PushAsync()).IsSuccessful);
        PushResult push = await device2.NewContext().PushAsync();

        Assert.True(push.IsSuccessful);
        Assert.Equal(1, resolver.Calls);
        Assert.Equal("a0", resolver.SeenClient!.A);
        Assert.Equal("b2", resolver.SeenClient.B);
        Assert.Equal("a1", resolver.SeenServer!.A);
        Assert.Equal("b0", resolver.SeenServer.B);

        ServerItem stored = await server.WithDbAsync(db => db.Items.AsNoTracking().SingleAsync(x => x.Id == "i1"));
        Assert.Equal("a1", stored.A);
        Assert.Equal("b2", stored.B);

        // The merged result also replaces the local row, in place, with the server's new version.
        ClientItem local = await device2.ReadAsync("i1");
        Assert.Equal("a1", local.A);
        Assert.Equal("b2", local.B);
        Assert.Equal(Convert.ToBase64String(stored.Version), local.Version);
        Assert.Empty(await device2.NewContext().DatasyncOperationsQueue.ToListAsync());
    }

    [Fact]
    public async Task C09_the_write_after_a_resolver_merge_is_unconditional()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        await using ProbeClient device1 = new(server);
        await using ProbeClient device2 = new(server);
        await PullFreshAsync(device1);
        await PullFreshAsync(device2);
        device2.Config.ItemResolver = new RecordingMergeResolver((client, _) => client);

        await device1.EditAsync("i1", x => x.A = "one");
        await device2.EditAsync("i1", x => x.A = "two");
        await device1.NewContext().PushAsync();
        device2.Fault.Requests.Clear();
        await device2.NewContext().PushAsync();

        Assert.Collection(device2.Fault.Requests,
            first => { Assert.Equal("PUT", first.Method); Assert.NotNull(first.IfMatch); },
            second => { Assert.Equal("PUT", second.Method); Assert.Null(second.IfMatch); });
    }

    /// <summary>
    /// The most important finding of the spike. Because of C09, a third writer landing between the
    /// 412 and the forced write is silently lost, so a merge done in IConflictResolver is not safe on its own.
    /// </summary>
    [Fact]
    public async Task C10_a_resolver_merge_loses_a_third_writers_update_in_the_race_window()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1", a: "a0", b: "b0");
        await using ProbeClient device1 = new(server);
        await using ProbeClient device2 = new(server);
        await PullFreshAsync(device1);
        await PullFreshAsync(device2);

        device2.Config.ItemResolver = new RecordingMergeResolver((client, serverRow) => new ClientItem { Id = client.Id, A = serverRow.A, B = client.B });

        await device1.EditAsync("i1", x => x.A = "a1");
        await device2.EditAsync("i1", x => x.B = "b2");
        await device1.NewContext().PushAsync();

        // A third writer lands just as device 2 receives its 412. Raw HTTP, not a second Datasync
        // client: LockManager is process-wide (C23), so one would deadlock inside this push.
        bool injected = false;
        using HttpClient thirdWriter = new() { BaseAddress = server.BaseAddress };
        device2.Fault.AfterSend = async (_, response) =>
        {
            if (!injected && (int)response.StatusCode == 412)
            {
                injected = true;
                using HttpResponseMessage written = await thirdWriter.PutAsync("tables/items/i1", JsonContent.Create(new { id = "i1", a = "a3-must-survive", b = "b0" }));
                Assert.True(written.IsSuccessStatusCode);
            }
        };

        Assert.True((await device2.NewContext().PushAsync()).IsSuccessful);

        Assert.True(injected);
        ServerItem stored = await server.WithDbAsync(db => db.Items.AsNoTracking().SingleAsync(x => x.Id == "i1"));
        Assert.Equal("a1", stored.A); // Device 3's acknowledged write to A has been silently overwritten.
        Assert.Equal("b2", stored.B);
    }

    /// <summary>The clean workaround for C10: no fork needed, because the queue row is public and retains If-Match.</summary>
    [Fact]
    public async Task C11_rebasing_a_failed_queue_row_gives_a_compare_and_swap_merge_loop()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1", a: "a0", b: "b0");
        await using ProbeClient device1 = new(server);
        await using ProbeClient device2 = new(server);
        await PullFreshAsync(device1);
        await PullFreshAsync(device2);

        await device1.EditAsync("i1", x => x.A = "a1");
        await device2.EditAsync("i1", x => x.B = "b2");
        await device1.NewContext().PushAsync();

        // No resolver: the push fails, and the failure carries the server's row.
        ProbeClientDb first = device2.NewContext();
        PushResult failed = await first.PushAsync();
        ServiceResponse conflict = Assert.Single(failed.FailedRequests).Value;
        conflict.ContentStream.Position = 0;
        ClientItem serverRow = (await JsonSerializer.DeserializeAsync<ClientItem>(conflict.ContentStream, DatasyncSerializer.JsonSerializerOptions))!;

        ProbeClientDb rebase = device2.NewContext();
        DatasyncOperation op = await rebase.DatasyncOperationsQueue.SingleAsync();
        ClientItem clientRow = JsonSerializer.Deserialize<ClientItem>(op.Item, DatasyncSerializer.JsonSerializerOptions)!;
        op.Item = JsonSerializer.Serialize(new ClientItem { Id = clientRow.Id, A = serverRow.A, B = clientRow.B }, DatasyncSerializer.JsonSerializerOptions);
        op.EntityVersion = serverRow.Version!;
        await rebase.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false);

        device2.Fault.Requests.Clear();
        PushResult retried = await device2.NewContext().PushAsync();

        Assert.True(retried.IsSuccessful);
        Assert.NotNull(Assert.Single(device2.Fault.Requests).IfMatch); // Still conditional, so a racing writer gets a 412, not a loss.
        ServerItem stored = await server.WithDbAsync(db => db.Items.AsNoTracking().SingleAsync(x => x.Id == "i1"));
        Assert.Equal("a1", stored.A);
        Assert.Equal("b2", stored.B);
    }

    [Fact]
    public async Task C12_a_lost_response_to_a_replace_makes_the_device_conflict_with_itself()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        await using ProbeClient client = new(server);
        await PullFreshAsync(client);
        await client.EditAsync("i1", x => x.A = "applied");

        // The server applies the write, but the device never hears back.
        client.Fault.AfterSend = (_, _) => throw new HttpRequestException("response lost");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.NewContext().PushAsync());
        client.Fault.AfterSend = null;
        Assert.Equal("applied", (await server.WithDbAsync(db => db.Items.AsNoTracking().SingleAsync(x => x.Id == "i1"))).A);

        PushResult retry = await client.NewContext().PushAsync();

        Assert.False(retry.IsSuccessful);
        Assert.Equal(412, Assert.Single(retry.FailedRequests).Value.StatusCode);
    }

    /// <summary>Why a mid-batch drop is worse than it looks: completed work is only recorded at the end of the batch.</summary>
    [Fact]
    public async Task C13_a_connection_drop_mid_batch_forgets_operations_that_already_succeeded()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        await server.SeedAsync("i2");
        await using ProbeClient client = new(server);
        await PullFreshAsync(client);
        await client.EditAsync("i1", x => x.A = "one");
        await client.EditAsync("i2", x => x.A = "two");

        int puts = 0;
        client.Fault.BeforeSend = _ => ++puts == 2 ? throw new HttpRequestException("signal lost") : Task.CompletedTask;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.NewContext().PushAsync());
        client.Fault.BeforeSend = null;

        // One row reached the server, yet both operations are still queued on disk.
        int applied = await server.WithDbAsync(db => db.Items.AsNoTracking().CountAsync(x => x.A != null));
        Assert.Equal(1, applied);
        Assert.Equal(2, await client.NewContext().DatasyncOperationsQueue.CountAsync());

        // After a restart, the retry therefore self-conflicts on the one that had already succeeded.
        PushResult retry = await client.NewContext().PushAsync();
        Assert.Equal(1, retry.CompletedOperations);
        Assert.Equal(412, Assert.Single(retry.FailedRequests).Value.StatusCode);
    }

    /// <summary>The design proposed in ADR-0012: append-only rows with client-assigned IDs make retries harmless.</summary>
    [Fact]
    public async Task C14_an_append_only_table_with_client_ids_plus_server_wins_is_idempotent_over_datasync()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await using ProbeClient client = new(server);
        client.Config.OpResolver = new ServerWinsConflictResolver();

        const string opId = "0192f3a0c1d27a3e9b4c5d6e7f801234";
        ProbeClientDb write = client.NewContext();
        write.Ops.Add(new ClientOp { Id = opId, Payload = "spend:1" });
        await write.SaveChangesAsync();

        // First attempt: the server stores it, but the response is lost.
        client.Fault.AfterSend = (_, _) => throw new HttpRequestException("response lost");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.NewContext().PushAsync());
        client.Fault.AfterSend = null;
        Assert.Equal(1, await client.NewContext().DatasyncOperationsQueue.CountAsync());

        // Retry: 409 with the stored row, which server-wins resolves to "already applied".
        client.Fault.Requests.Clear();
        PushResult retry = await client.NewContext().PushAsync();

        Assert.True(retry.IsSuccessful);
        Assert.Equal("POST", Assert.Single(client.Fault.Requests).Method);
        Assert.Empty(await client.NewContext().DatasyncOperationsQueue.ToListAsync());
        Assert.Equal(1, await server.WithDbAsync(db => db.Ops.AsNoTracking().CountAsync(x => x.Id == opId)));
    }

    [Fact]
    public async Task C15_without_a_resolver_the_same_duplicate_add_is_a_stuck_409()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await using ProbeClient client = new(server);
        ProbeClientDb write = client.NewContext();
        write.Ops.Add(new ClientOp { Id = "0192f3a0c1d27a3e9b4c5d6e7f805678", Payload = "spend:1" });
        await write.SaveChangesAsync();

        client.Fault.AfterSend = (_, _) => throw new HttpRequestException("response lost");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.NewContext().PushAsync());
        client.Fault.AfterSend = null;

        PushResult retry = await client.NewContext().PushAsync();
        Assert.Equal(409, Assert.Single(retry.FailedRequests).Value.StatusCode);
    }

    [Fact]
    public async Task C16_a_DoNotSynchronize_table_commits_atomically_with_a_row_edit_and_is_never_pushed_or_queued()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        await using ProbeClient client = new(server);
        await PullFreshAsync(client);

        ProbeClientDb db = client.NewContext();
        (await db.Items.SingleAsync()).A = "edit";
        db.LocalLedger.Add(new LocalLedgerRow { Id = "l1", Note = "spend" });
        await db.SaveChangesAsync();

        // The queue holds the row edit only; the ledger is local.
        DatasyncOperation op = await client.NewContext().DatasyncOperationsQueue.SingleAsync();
        Assert.Equal("i1", op.ItemId);
        client.Fault.Requests.Clear();
        await client.NewContext().PushAsync();
        Assert.All(client.Fault.Requests, r => Assert.DoesNotContain("ledger", r.Path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, await client.NewContext().LocalLedger.CountAsync());

        // Atomicity: a failed commit keeps neither the ledger row nor the queue row.
        ProbeClientDb failing = client.NewContext();
        (await failing.Items.SingleAsync()).A = "second edit";
        failing.LocalLedger.Add(new LocalLedgerRow { Id = "l1", Note = "duplicate key" });
        await Assert.ThrowsAsync<DbUpdateException>(() => failing.SaveChangesAsync());
        Assert.Equal(1, await client.NewContext().LocalLedger.CountAsync());
        Assert.Empty(await client.NewContext().DatasyncOperationsQueue.ToListAsync());
    }

    [Fact]
    public async Task C17_a_pull_overwrites_every_column_except_JsonIgnore_columns()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1", a: "a0");
        await using ProbeClient device1 = new(server);
        await using ProbeClient device2 = new(server);
        await PullFreshAsync(device1);
        await PullFreshAsync(device2);

        // Local-only state on device 2. addToQueue:false, because it should not sync.
        ProbeClientDb local = device2.NewContext();
        (await local.Items.SingleAsync()).LocalOnly = "keep me";
        await local.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false);

        await device1.EditAsync("i1", x => x.A = "a1");
        await device1.NewContext().PushAsync();
        await PullFreshAsync(device2);

        ClientItem row = await device2.ReadAsync("i1");
        Assert.Equal("a1", row.A);
        Assert.Equal("keep me", row.LocalOnly);
    }

    [Fact]
    public async Task C18_JsonIgnore_columns_are_not_sent_to_the_server()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        await using ProbeClient client = new(server);
        await PullFreshAsync(client);

        await client.EditAsync("i1", x => { x.A = "edit"; x.LocalOnly = "secret"; });
        DatasyncOperation op = await client.NewContext().DatasyncOperationsQueue.SingleAsync();

        Assert.DoesNotContain("secret", op.Item);
    }

    /// <summary>
    /// The delta token filters on updatedAt strictly greater than the newest row seen. A row committed later
    /// with the same millisecond is never delivered, which would silently break convergence.
    /// </summary>
    [Fact]
    public async Task C19_a_row_with_the_same_updatedAt_as_the_delta_token_is_never_pulled()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1", a: "first");
        await using ProbeClient client = new(server);
        await PullFreshAsync(client);

        DateTimeOffset sameMillisecond = (await server.WithDbAsync(db => db.Items.AsNoTracking().SingleAsync(x => x.Id == "i1"))).UpdatedAt!.Value;
        await server.WithDbAsync(async db =>
        {
            // Not via the repository: this pins UpdatedAt to the exact value the repository would stamp in the same millisecond.
            db.Items.Add(new ServerItem { Id = "i2", A = "second", UpdatedAt = sameMillisecond, Version = Guid.NewGuid().ToByteArray() });
            return await db.SaveChangesAsync();
        });

        await PullFreshAsync(client);

        Assert.Equal(1, await client.NewContext().Items.CountAsync());
        client.Fault.Requests.Clear();
        await PullFreshAsync(client);

        // The cause: the delta filter is a strict "greater than".
        Assert.Contains("updatedAt gt ", client.Fault.Requests[0].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task C20_an_http_pipeline_handler_can_stamp_device_identity_on_every_request()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        await using ProbeClient client = new(server);
        client.Fault.BeforeSend = request =>
        {
            request.Headers.Add("X-Leeway-Device", "gate-7");
            return Task.CompletedTask;
        };

        await PullFreshAsync(client);
        await client.EditAsync("i1", x => x.A = "edit");
        await client.NewContext().PushAsync();

        Assert.True(server.Probe.DeviceHeaders.Count >= 2);
        Assert.All(server.Probe.DeviceHeaders, h => Assert.Equal("gate-7", h));
    }

    [Fact]
    public async Task C21_synchronization_events_expose_each_pushed_operation()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        await using ProbeClient client = new(server);
        await PullFreshAsync(client);
        await client.EditAsync("i1", x => x.A = "edit");

        List<SynchronizationEventArgs> events = [];
        ProbeClientDb db = client.NewContext();
        db.SynchronizationProgress += (_, e) => events.Add(e);
        await db.PushAsync();

        Assert.Equal(
            [SynchronizationEventType.PushStarted, SynchronizationEventType.PushItem, SynchronizationEventType.PushEnded],
            events.Select(e => e.EventType));
        Assert.Equal("i1", events[1].PushOperation!.ItemId);
    }

    /// <summary>
    /// The sync lock is static, so it spans every context in the process. A queued local write waits for any
    /// in-flight push, which offline means up to the HTTP timeout. A write with addToQueue:false does not wait.
    /// </summary>
    [Fact]
    public async Task C23_a_queued_local_write_blocks_during_a_push_but_an_unqueued_one_does_not()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        await server.SeedAsync("i2");
        await using ProbeClient client = new(server);
        await PullFreshAsync(client);
        await client.EditAsync("i1", x => x.A = "being pushed");

        TaskCompletionSource requestArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Fault.BeforeSend = async _ =>
        {
            requestArrived.TrySetResult();
            await release.Task;
        };

        Task<PushResult> push = client.NewContext().PushAsync();
        await requestArrived.Task;

        // A wallet-style write, outside the Datasync queue, completes while the push is stuck.
        ProbeClientDb unqueued = client.NewContext();
        unqueued.LocalLedger.Add(new LocalLedgerRow { Id = "l1", Note = "spend" });
        await unqueued.SaveChangesAsync(acceptAllChangesOnSuccess: true, addToQueue: false).WaitAsync(TimeSpan.FromSeconds(5));

        // A normal write to a synchronized row cannot finish until the push does.
        ProbeClientDb queued = client.NewContext();
        (await queued.Items.SingleAsync(x => x.Id == "i2")).A = "edited during push";
        Task<int> queuedWrite = Task.Run(() => queued.SaveChangesAsync());
        await Task.Delay(500);
        Assert.False(queuedWrite.IsCompleted);

        release.SetResult();
        Assert.True((await push).IsSuccessful);
        Assert.True(await queuedWrite.WaitAsync(TimeSpan.FromSeconds(10)) > 0);
    }

    /// <summary>
    /// Found by a flaky exit check. A timeout is an OperationCanceledException, which the push's ActionBlock treats
    /// as cooperative cancellation rather than a fault. So on a bad signal, unlike no signal (C02), PushAsync reports
    /// success having done nothing. Callers must check the queue, not PushResult.IsSuccessful.
    /// </summary>
    [Fact]
    public async Task C24_a_push_that_times_out_reports_success_with_the_operation_still_queued()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        await using ProbeClient client = new(server, timeout: TimeSpan.FromMilliseconds(500));
        await PullFreshAsync(client);
        await client.EditAsync("i1", x => x.A = "never sent");

        client.Fault.Hang = true;
        PushResult push = await client.NewContext().PushAsync();

        Assert.True(push.IsSuccessful); // The trap.
        Assert.Equal(0, push.CompletedOperations);
        Assert.Empty(push.FailedRequests);
        DatasyncOperation op = await client.NewContext().DatasyncOperationsQueue.SingleAsync();
        Assert.Equal(OperationState.Pending, op.State);
        Assert.Null((await server.WithDbAsync(d => d.Items.AsNoTracking().SingleAsync(x => x.Id == "i1"))).A);

        // A caller that trusts IsSuccessful goes on to pull, and gets an exception it had no reason to expect.
        await Assert.ThrowsAsync<DatasyncException>(() => client.NewContext().PullAsync([typeof(ClientItem)], new PullOptions()));
    }

    /// <summary>
    /// C24 and C12 combined, also found under load: the server applies the write, the client times out waiting
    /// for the response, and the push reports success. The retry then conflicts with the device's own write.
    /// So on a poor signal, one device with no concurrent writers can end up stuck with the default resolver.
    /// </summary>
    [Fact]
    public async Task C25_a_timeout_after_the_server_applied_the_write_leaves_the_device_conflicting_with_itself()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        await using ProbeClient client = new(server, timeout: TimeSpan.FromMilliseconds(500));
        await PullFreshAsync(client);
        await client.EditAsync("i1", x => x.A = "applied");

        client.Fault.AfterSend = async (_, _) => await Task.Delay(TimeSpan.FromSeconds(5));
        PushResult first = await client.NewContext().PushAsync();
        client.Fault.AfterSend = null;

        Assert.True(first.IsSuccessful);
        Assert.Equal(0, first.CompletedOperations);
        Assert.Equal("applied", (await server.WithDbAsync(d => d.Items.AsNoTracking().SingleAsync(x => x.Id == "i1"))).A);
        Assert.Equal(1, await client.NewContext().DatasyncOperationsQueue.CountAsync());

        PushResult retry = await client.NewContext().PushAsync();

        Assert.False(retry.IsSuccessful);
        Assert.Equal(412, Assert.Single(retry.FailedRequests).Value.StatusCode);
    }

    /// <summary>Guards ADR-0008: the pinned native SQLite must be at or above the CVE-2025-6965 fix.</summary>
    [Fact]
    public async Task C22_the_loaded_native_sqlite_is_patched_for_CVE_2025_6965()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "select sqlite_version()";
        string version = (string)(await command.ExecuteScalarAsync())!;
        Assert.True(Version.Parse(version) >= new Version(3, 50, 2), $"Loaded SQLite is {version}");
    }
}
