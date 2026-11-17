namespace FieldTrack.Simulator;

/// <summary>
/// Creates the Assets, Devices and Attachments a fleet file describes through the REST API, so that a Device
/// the simulator publishes for is one ingest accepts. Running it again adds only what is missing.
/// </summary>
internal static class Seeder
{
    /// <summary>
    /// Makes sure every Device in <paramref name="fleet"/> exists and is attached to an Asset with the name and
    /// Asset Type the file gives. Writes one line per Device to <paramref name="output"/>.
    /// </summary>
    /// <exception cref="SimulatorException">
    /// The platform holds something that conflicts with the file: a Device that is Archived or attached to another
    /// Asset, or an Asset of another type or with another Device. Nothing is changed for that Device.
    /// </exception>
    public static async Task SeedAsync(FieldTrackApi api, FleetFile fleet, TextWriter output, CancellationToken cancellationToken)
    {
        var assets = (await api.ListAssetsAsync(cancellationToken)).ToList();
        var devices = (await api.ListDevicesAsync(cancellationToken)).ToList();
        var open = (await api.ListAttachmentsAsync(cancellationToken)).Where(attachment => attachment.DetachedAt is null).ToList();

        foreach (var wanted in fleet.Devices)
        {
            // Every conflict is checked before anything is created, so a refused Device leaves nothing behind.
            var device = devices.Find(candidate => candidate.SerialNumber == wanted.Serial);
            if (device is { Archived: true })
            {
                throw new SimulatorException($"Device {wanted.Serial} is Archived; its serial number stays reserved, so use another serial.");
            }

            if (device is not null && open.Find(attachment => attachment.DeviceId == device.Id) is { } current)
            {
                var currentAsset = assets.Find(candidate => candidate.Id == current.AssetId);
                if (currentAsset?.Name != wanted.AssetName)
                {
                    throw new SimulatorException($"Device {wanted.Serial} is attached to Asset '{currentAsset?.Name ?? current.AssetId.ToString()}', not '{wanted.AssetName}'. Detach it first.");
                }

                CheckType(currentAsset, wanted);
                output.WriteLine($"{wanted.Serial}: asset found, device found, already attached");
                continue;
            }

            var asset = assets.Find(candidate => candidate.Name == wanted.AssetName);
            if (asset is not null)
            {
                CheckType(asset, wanted);
                if (open.Find(attachment => attachment.AssetId == asset.Id) is { } other)
                {
                    var otherSerial = devices.Find(candidate => candidate.Id == other.DeviceId)?.SerialNumber ?? other.DeviceId.ToString();
                    throw new SimulatorException($"Asset '{asset.Name}' already has Device {otherSerial} attached, not {wanted.Serial}. Detach it first.");
                }
            }

            var deviceCreated = device is null;
            if (device is null)
            {
                device = await api.CreateDeviceAsync(wanted.Serial, cancellationToken);
                devices.Add(device);
            }

            var assetCreated = asset is null;
            if (asset is null)
            {
                asset = await api.CreateAssetAsync(wanted.AssetName, wanted.AssetType, cancellationToken);
                assets.Add(asset);
            }

            open.Add(await api.AttachAsync(device.Id, asset.Id, cancellationToken));
            output.WriteLine($"{wanted.Serial}: asset {(assetCreated ? "created" : "found")}, device {(deviceCreated ? "created" : "found")}, attached");
        }
    }

    private static void CheckType(ApiAsset asset, FleetDevice wanted)
    {
        if (asset.Type != wanted.AssetType)
        {
            throw new SimulatorException($"Asset '{asset.Name}' exists as a {asset.Type}, but the fleet file says {wanted.AssetType}.");
        }
    }
}
