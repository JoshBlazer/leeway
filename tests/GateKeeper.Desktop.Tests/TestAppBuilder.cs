using Avalonia;
using Avalonia.Headless;
using GateKeeper.Desktop;

[assembly: AvaloniaTestApplication(typeof(GateKeeper.Desktop.Tests.TestAppBuilder))]

namespace GateKeeper.Desktop.Tests;

/// <summary>
/// Runs a test body on the Avalonia UI thread. Uses HeadlessUnitTestSession directly because the
/// Avalonia.Headless.XUnit adapter is binary-incompatible with xUnit v3 4.x, see ADR-0010.
/// </summary>
public static class Ui
{
    private static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

    public static Task Run(Func<Task> test) => Session.Dispatch(
        async () =>
        {
            await test();
            return true;
        },
        CancellationToken.None);
}

public static class TestAppBuilder
{
    // Real Skia drawing rather than headless stubs, so tests can capture what the window really shows.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
