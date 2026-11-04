namespace GateKeeper.Desktop.Data;

/// <summary>The client's copy of a zone. The class name sets the default endpoint, /tables/zone.</summary>
public class Zone
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? Version { get; set; }
    public bool Deleted { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Capacity { get; set; }
    public string? Notes { get; set; }
}
