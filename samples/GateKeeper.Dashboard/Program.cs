using GateKeeper.Dashboard;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
Uri server = new(builder.Configuration["Server"] ?? "http://localhost:5180/");
WebApplication app = GateKeeperDashboardApp.Build(args, server);
await app.RunAsync();
