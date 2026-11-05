using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GateKeeper.Desktop.Data;
using GateKeeper.Desktop.ViewModels;
using GateKeeper.Desktop.Views;

namespace GateKeeper.Desktop;

public partial class App : Application
{
    /// <summary>Server address. Override with --server=http://host:port.</summary>
    public static Uri ServerUri { get; set; } = new("http://localhost:5180/");

    /// <summary>Local database. Override with --db=path.</summary>
    public static string DatabasePath { get; set; } = "gatekeeper-gate.db";

    /// <summary>This gate's name, which is its device ID. Override with --gate=name.</summary>
    public static string GateName { get; set; } = "gate-a";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ConnectivityGate connectivity = new() { InnerHandler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) } };
            HttpClient httpClient = new(connectivity) { BaseAddress = ServerUri, Timeout = TimeSpan.FromSeconds(10) };
            MainViewModel viewModel = new(new GateService(GateName, $"Data Source={DatabasePath}", httpClient), connectivity);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
