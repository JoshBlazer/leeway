using Avalonia;

namespace GateKeeper.Desktop;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg.StartsWith("--server=", StringComparison.Ordinal))
            {
                App.ServerUri = new Uri(arg["--server=".Length..]);
            }
            else if (arg.StartsWith("--db=", StringComparison.Ordinal))
            {
                App.DatabasePath = arg["--db=".Length..];
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
