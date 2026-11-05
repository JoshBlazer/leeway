using System.Text.Json;
using Leeway.Core;
using Leeway.Datasync.Client;

namespace Leeway.Integration.Tests.Leeway;

/// <summary>M5: generated code running for real, in this assembly, over real Datasync.</summary>
public class GeneratedApiTests
{
    [Fact]
    public void Entities_in_this_assembly_use_a_generated_schema_rather_than_reflection()
    {
        Assert.True(MergeSchema.For(typeof(Zone)).IsGenerated);
        Assert.True(MergeSchema.For(typeof(ServerZone)).IsGenerated);
    }

    [Theory]
    [InlineData(typeof(Zone))]
    [InlineData(typeof(ServerZone))]
    public void The_generated_schema_agrees_with_reflection(Type type)
    {
        MergeSchema generated = MergeSchema.For(type);
        MergeSchema reflected = MergeSchema.FromReflection(type);

        Assert.False(reflected.IsGenerated);
        Assert.Equal(reflected.Fields.Keys.Order(), generated.Fields.Keys.Order());
        foreach ((string name, MergeField field) in reflected.Fields)
        {
            MergeField other = generated.Fields[name];
            Assert.Equal(field.Kind, other.Kind);
            Assert.Equal(field.ValueType, other.ValueType);
            Assert.Equal(field.Min, other.Min);
            Assert.Equal(field.Lease, other.Lease);
        }
    }

    [Fact]
    public async Task The_typed_api_works_end_to_end()
    {
        await using TestServer server = await TestServer.StartAsync(capacity: 10);
        using Gate a = await Gate.OpenAsync(server, "gate-a");
        using Gate b = await Gate.OpenAsync(server, "gate-b");

        await a.Client.RequestRemainingCapacityLeaseAsync("north", 4);
        await a.Client.SyncAsync();
        Assert.Equal(4, (await a.Client.RemainingCapacityWalletAsync("north")).Available);

        a.Fault.Offline = true;
        SpendResult spend = await a.Client.TrySpendRemainingCapacityAsync("north", 1);
        Assert.Equal(SpendStatus.Confirmed, spend.Status);
        await a.Client.AddCheckInsAsync("north", 2);
        await a.Client.SetNotesAsync("north", "typed");
        await a.Client.AddToFlagsJsonAsync("north", "vip");
        await a.Client.AddToFlagsJsonAsync("north", "medical");
        await a.Client.RemoveFromFlagsJsonAsync("north", "vip");
        Assert.Equal(3, await a.Client.ReturnRemainingCapacityLeaseAsync("north", 3));
        a.Fault.Offline = false;

        Assert.Equal(SyncState.Synced, (await a.Client.SyncAsync()).State);
        Assert.Equal(SyncState.Synced, (await b.Client.SyncAsync()).State);

        Zone zone = await b.ZoneAsync();
        Assert.Equal(9, zone.RemainingCapacity);
        Assert.Equal(2, zone.CheckIns);
        Assert.Equal("typed", zone.Notes);
        Assert.Equal(["medical"], JsonSerializer.Deserialize<string[]>(zone.FlagsJson!));
        Assert.Equal(OutcomeStatus.SpendConfirmed, (await a.Client.OutcomeOfAsync(spend.OperationId))!.Status);
    }
}
