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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ConnectivityGate gate = new() { InnerHandler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) } };
            HttpClient httpClient = new(gate) { BaseAddress = ServerUri, Timeout = TimeSpan.FromSeconds(10) };
            GateStore store = new($"Data Source={DatabasePath}", httpClient);
            MainViewModel viewModel = new(store, gate);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
