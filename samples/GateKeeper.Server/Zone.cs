using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Datasync.Server.EntityFrameworkCore;

namespace GateKeeper.Server;

/// <summary>
/// The M0 spike entity: a venue zone, synced whole-row with no Leeway semantics yet.
/// The repository stamps UpdatedAt and Version.
/// </summary>
public class Zone : RepositoryControlledEntityTableData
{
    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;

    public int Capacity { get; set; }

    public string? Notes { get; set; }
}
