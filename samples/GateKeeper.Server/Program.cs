using GateKeeper.Server;

WebApplication app = GateKeeperServerApp.Build(args);
await app.InitializeDatabaseAsync();
await app.RunAsync();
