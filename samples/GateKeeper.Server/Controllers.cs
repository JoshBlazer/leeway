using Leeway.Datasync.Server;
using Microsoft.AspNetCore.Mvc;

namespace GateKeeper.Server;

[Route("tables/leewayoperations")]
public class OperationsController(GateKeeperDbContext db, LeewayAuthority<GateKeeperDbContext> authority) : LeewayOperationsController<GateKeeperDbContext>(db, authority);

[Route("tables/leewayleases")]
public class LeasesController(GateKeeperDbContext db, LeewayAuthority<GateKeeperDbContext> authority) : LeewayLeasesController<GateKeeperDbContext>(db, authority);

[Route("tables/zone")]
public class ZonesController(GateKeeperDbContext db, LeewayAuthority<GateKeeperDbContext> authority) : LeewayEntityController<Zone, GateKeeperDbContext>(db, authority);

[Route("tables/ticket")]
public class TicketsController(GateKeeperDbContext db, LeewayAuthority<GateKeeperDbContext> authority) : LeewayEntityController<Ticket, GateKeeperDbContext>(db, authority);
