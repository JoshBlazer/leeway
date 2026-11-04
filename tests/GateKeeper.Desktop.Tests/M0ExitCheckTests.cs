using Avalonia.Threading;

namespace GateKeeper.Desktop.Tests;

/// <summary>
/// The M0 exit check: edit offline, reconnect, sync. These drive the real window with real input,
/// against the real server app on a real socket. Nothing in the Datasync path is mocked.
/// </summary>
public class M0ExitCheckTests
{
    [Fact]
    public Task Edit_with_the_server_down_then_reconnect_and_sync() => Ui.Run(async () =>
    {
        await using ServerProcess server = new();
        await server.StartAsync();

        GateApp north = new(server, "gate-north");
        GateApp south = new(server, "gate-south");
        await north.ViewModel.InitializeAsync();
        await south.ViewModel.InitializeAsync();
        Assert.Equal("Synced with the server.", north.Status);
        Assert.Contains("North Stand (capacity 500): no notes", north.ZoneLines);
        north.Capture("m0-1-online.png");

        // The server goes away: connections are now really refused.
        await server.StopAsync();
        north.SelectZone("zone-north");
        await north.TypeNotesAndSaveAsync("Turnstile 3 jammed");

        Assert.Equal("Saved locally. Waiting to sync.", north.Status);
        Assert.Equal("Changes waiting to sync: 1", north.PendingLine);
        Assert.Contains("North Stand (capacity 500): Turnstile 3 jammed", north.ZoneLines);

        await north.ClickSyncAsync();
        Assert.Equal("Server unreachable. Edits stay queued on this device.", north.Status);
        Assert.Equal("Changes waiting to sync: 1", north.PendingLine);
        north.Capture("m0-2-offline-edit.png");

        // Reconnect: same port, same database.
        await server.StartAsync();
        await north.ClickSyncAsync();
        Assert.Equal("Synced with the server.", north.Status);
        Assert.Equal("Changes waiting to sync: 0", north.PendingLine);
        north.Capture("m0-3-reconnected.png");

        // The proof the edit really reached the server: a different gate receives it.
        await south.ClickSyncAsync();
        Assert.Contains("North Stand (capacity 500): Turnstile 3 jammed", south.ZoneLines);
    });

    [Fact]
    public Task The_no_signal_switch_blocks_sync_until_it_is_turned_off() => Ui.Run(async () =>
    {
        await using ServerProcess server = new();
        await server.StartAsync();
        GateApp gate = new(server, "gate");
        await gate.ViewModel.InitializeAsync();

        gate.Find<Avalonia.Controls.ToggleSwitch>("NoSignalSwitch").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(gate.Gate.SimulateNoSignal);

        gate.SelectZone("zone-south");
        await gate.TypeNotesAndSaveAsync("Queue at door B");
        await gate.ClickSyncAsync();
        Assert.Equal("Server unreachable. Edits stay queued on this device.", gate.Status);
        Assert.Equal("Changes waiting to sync: 1", gate.PendingLine);

        gate.Find<Avalonia.Controls.ToggleSwitch>("NoSignalSwitch").IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        await gate.ClickSyncAsync();
        Assert.Equal("Synced with the server.", gate.Status);
        Assert.Equal("Changes waiting to sync: 0", gate.PendingLine);
    });

    /// <summary>Regression test for Datasync probe C24: a timed-out push reports success with the edit still queued.</summary>
    [Fact]
    public Task A_sync_that_times_out_on_a_poor_signal_keeps_the_edit_and_reports_offline() => Ui.Run(async () =>
    {
        await using ServerProcess server = new();
        await server.StartAsync();
        GateApp gate = new(server, "gate");
        await gate.ViewModel.InitializeAsync();

        gate.SelectZone("zone-north");
        await gate.TypeNotesAndSaveAsync("Sent on a poor signal");
        gate.Signal.Hang = true;

        await gate.ClickSyncAsync();
        Assert.Equal("Server unreachable. Edits stay queued on this device.", gate.Status);
        Assert.Equal("Changes waiting to sync: 1", gate.PendingLine);

        gate.Signal.Hang = false;
        await gate.ClickSyncAsync();
        Assert.Equal("Synced with the server.", gate.Status);
        Assert.Equal("Changes waiting to sync: 0", gate.PendingLine);
    });

    /// <summary>The baseline Leeway exists to improve: with the default resolver, whole-row conflicts just stop.</summary>
    [Fact]
    public Task Default_resolver_two_offline_edits_to_one_row_leave_a_stuck_conflict() => Ui.Run(async () =>
    {
        await using ServerProcess server = new();
        await server.StartAsync();
        GateApp north = new(server, "gate-north");
        GateApp south = new(server, "gate-south");
        await north.ViewModel.InitializeAsync();
        await south.ViewModel.InitializeAsync();

        await server.StopAsync();
        north.SelectZone("zone-north");
        await north.TypeNotesAndSaveAsync("From north gate");
        south.SelectZone("zone-north");
        await south.TypeNotesAndSaveAsync("From south gate");

        await server.StartAsync();
        await north.ClickSyncAsync();
        Assert.Equal("Synced with the server.", north.Status);

        await south.ClickSyncAsync();
        Assert.StartsWith("Conflict: the server rejected 1 change(s).", south.Status);
        Assert.Equal("Changes waiting to sync: 1", south.PendingLine);
        south.Capture("m0-4-default-conflict.png");

        // Syncing again changes nothing, and the other gate's value holds on the server.
        await south.ClickSyncAsync();
        Assert.StartsWith("Conflict:", south.Status);
        await north.ClickSyncAsync();
        Assert.Contains("North Stand (capacity 500): From north gate", north.ZoneLines);
    });
}
