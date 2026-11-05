using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using GateKeeper.Desktop.ViewModels;

namespace GateKeeper.Desktop.Tests;

/// <summary>
/// The M6 demo as tests: real windows and real input, against the real server and dashboard on real sockets.
/// Stopping the server really refuses connections. Nothing in the Leeway or Datasync path is mocked.
/// </summary>
public class M6ExitCheckTests
{
    private static async Task<JsonElement> StateAsync(ServerProcess server)
    {
        using HttpClient http = new();
        return JsonDocument.Parse(await http.GetStringAsync(server.DashboardAddress + "api/state")).RootElement;
    }

    private static JsonElement ZoneState(JsonElement state, string id)
        => state.GetProperty("zones").EnumerateArray().Single(z => z.GetProperty("id").GetString() == id);

    /// <summary>The main demo. It also produces the frames for the README's GIF, when asked to.</summary>
    [Fact]
    public Task Two_offline_gates_cannot_overfill_a_zone_and_a_duplicate_ticket_is_flagged() => Ui.Run(async () =>
    {
        await using ServerProcess server = new();
        await server.StartAsync();
        await server.StartDashboardAsync();
        GateApp a = new(server, "gate-a");
        GateApp b = new(server, "gate-b");
        await a.ViewModel.InitializeAsync();
        await b.ViewModel.InitializeAsync();

        // North Stand: capacity 10. Each gate leases 4, and 2 stay in the pool.
        Assert.Equal("Lease: 4 left", a.WalletLine);
        Assert.Equal("Lease: 4 left", b.WalletLine);
        JsonElement start = ZoneState(await StateAsync(server), "zone-north");
        Assert.Equal(2, start.GetProperty("unleased").GetInt64());
        await server.CaptureStepAsync(a, b, "1-online");

        // The signal goes: the server is really gone.
        await server.StopAsync();
        foreach (string ticket in new[] { "T-1001", "T-1002", "T-1003", "T-1004" })
        {
            await a.ScanAsync(ticket);
            Assert.StartsWith("ADMIT", a.LastScan);
        }

        Assert.Equal("Lease: 0 left", a.WalletLine);
        await a.ScanAsync("T-1005");
        Assert.StartsWith("PROVISIONAL", a.LastScan);

        // Gate B cannot know that T-1001 was already used.
        foreach (string ticket in new[] { "T-1001", "T-1006", "T-1007", "T-1008" })
        {
            await b.ScanAsync(ticket);
            Assert.StartsWith("ADMIT", b.LastScan);
        }

        await b.ScanAsync("T-1009");
        Assert.StartsWith("PROVISIONAL", b.LastScan);
        await b.ScanAsync("T-1010");
        Assert.StartsWith("PROVISIONAL", b.LastScan);

        await a.ClickSyncAsync();
        await b.ClickSyncAsync();
        Assert.StartsWith("Server unreachable", a.Status);
        Assert.StartsWith("Server unreachable", b.Status);
        Assert.Equal("Lease: 0 left", a.WalletLine);
        await server.CaptureStepAsync(a, b, "2-offline");

        // Reconnect. The pool holds 2, and 3 provisional spends want it.
        await server.StartAsync();
        await a.ClickSyncAsync();
        await server.CaptureStepAsync(a, b, "3-gate-a-back");
        await b.ClickSyncAsync();
        await a.ClickSyncAsync();
        await b.ClickSyncAsync();
        Assert.Equal("Synced with the server.", a.Status);
        Assert.Equal("Synced with the server.", b.Status);

        Assert.Contains(a.ScanLines, line => line.StartsWith("T-1005: admitted", StringComparison.Ordinal));

        // First come, first served within a gate: T-1009 was scanned first, so it gets the last place.
        Assert.Contains("T-1009: admitted", b.ScanLines);
        Assert.Contains("T-1010: turned away", b.ScanLines);

        // The rule held, and the duplicate is flagged at both gates and on the dashboard.
        JsonElement state = await StateAsync(server);
        JsonElement north = ZoneState(state, "zone-north");
        Assert.Equal(10, north.GetProperty("consumed").GetInt64());
        Assert.Equal(0, north.GetProperty("remaining").GetInt64());
        Assert.True(north.GetProperty("conserved").GetBoolean());
        Assert.Equal("T-1001", state.GetProperty("duplicates").EnumerateArray().Single().GetProperty("ticket").GetString());
        Assert.Contains("T-1001: gate-a, gate-b", a.DuplicateLines);
        Assert.Contains("T-1001: gate-a, gate-b", b.DuplicateLines);
        Assert.Contains("North Stand: 0 of 10 left", a.ZoneLines[0]);
        Assert.Equal(a.ZoneLines[0], b.ZoneLines[0]);
        await server.CaptureStepAsync(a, b, "4-reconciled");
    });

    [Fact]
    public Task A_gate_refuses_what_it_can_tell_is_wrong_with_no_signal() => Ui.Run(async () =>
    {
        await using ServerProcess server = new();
        await server.StartAsync();
        GateApp gate = new(server, "gate-a");
        await gate.ViewModel.InitializeAsync();
        await server.StopAsync();

        await gate.ScanAsync("T-1001");
        Assert.StartsWith("ADMIT", gate.LastScan);

        await gate.ScanAsync("T-1001");
        Assert.StartsWith("REFUSED: already scanned at gate-a", gate.LastScan);

        await gate.ScanAsync("NOPE");
        Assert.StartsWith("REFUSED: unknown ticket", gate.LastScan);

        await gate.ScanAsync("S-2001");
        Assert.StartsWith("REFUSED: this ticket is for another zone", gate.LastScan);

        Assert.Equal("Lease: 3 left", gate.WalletLine);
    });

    [Fact]
    public Task A_gate_refuses_a_ticket_it_has_learned_was_used_elsewhere() => Ui.Run(async () =>
    {
        await using ServerProcess server = new();
        await server.StartAsync();
        GateApp a = new(server, "gate-a");
        GateApp b = new(server, "gate-b");
        await a.ViewModel.InitializeAsync();
        await b.ViewModel.InitializeAsync();

        await a.ScanAsync("T-1001");
        await a.ClickSyncAsync();
        await b.ClickSyncAsync();

        await b.ScanAsync("T-1001");
        Assert.StartsWith("REFUSED: already scanned at gate-a", b.LastScan);
    });

    [Fact]
    public Task Notes_and_flags_from_two_offline_gates_merge() => Ui.Run(async () =>
    {
        await using ServerProcess server = new();
        await server.StartAsync();
        GateApp a = new(server, "gate-a");
        GateApp b = new(server, "gate-b");
        await a.ViewModel.InitializeAsync();
        await b.ViewModel.InitializeAsync();
        await server.StopAsync();

        await a.SaveNotesAsync("Turnstile 3 jammed");
        await a.AddFlagAsync("medical");
        await b.AddFlagAsync("vip");

        // Shown straight away, with no signal.
        Assert.Contains("Turnstile 3 jammed", a.ZoneLines[0]);
        Assert.Contains("medical", a.ZoneLines[0]);

        await server.StartAsync();
        await a.ClickSyncAsync();
        await b.ClickSyncAsync();
        await a.ClickSyncAsync();

        foreach (GateApp gate in new[] { a, b })
        {
            Assert.Contains("Turnstile 3 jammed", gate.ZoneLines[0]);
            Assert.Contains("medical", gate.ZoneLines[0]);
            Assert.Contains("vip", gate.ZoneLines[0]);
        }
    });

    [Fact]
    public Task A_poor_signal_keeps_every_scan_and_reports_offline() => Ui.Run(async () =>
    {
        await using ServerProcess server = new();
        await server.StartAsync();
        GateApp gate = new(server, "gate-a");
        await gate.ViewModel.InitializeAsync();
        await gate.ScanAsync("T-1001");

        gate.Signal.Hang = true;
        await gate.ClickSyncAsync();
        Assert.StartsWith("Server unreachable", gate.Status);

        gate.Signal.Hang = false;
        await gate.ClickSyncAsync();
        Assert.Equal("Synced with the server.", gate.Status);
        Assert.Equal(1, ZoneConsumed(gate));
    });

    [Fact]
    public Task The_no_signal_switch_blocks_sync_until_it_is_turned_off() => Ui.Run(async () =>
    {
        await using ServerProcess server = new();
        await server.StartAsync();
        GateApp gate = new(server, "gate-a");
        await gate.ViewModel.InitializeAsync();

        gate.Find<ToggleSwitch>("NoSignalSwitch").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        await gate.ScanAsync("T-1001");
        Assert.StartsWith("ADMIT", gate.LastScan);
        await gate.ClickSyncAsync();
        Assert.StartsWith("Server unreachable", gate.Status);

        gate.Find<ToggleSwitch>("NoSignalSwitch").IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        await gate.ClickSyncAsync();
        Assert.Equal("Synced with the server.", gate.Status);
    });

    [Fact]
    public Task A_new_lease_lets_a_gate_confirm_again() => Ui.Run(async () =>
    {
        await using ServerProcess server = new();
        await server.StartAsync();
        GateApp gate = new(server, "gate-a");
        await gate.ViewModel.InitializeAsync();
        foreach (string ticket in new[] { "T-1001", "T-1002", "T-1003", "T-1004" })
        {
            await gate.ScanAsync(ticket);
        }

        Assert.Equal("Lease: 0 left", gate.WalletLine);
        await gate.ClickSyncAsync();
        await gate.ClickRequestLeaseAsync();
        await gate.ClickSyncAsync();

        Assert.Equal("Lease: 4 left", gate.WalletLine);
        await gate.ScanAsync("T-1005");
        Assert.StartsWith("ADMIT", gate.LastScan);
    });

    [Fact]
    public Task The_dashboard_serves_its_page_and_says_when_the_server_is_down() => Ui.Run(async () =>
    {
        await using ServerProcess server = new();
        await server.StartAsync();
        await server.StartDashboardAsync();
        using HttpClient http = new();

        string page = await http.GetStringAsync(server.DashboardAddress);
        Assert.Contains("GateKeeper", page);
        Assert.DoesNotContain("http://", page.Replace("http://www.w3.org", string.Empty)); // No external assets.
        Assert.DoesNotContain("https://", page);

        JsonElement up = await StateAsync(server);
        Assert.True(up.GetProperty("serverUp").GetBoolean());
        Assert.Equal(10, ZoneState(up, "zone-north").GetProperty("capacity").GetInt64());

        await server.StopAsync();
        JsonElement down = await StateAsync(server);
        Assert.False(down.GetProperty("serverUp").GetBoolean());
        Assert.Equal(10, ZoneState(down, "zone-north").GetProperty("capacity").GetInt64()); // The last known state.
    });

    private static long ZoneConsumed(GateApp gate)
    {
        ZoneRow north = gate.ViewModel.Zones.Single(z => z.Id == "zone-north");
        return north.Capacity - north.Remaining;
    }
}
