using CommunityToolkit.Datasync.Client.Offline;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace Leeway.Integration.Tests.Datasync;

/// <summary>Characterization tests for the server side of Datasync 10.1.2. See <see cref="ClientProbes"/>.</summary>
public class ServerProbes
{
    private static async Task<(ProbeClient One, ProbeClient Two)> TwoSyncedClientsAsync(ProbeServer server)
    {
        ProbeClient one = new(server);
        ProbeClient two = new(server);
        Assert.True((await one.NewContext().PullAsync([typeof(ClientItem)], new PullOptions())).IsSuccessful);
        Assert.True((await two.NewContext().PullAsync([typeof(ClientItem)], new PullOptions())).IsSuccessful);
        return (one, two);
    }

    private static Task<ServerItem> StoredAsync(ProbeServer server, string id)
        => server.WithDbAsync(db => db.Items.AsNoTracking().SingleAsync(x => x.Id == id));

    [Fact]
    public async Task S01_a_successful_write_runs_precommit_then_repository_then_postcommit()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        (ProbeClient one, ProbeClient two) = await TwoSyncedClientsAsync(server);
        await using (one)
        await using (two)
        {
            await one.EditAsync("i1", x => x.A = "edit");
            server.Probe.Log.Clear();
            await one.NewContext().PushAsync();

            Assert.Equal(["pre:Update", "repo:replace:version=present", "post:Update"], server.Probe.Log);
        }
    }

    [Fact]
    public async Task S02_a_precommit_hook_can_rewrite_the_entity_before_it_is_stored()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        server.Probe.PreCommit = entity => entity.B = "added by server";
        (ProbeClient one, ProbeClient two) = await TwoSyncedClientsAsync(server);
        await using (one)
        await using (two)
        {
            await one.EditAsync("i1", x => x.A = "edit");
            await one.NewContext().PushAsync();

            Assert.Equal("added by server", (await StoredAsync(server, "i1")).B);

            // The device gets the rewritten row back immediately, in the push response.
            Assert.Equal("added by server", (await one.ReadAsync("i1")).B);
        }
    }

    [Fact]
    public async Task S03_a_stale_if_match_is_rejected_before_hooks_or_repository_see_the_request()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        (ProbeClient one, ProbeClient two) = await TwoSyncedClientsAsync(server);
        await using (one)
        await using (two)
        {
            await one.EditAsync("i1", x => x.A = "one");
            await two.EditAsync("i1", x => x.A = "two");
            await one.NewContext().PushAsync();

            server.Probe.Log.Clear();
            PushResult push = await two.NewContext().PushAsync();

            Assert.Equal(412, Assert.Single(push.FailedRequests).Value.StatusCode);
            Assert.Empty(server.Probe.Log); // So neither a hook nor a custom repository can intercept a version conflict.
        }
    }

    [Fact]
    public async Task S04_overriding_ReplaceAsync_can_turn_a_conflict_into_a_server_side_merge()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1", a: "a0", b: "b0");
        (ProbeClient one, ProbeClient two) = await TwoSyncedClientsAsync(server);
        await using (one)
        await using (two)
        {
            await one.EditAsync("i1", x => x.A = "a1");
            await two.EditAsync("i1", x => x.B = "b2");
            await one.NewContext().PushAsync();

            // The controller override removes the precondition; the hook, which runs next, does the merge.
            server.Probe.StripIfMatchInController = true;
            server.Probe.PreCommit = incoming => incoming.A = "a1";
            server.Probe.Log.Clear();
            PushResult push = await two.NewContext().PushAsync();

            Assert.True(push.IsSuccessful);
            Assert.Equal(["controller:replace-override", "pre:Update", "repo:replace:version=absent", "post:Update"], server.Probe.Log);
            ServerItem stored = await StoredAsync(server, "i1");
            Assert.Equal("a1", stored.A);
            Assert.Equal("b2", stored.B);

            // No client resolver ran: the device simply received the merged row.
            ClientItem local = await two.ReadAsync("i1");
            Assert.Equal("a1", local.A);
            Assert.Equal("b2", local.B);
        }
    }

    /// <summary>Hooks are not transactional with the store, so they cannot be what makes an operation apply exactly once.</summary>
    [Fact]
    public async Task S05_a_failing_postcommit_hook_reports_an_error_but_the_write_has_already_committed()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1");
        (ProbeClient one, ProbeClient two) = await TwoSyncedClientsAsync(server);
        await using (one)
        await using (two)
        {
            await one.EditAsync("i1", x => x.A = "committed");
            server.Probe.ThrowInPostCommit = true;

            HttpRequestMessage request = new(HttpMethod.Put, new Uri(server.BaseAddress, "tables/items/i1"))
            {
                Content = JsonContent.Create(new { id = "i1", a = "committed" }),
            };
            using HttpClient http = new();
            using HttpResponseMessage response = await http.SendAsync(request);

            Assert.Equal(500, (int)response.StatusCode);
            Assert.Equal("committed", (await StoredAsync(server, "i1")).A);
        }
    }

    [Fact]
    public async Task S06_a_duplicate_create_returns_409_with_the_stored_row_and_runs_precommit_first()
    {
        await using ProbeServer server = await ProbeServer.StartAsync();
        await server.SeedAsync("i1", a: "stored");
        using HttpClient http = new();

        using HttpResponseMessage response = await http.PostAsync(
            new Uri(server.BaseAddress, "tables/items"),
            JsonContent.Create(new { id = "i1", a = "duplicate" }));

        Assert.Equal(409, (int)response.StatusCode);
        Assert.Contains("stored", await response.Content.ReadAsStringAsync());
        Assert.Contains("pre:Create", server.Probe.Log);
        Assert.Equal("stored", (await StoredAsync(server, "i1")).A);
    }
}
