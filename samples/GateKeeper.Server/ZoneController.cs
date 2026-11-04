using CommunityToolkit.Datasync.Server;
using CommunityToolkit.Datasync.Server.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace GateKeeper.Server;

[Route("tables/zone")]
public class ZoneController : TableController<Zone>
{
    public ZoneController(GateKeeperDbContext context)
        : base(new EntityTableRepository<Zone>(context), new TableControllerOptions { EnableSoftDelete = true })
    {
    }
}
