using CommunityToolkit.Datasync.Server;
using CommunityToolkit.Datasync.Server.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GateKeeper.Server;

/// <summary>Builds the server so that Program.cs and the end-to-end tests run exactly the same app.</summary>
public static class GateKeeperServerApp
{
    public static WebApplication Build(string[] args, string? connectionString = null, string? url = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        connectionString ??= builder.Configuration.GetConnectionString("GateKeeper") ?? "Data Source=gatekeeper-server.db";
        if (url is not null)
        {
            builder.WebHost.UseUrls(url);
        }

        builder.Services.AddDbContext<GateKeeperDbContext>(options => options.UseSqlite(connectionString));
        builder.Services.AddDatasyncServices();
        builder.Services.AddControllers().AddApplicationPart(typeof(ZoneController).Assembly);

        WebApplication app = builder.Build();
        app.MapControllers();
        return app;
    }

    /// <summary>Creates the schema and seeds two zones the first time the database is used.</summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        using IServiceScope scope = app.Services.CreateScope();
        GateKeeperDbContext context = scope.ServiceProvider.GetRequiredService<GateKeeperDbContext>();
        await context.Database.EnsureCreatedAsync();
        if (!await context.Zones.AnyAsync())
        {
            EntityTableRepository<Zone> repository = new(context);
            await repository.CreateAsync(new Zone { Id = "zone-north", Name = "North Stand", Capacity = 500 });
            await repository.CreateAsync(new Zone { Id = "zone-south", Name = "South Stand", Capacity = 300 });
        }
    }
}
