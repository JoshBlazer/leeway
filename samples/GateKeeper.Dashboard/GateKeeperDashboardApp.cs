using System.Reflection;
using System.Text.Json;

namespace GateKeeper.Dashboard;

/// <summary>A small separate app: serves one page, and fetches the server's state for it.</summary>
public static class GateKeeperDashboardApp
{
    public static WebApplication Build(string[] args, Uri server, string? url = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        if (url is not null)
        {
            builder.WebHost.UseUrls(url);
        }

        WebApplication app = builder.Build();
        HttpClient http = new() { BaseAddress = server, Timeout = TimeSpan.FromSeconds(3) };

        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GateKeeper.Dashboard.dashboard.html")!;
        using StreamReader reader = new(stream);
        string page = reader.ReadToEnd();

        // The last good state, so that a page opened while the server is down still has something to show.
        JsonElement? lastKnown = null;

        async Task<object> GetStateAsync()
        {
            bool serverUp = true;
            try
            {
                using JsonDocument state = JsonDocument.Parse(await http.GetStringAsync("admin/state"));
                lastKnown = state.RootElement.Clone();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                serverUp = false;
            }

            return lastKnown is { } known
                ? new
                {
                    serverUp,
                    zones = known.GetProperty("zones"),
                    duplicates = known.GetProperty("duplicates"),
                    activity = known.GetProperty("activity"),
                }
                : new { serverUp };
        }

        // The page arrives with its first state, so it is right from the first paint.
        app.MapGet("/", async () =>
        {
            string state = JsonSerializer.Serialize(await GetStateAsync()).Replace("<", "\\u003c", StringComparison.Ordinal);
            return Results.Content(page.Replace("/*INITIAL_STATE*/null", state, StringComparison.Ordinal), "text/html");
        });
        app.MapGet("/api/state", async () => Results.Json(await GetStateAsync()));
        return app;
    }
}
