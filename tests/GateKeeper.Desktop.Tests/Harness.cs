using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using GateKeeper.Desktop.Data;
using GateKeeper.Desktop.ViewModels;
using GateKeeper.Desktop.Views;
using GateKeeper.Server;
using Microsoft.AspNetCore.Builder;

namespace GateKeeper.Desktop.Tests;

/// <summary>The real server on a real socket, with a real SQLite file. Stopping it really refuses connections.</summary>
public sealed class ServerProcess : IAsyncDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("leeway-e2e-").FullName;
    private readonly int port = FreePort();
    private WebApplication? app;

    public Uri BaseAddress => new($"http://127.0.0.1:{this.port}/");

    public string TempFile(string name) => Path.Combine(this.directory, name);

    public async Task StartAsync()
    {
        this.app = GateKeeperServerApp.Build([], $"Data Source={TempFile("server.db")};Pooling=False", BaseAddress.ToString());
        await this.app.InitializeDatabaseAsync();
        await this.app.StartAsync();
    }

    public async Task StopAsync()
    {
        if (this.app is not null)
        {
            await this.app.StopAsync();
            await this.app.DisposeAsync();
            this.app = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        try
        {
            Directory.Delete(this.directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp directory cleanup is best effort.
        }
    }

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>
/// Models a very poor signal: the request fails the way an HttpClient timeout does, before reaching the server.
/// The real timeout path is pinned by probes C24 and C25. Using a generous real timeout here means that a loaded
/// test machine cannot time out after the server applied a write, which is C25's separate behaviour.
/// </summary>
public sealed class PoorSignal : DelegatingHandler
{
    public bool Hang { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Hang)
        {
            await Task.Delay(50, cancellationToken);
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>One gate: the real window and view model, a local SQLite file, and its own HTTP client.</summary>
public sealed class GateApp
{
    public GateApp(ServerProcess server, string name)
    {
        Signal = new PoorSignal { InnerHandler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3), PooledConnectionLifetime = TimeSpan.Zero } };
        Gate = new ConnectivityGate { InnerHandler = Signal };
        HttpClient http = new(Gate) { BaseAddress = server.BaseAddress, Timeout = TimeSpan.FromSeconds(60) };
        ViewModel = new MainViewModel(new GateStore($"Data Source={server.TempFile(name + ".db")};Pooling=False", http), Gate);
        Window = new MainWindow { DataContext = ViewModel };
        Window.Show();
    }

    public ConnectivityGate Gate { get; }

    public PoorSignal Signal { get; }

    public MainViewModel ViewModel { get; }

    public MainWindow Window { get; }

    public T Find<T>(string name) where T : Control => Window.FindControl<T>(name) ?? throw new InvalidOperationException($"No control named {name}");

    public string Status => Find<TextBlock>("StatusText").Text ?? string.Empty;

    public string PendingLine => Find<TextBlock>("PendingText").Text ?? string.Empty;

    public string[] ZoneLines => ViewModel.Zones.Select(z => z.Summary).ToArray();

    /// <summary>Selects a zone by clicking its row, as a user would.</summary>
    public void SelectZone(string zoneId)
    {
        ZoneRow row = ViewModel.Zones.Single(z => z.Id == zoneId);
        ListBox list = Find<ListBox>("ZoneList");
        list.SelectedItem = row;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Types into the notes box with real key input, then clicks Save.</summary>
    public async Task TypeNotesAndSaveAsync(string notes)
    {
        TextBox box = Find<TextBox>("NotesBox");
        box.Focus();
        box.SelectAll();
        Dispatcher.UIThread.RunJobs();
        Window.KeyTextInput(notes);
        Dispatcher.UIThread.RunJobs();
        await ClickAsync(Find<Button>("SaveButton"), ViewModel.SaveCommand);
    }

    public Task ClickSyncAsync() => ClickAsync(Find<Button>("SyncButton"), ViewModel.SyncCommand);

    private async Task ClickAsync(Button button, AsyncCommand command)
    {
        Assert.True(button.IsEffectivelyEnabled);
        Assert.Same(command, button.Command);

        // A real pointer click on the button's centre. AsyncCommand runs on the UI thread, so pump until it finishes.
        Point centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), Window)
            ?? throw new InvalidOperationException("Button is not in the window.");
        Window.MouseDown(centre, MouseButton.Left);
        Window.MouseUp(centre, MouseButton.Left);
        await PumpUntilAsync(() => command.CanExecute(null));
    }

    public static async Task PumpUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        while (!condition() && DateTime.UtcNow < deadline);
        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), "Timed out waiting for the UI.");
    }

    /// <summary>Saves what the window currently shows, for the milestone walkthrough.</summary>
    public void Capture(string fileName)
    {
        string? directory = Environment.GetEnvironmentVariable("LEEWAY_CAPTURE_DIR");
        if (directory is null)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Window.CaptureRenderedFrame()?.Save(Path.Combine(directory, fileName), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
