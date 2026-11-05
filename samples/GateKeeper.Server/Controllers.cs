using Leeway.Datasync.Server;
using Microsoft.AspNetCore.Mvc;

namespace GateKeeper.Server;

[Route("tables/leewayoperations")]
public class OperationsController(GateKeeperDbContext db, LeewayAuthority<GateKeeperDbContext> authority) : LeewayOperationsController<GateKeeperDbContext>(db, authority);

[Route("tables/leewayleases")]
public class LeasesController(GateKeeperDbContext db) : LeewayLeasesController<GateKeeperDbContext>(db);

[Route("tables/zone")]
public class ZonesController(GateKeeperDbContext db) : LeewayEntityController<Zone, GateKeeperDbContext>(db);

[Route("tables/ticket")]
public class TicketsController(GateKeeperDbContext db) : LeewayEntityController<Ticket, GateKeeperDbContext>(db);
