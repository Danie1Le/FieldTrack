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
    /// Asset, or an Asset of another type. Nothing is changed for that Device.
    /// </exception>
    public static async Task SeedAsync(FieldTrackApi api, FleetFile fleet, TextWriter output, CancellationToken cancellationToken)
    {
        var assets = (await api.ListAssetsAsync(cancellationToken)).ToList();
        var devices = (await api.ListDevicesAsync(cancellationToken)).ToDictionary(device => device.SerialNumber);

        foreach (var wanted in fleet.Devices)
        {
            var deviceCreated = !devices.TryGetValue(wanted.Serial, out var device);
            if (device is null)
            {
                device = await api.CreateDeviceAsync(wanted.Serial, cancellationToken);
                devices[wanted.Serial] = device;
            }
            else if (device.Archived)
            {
                throw new SimulatorException($"Device {wanted.Serial} is Archived; its serial number stays reserved, so use another serial.");
            }

            var open = deviceCreated ? null : (await api.ListAttachmentsAsync(device.Id, cancellationToken)).FirstOrDefault(attachment => attachment.DetachedAt is null);
            if (open is not null)
            {
                // Checked before anything is created, so a conflicting file leaves no stray Asset behind.
                var current = assets.Find(candidate => candidate.Id == open.AssetId);
                if (current?.Name != wanted.AssetName)
                {
                    throw new SimulatorException($"Device {wanted.Serial} is attached to Asset '{current?.Name ?? open.AssetId.ToString()}', not '{wanted.AssetName}'. Detach it first.");
                }

                CheckType(current, wanted);
                output.WriteLine($"{wanted.Serial}: asset found, device found, already attached");
                continue;
            }

            var asset = assets.Find(candidate => candidate.Name == wanted.AssetName);
            var assetCreated = asset is null;
            if (asset is not null)
            {
                CheckType(asset, wanted);
            }
            else
            {
                asset = await api.CreateAssetAsync(wanted.AssetName, wanted.AssetType, cancellationToken);
                assets.Add(asset);
            }

            await api.AttachAsync(device.Id, asset.Id, cancellationToken);
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
