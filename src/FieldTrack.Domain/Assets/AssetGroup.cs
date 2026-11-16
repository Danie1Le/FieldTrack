namespace FieldTrack.Domain.Assets;

/// <summary>
/// A named set of Assets, such as a site or fleet, so that one Alert Rule can cover many Assets.
/// </summary>
public sealed class AssetGroup
{
    /// <summary>Surrogate key.</summary>
    public Guid Id { get; set; }

    /// <summary>The name people know the group by, such as "North quarry".</summary>
    public required string Name { get; set; }
}
