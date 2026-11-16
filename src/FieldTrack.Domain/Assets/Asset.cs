namespace FieldTrack.Domain.Assets;

/// <summary>A piece of field equipment being tracked. Its Readings and Alerts refer to it by <see cref="Id"/>.</summary>
public sealed class Asset
{
    /// <summary>Surrogate key.</summary>
    public Guid Id { get; set; }

    /// <summary>The name people know the Asset by, such as "Haul truck 7".</summary>
    public required string Name { get; set; }

    /// <summary>The kind of equipment, which picks the kind letter drawn in its map marker (spec #21).</summary>
    public AssetType Type { get; set; }

    /// <summary>The Asset Group it belongs to, if any; an Asset belongs to at most one.</summary>
    public Guid? AssetGroupId { get; set; }

    /// <summary>
    /// True once the Asset is removed from use but kept because it has history. Archived Assets
    /// are left out of lists unless asked for.
    /// </summary>
    public bool Archived { get; set; }
}
