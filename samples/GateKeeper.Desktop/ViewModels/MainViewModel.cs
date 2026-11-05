using System.Collections.ObjectModel;
using Avalonia.Media;
using GateKeeper.Desktop.Data;
using Leeway.Core;
using Leeway.Datasync.Client;

namespace GateKeeper.Desktop.ViewModels;

public sealed class ZoneRow(Zone zone)
{
    public string Id { get; } = zone.Id;

    public string Name { get; } = zone.Name;

    public int Capacity { get; } = zone.Capacity;

    public int Remaining { get; } = zone.RemainingCapacity;

    public string? Notes { get; } = zone.Notes;

    public string Summary
    {
        get
        {
            string[] flags = GateService.ParseSet(zone.Flags);
            List<string> parts = [$"{Name}: {Remaining} of {Capacity} left, {zone.CheckIns} in"];
            if (!string.IsNullOrEmpty(Notes))
            {
                parts.Add(Notes);
            }

            if (flags.Length > 0)
            {
                parts.Add("Flags: " + string.Join(", ", flags));
            }

            return string.Join(" | ", parts);
        }
    }
}

public sealed class MainViewModel : ViewModelBase
{
    private static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#E8E7E3"));
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#BFE8CF"));
    private static readonly IBrush Caution = new SolidColorBrush(Color.Parse("#F8E2A8"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#F5C2BE"));

    private readonly GateService gate;
    private readonly ConnectivityGate connectivity;
    private ZoneRow? selectedZone;
    private string? ticketCode;
    private string? notesDraft;
    private string? flagDraft;
    private string status = "Starting";
    private string walletLine = "Lease: unknown";
    private string lastScan = "Scan a ticket";
    private IBrush lastScanBrush = Neutral;

    public MainViewModel(GateService gate, ConnectivityGate connectivity)
    {
        this.gate = gate;
        this.connectivity = connectivity;
        ScanCommand = new AsyncCommand(ScanAsync);
        SyncCommand = new AsyncCommand(SyncAsync);
        RequestLeaseCommand = new AsyncCommand(RequestLeaseAsync);
        SaveNotesCommand = new AsyncCommand(SaveNotesAsync);
        AddFlagCommand = new AsyncCommand(AddFlagAsync);
    }

    public string GateName => this.gate.GateName;

    public ObservableCollection<ZoneRow> Zones { get; } = [];

    public ObservableCollection<string> ScanLines { get; } = [];

    public ObservableCollection<string> DuplicateLines { get; } = [];

    public AsyncCommand ScanCommand { get; }

    public AsyncCommand SyncCommand { get; }

    public AsyncCommand RequestLeaseCommand { get; }

    public AsyncCommand SaveNotesCommand { get; }

    public AsyncCommand AddFlagCommand { get; }

    public ZoneRow? SelectedZone
    {
        get => this.selectedZone;
        set
        {
            if (Set(ref this.selectedZone, value) && value is not null)
            {
                NotesDraft = value.Notes;
                _ = RefreshWalletAsync();
            }
        }
    }

    public string? TicketCode
    {
        get => this.ticketCode;
        set => Set(ref this.ticketCode, value);
    }

    public string? NotesDraft
    {
        get => this.notesDraft;
        set => Set(ref this.notesDraft, value);
    }

    public string? FlagDraft
    {
        get => this.flagDraft;
        set => Set(ref this.flagDraft, value);
    }

    public string Status
    {
        get => this.status;
        private set => Set(ref this.status, value);
    }

    public string WalletLine
    {
        get => this.walletLine;
        private set => Set(ref this.walletLine, value);
    }

    public string LastScan
    {
        get => this.lastScan;
        private set => Set(ref this.lastScan, value);
    }

    public IBrush LastScanBrush
    {
        get => this.lastScanBrush;
        private set => Set(ref this.lastScanBrush, value);
    }

    public bool SimulateNoSignal
    {
        get => this.connectivity.SimulateNoSignal;
        set
        {
            if (this.connectivity.SimulateNoSignal != value)
            {
                this.connectivity.SimulateNoSignal = value;
                Status = value ? "No signal (simulated). Scans are saved here." : "Signal restored. Press Sync.";
            }
        }
    }

    /// <summary>First sync, then a lease on each zone, so that this gate can work offline from then on.</summary>
    public async Task InitializeAsync()
    {
        await this.gate.InitializeAsync();
        SyncReport first = await this.gate.SyncAsync();
        if (first.State == SyncState.Synced)
        {
            foreach (Zone zone in await this.gate.GetZonesAsync())
            {
                if ((await this.gate.GetWalletAsync(zone.Id)).Granted == 0)
                {
                    await this.gate.RequestLeaseAsync(zone.Id);
                }
            }
        }

        await SyncAsync();
    }

    private async Task ScanAsync()
    {
        string code = (TicketCode ?? string.Empty).Trim();
        if (SelectedZone is null || code.Length == 0)
        {
            return;
        }

        ScanResult result = await this.gate.ScanAsync(SelectedZone.Id, code);
        (LastScan, LastScanBrush) = result.Outcome switch
        {
            ScanOutcome.Admitted => ($"ADMIT {code}: guaranteed, within this gate's lease.", Good),
            ScanOutcome.Provisional => ($"PROVISIONAL {code}: beyond the lease. The server decides on reconnect.", Caution),
            ScanOutcome.AlreadyScanned => ($"REFUSED: already scanned at {result.ScannedAt}.", Bad),
            ScanOutcome.WrongZone => ("REFUSED: this ticket is for another zone.", Bad),
            _ => ("REFUSED: unknown ticket.", Bad),
        };
        TicketCode = null;
        await RefreshAsync();
    }

    private async Task SyncAsync()
    {
        SyncReport report = await this.gate.SyncAsync();
        await RefreshAsync();
        Status = report.State switch
        {
            SyncState.Synced => "Synced with the server.",
            SyncState.Offline => "Server unreachable. Scans are safe here, and will send on the next sync.",
            _ => $"The server refused {report.RejectedOperations} operation(s). This needs attention.",
        };

        // A scan's banner is only true until the server has answered. The list below it shows how each one ended.
        if (report.State == SyncState.Synced)
        {
            LastScan = "Scan a ticket";
            LastScanBrush = Neutral;
        }
    }

    private async Task RequestLeaseAsync()
    {
        if (SelectedZone is not null)
        {
            await this.gate.RequestLeaseAsync(SelectedZone.Id);
            Status = "Lease requested. It arrives on the next sync.";
        }
    }

    private async Task SaveNotesAsync()
    {
        if (SelectedZone is not null)
        {
            await this.gate.SaveNotesAsync(SelectedZone.Id, NotesDraft);
            await RefreshAsync();
        }
    }

    private async Task AddFlagAsync()
    {
        string flag = (FlagDraft ?? string.Empty).Trim();
        if (SelectedZone is not null && flag.Length > 0)
        {
            await this.gate.AddFlagAsync(SelectedZone.Id, flag);
            FlagDraft = null;
            await RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        string? selectedId = SelectedZone?.Id;
        List<Zone> zones = await this.gate.GetZonesAsync();
        Zones.Clear();
        foreach (Zone zone in zones)
        {
            Zones.Add(new ZoneRow(zone));
        }

        this.selectedZone = null;
        SelectedZone = Zones.FirstOrDefault(z => z.Id == selectedId) ?? Zones.FirstOrDefault();
        await RefreshWalletAsync();

        ScanLines.Clear();
        foreach (GateScan scan in (await this.gate.GetScansAsync()).Take(8))
        {
            string state = scan.State switch
            {
                ScanState.Admitted => "admitted",
                ScanState.Provisional => "provisional",
                _ => "turned away",
            };
            ScanLines.Add($"{scan.TicketId}: {state}");
        }

        DuplicateLines.Clear();
        foreach (DuplicateTicket duplicate in await this.gate.GetDuplicatesAsync())
        {
            DuplicateLines.Add($"{duplicate.TicketId}: {string.Join(", ", duplicate.Gates)}");
        }
    }

    private async Task RefreshWalletAsync()
    {
        if (SelectedZone is { } zone)
        {
            Wallet wallet = await this.gate.GetWalletAsync(zone.Id);
            WalletLine = $"Lease: {wallet.Available} left";
        }
    }
}
