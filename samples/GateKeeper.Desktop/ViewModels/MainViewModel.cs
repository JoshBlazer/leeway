using System.Collections.ObjectModel;
using GateKeeper.Desktop.Data;

namespace GateKeeper.Desktop.ViewModels;

public sealed class ZoneRow(Zone zone)
{
    public string Id { get; } = zone.Id;
    public string Name { get; } = zone.Name;
    public int Capacity { get; } = zone.Capacity;
    public string? Notes { get; } = zone.Notes;
    public string Summary => $"{Name} (capacity {Capacity}): {(string.IsNullOrEmpty(Notes) ? "no notes" : Notes)}";
}

public sealed class MainViewModel : ViewModelBase
{
    private readonly GateStore store;
    private readonly ConnectivityGate gate;
    private ZoneRow? selectedZone;
    private string? notesDraft;
    private string status = "Starting";
    private int pendingChanges;

    public MainViewModel(GateStore store, ConnectivityGate gate)
    {
        this.store = store;
        this.gate = gate;
        SaveCommand = new AsyncCommand(SaveAsync);
        SyncCommand = new AsyncCommand(SyncAsync);
    }

    public ObservableCollection<ZoneRow> Zones { get; } = [];

    public AsyncCommand SaveCommand { get; }

    public AsyncCommand SyncCommand { get; }

    public ZoneRow? SelectedZone
    {
        get => this.selectedZone;
        set
        {
            if (Set(ref this.selectedZone, value))
            {
                NotesDraft = value?.Notes;
            }
        }
    }

    public string? NotesDraft
    {
        get => this.notesDraft;
        set => Set(ref this.notesDraft, value);
    }

    public string Status
    {
        get => this.status;
        private set => Set(ref this.status, value);
    }

    public int PendingChanges
    {
        get => this.pendingChanges;
        private set => Set(ref this.pendingChanges, value);
    }

    public bool SimulateNoSignal
    {
        get => this.gate.SimulateNoSignal;
        set
        {
            if (this.gate.SimulateNoSignal != value)
            {
                this.gate.SimulateNoSignal = value;
                Status = value ? "No signal (simulated). Edits are saved locally." : "Signal restored. Press Sync.";
            }
        }
    }

    public async Task InitializeAsync()
    {
        await this.store.InitializeAsync();
        await SyncAsync();
    }

    private async Task SaveAsync()
    {
        if (SelectedZone is null)
        {
            return;
        }

        await this.store.SaveNotesAsync(SelectedZone.Id, NotesDraft);
        await RefreshAsync();
        Status = "Saved locally. Waiting to sync.";
    }

    private async Task SyncAsync()
    {
        SyncOutcome outcome = await this.store.SyncAsync();
        await RefreshAsync();
        Status = outcome.State switch
        {
            SyncState.Synced => "Synced with the server.",
            SyncState.Offline => "Server unreachable. Edits stay queued on this device.",
            _ => $"Conflict: the server rejected {outcome.ConflictedOperations} change(s). Datasync's default keeps them queued and blocks pulls.",
        };
    }

    private async Task RefreshAsync()
    {
        string? selectedId = SelectedZone?.Id;
        List<Zone> zones = await this.store.GetZonesAsync();
        Zones.Clear();
        foreach (Zone zone in zones)
        {
            Zones.Add(new ZoneRow(zone));
        }

        SelectedZone = Zones.FirstOrDefault(z => z.Id == selectedId) ?? Zones.FirstOrDefault();
        PendingChanges = await this.store.CountPendingAsync();
    }
}
