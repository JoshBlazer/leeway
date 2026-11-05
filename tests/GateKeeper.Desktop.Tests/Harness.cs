extern alias DashboardApp;
extern alias ServerApp;

using System.Diagnostics;
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
using Microsoft.AspNetCore.Builder;
using ServerApp::GateKeeper.Server;

namespace GateKeeper.Desktop.Tests;

/// <summary>The real server on a real socket, with a real SQLite file. Stopping it really refuses connections.</summary>
public sealed class ServerProcess : IAsyncDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("leeway-e2e-").FullName;
    private readonly int port = FreePort();
    private WebApplication? app;
    private WebApplication? dashboard;

    public Uri BaseAddress => new($"http://127.0.0.1:{this.port}/");

    public Uri DashboardAddress => new(this.dashboard!.Urls.First() + "/");

    public string TempFile(string name) => Path.Combine(this.directory, name);

    /// <summary>
    /// A restart must reuse the port, so the port is reserved up front. Another process can take it
    /// in the gap, or the OS may not have freed it yet, so retry a few times.
    /// </summary>
    public async Task StartAsync()
    {
        for (int attempt = 1; ; attempt++)
        {
            this.app = GateKeeperServerApp.Build([], $"Data Source={TempFile("server.db")}", BaseAddress.ToString());
            await this.app.InitializeDatabaseAsync();
            try
            {
                await this.app.StartAsync();
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                await this.app.DisposeAsync();
                this.app = null;
                await Task.Delay(500);
            }
        }
    }

    public async Task StartDashboardAsync()
    {
        this.dashboard = DashboardApp::GateKeeper.Dashboard.GateKeeperDashboardApp.Build([], BaseAddress, "http://127.0.0.1:0");
        await this.dashboard.StartAsync();
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

    /// <summary>When asked to, saves both gates and the dashboard, for the README's GIF. Otherwise does nothing.</summary>
    public async Task CaptureStepAsync(GateApp a, GateApp b, string step)
    {
        string? directory = Environment.GetEnvironmentVariable("LEEWAY_CAPTURE_DIR");
        if (directory is null)
        {
            return;
        }

        a.Capture(Path.Combine(directory, $"{step}-a.png"));
        b.Capture(Path.Combine(directory, $"{step}-b.png"));

        string? browser = Environment.GetEnvironmentVariable("LEEWAY_BROWSER");
        if (browser is not null && this.dashboard is not null)
        {
            string profile = TempFile("browser-" + Guid.NewGuid().ToString("N"));
            string screenshot = Path.Combine(directory, $"{step}-dashboard.png");
            using Process process = Process.Start(new ProcessStartInfo(browser)
            {
                ArgumentList =
                {
                    "--headless=new", "--disable-gpu", "--hide-scrollbars", "--window-size=760,640", "--timeout=4000",
                    $"--user-data-dir={profile}", $"--screenshot={screenshot}", DashboardAddress.ToString(),
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Headless browsers can hang. Never leave one behind, or it holds the caller's output pipes open.
            using CancellationTokenSource limit = new(TimeSpan.FromSeconds(45));
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }

            Assert.True(File.Exists(screenshot), $"The browser made no screenshot for {step}.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (this.dashboard is not null)
        {
            await this.dashboard.StopAsync();
            await this.dashboard.DisposeAsync();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
/// The real timeout path is pinned by probes C24 and C25.
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
        ViewModel = new MainViewModel(new GateService(name, $"Data Source={server.TempFile(name + ".db")}", http), Gate);
        Window = new MainWindow { DataContext = ViewModel };
        Window.Show();
    }

    public ConnectivityGate Gate { get; }

    public PoorSignal Signal { get; }

    public MainViewModel ViewModel { get; }

    public MainWindow Window { get; }

    public T Find<T>(string name) where T : Control => Window.FindControl<T>(name) ?? throw new InvalidOperationException($"No control named {name}");

    public string Status => Find<TextBlock>("StatusText").Text ?? string.Empty;

    public string LastScan => Find<TextBlock>("LastScanText").Text ?? string.Empty;

    public string WalletLine => Find<TextBlock>("WalletText").Text ?? string.Empty;

    public string[] ZoneLines => ViewModel.Zones.Select(z => z.Summary).ToArray();

    public string[] ScanLines => [.. ViewModel.ScanLines];

    public string[] DuplicateLines => [.. ViewModel.DuplicateLines];

    /// <summary>Types with real key input, then clicks Scan.</summary>
    public async Task ScanAsync(string ticket)
    {
        Type("TicketBox", ticket);
        await ClickAsync(Find<Button>("ScanButton"), ViewModel.ScanCommand);
    }

    public async Task SaveNotesAsync(string notes)
    {
        Type("NotesBox", notes);
        await ClickAsync(Find<Button>("SaveNotesButton"), ViewModel.SaveNotesCommand);
    }

    public async Task AddFlagAsync(string flag)
    {
        Type("FlagBox", flag);
        await ClickAsync(Find<Button>("AddFlagButton"), ViewModel.AddFlagCommand);
    }

    public Task ClickSyncAsync() => ClickAsync(Find<Button>("SyncButton"), ViewModel.SyncCommand);

    public Task ClickRequestLeaseAsync() => ClickAsync(Find<Button>("RequestLeaseButton"), ViewModel.RequestLeaseCommand);

    private void Type(string boxName, string text)
    {
        TextBox box = Find<TextBox>(boxName);
        box.Focus();
        box.SelectAll();
        Dispatcher.UIThread.RunJobs();
        Window.KeyTextInput(text);
        Dispatcher.UIThread.RunJobs();
    }

    private async Task ClickAsync(Button button, AsyncCommand command)
    {
        Assert.True(button.IsEffectivelyEnabled);
        Assert.Same(command, button.Command);

        // A real pointer click on the button's centre. AsyncCommand runs on the UI thread, so pump until it finishes.
        Dispatcher.UIThread.RunJobs();
        Point centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), Window)
            ?? throw new InvalidOperationException("Button is not in the window.");
        Window.MouseDown(centre, MouseButton.Left);
        Window.MouseUp(centre, MouseButton.Left);
        await PumpUntilAsync(() => command.CanExecute(null));
    }

    public static async Task PumpUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        do
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        while (!condition() && DateTime.UtcNow < deadline);
        Dispatcher.UIThread.RunJobs();
        Assert.True(condition(), "Timed out waiting for the UI.");
    }

    public void Capture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Window.CaptureRenderedFrame()?.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
