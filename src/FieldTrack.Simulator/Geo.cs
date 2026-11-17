namespace FieldTrack.Simulator;

/// <summary>Spherical-earth geometry, accurate to well under a metre per kilometre: plenty for drawing a map.</summary>
internal static class Geo
{
    private const double EarthRadiusMeters = 6_371_008.8;

    /// <summary>The great-circle distance between two positions in metres.</summary>
    public static double Distance(Position from, Position to)
    {
        var (lat1, lat2) = (double.DegreesToRadians(from.Lat), double.DegreesToRadians(to.Lat));
        var (dLat, dLon) = (lat2 - lat1, double.DegreesToRadians(to.Lon - from.Lon));
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2)) + (Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return 2 * EarthRadiusMeters * Math.Asin(Math.Sqrt(a));
    }

    /// <summary>The direction from <paramref name="from"/> to a nearby <paramref name="to"/> in radians clockwise from north.</summary>
    public static double Bearing(Position from, Position to) =>
        Math.Atan2((to.Lon - from.Lon) * Math.Cos(double.DegreesToRadians(from.Lat)), to.Lat - from.Lat);

    /// <summary>The position <paramref name="meters"/> from <paramref name="from"/> towards <paramref name="bearing"/> (radians clockwise from north).</summary>
    public static Position Offset(Position from, double bearing, double meters)
    {
        var dLat = meters * Math.Cos(bearing) / EarthRadiusMeters;
        var dLon = meters * Math.Sin(bearing) / (EarthRadiusMeters * Math.Cos(double.DegreesToRadians(from.Lat)));
        return new Position(from.Lat + double.RadiansToDegrees(dLat), from.Lon + double.RadiansToDegrees(dLon));
    }
}
