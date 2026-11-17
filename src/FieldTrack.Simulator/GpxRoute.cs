using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace FieldTrack.Simulator;

/// <summary>
/// A route drawn by hand and committed as GPX, so there is no map-data licence to honour (#16). A Device follows
/// it at a steady speed and, past the last point, starts again from the first; draw closed loops so that start is seamless.
/// </summary>
internal sealed class GpxRoute
{
    private readonly Position[] _points;
    private readonly double[] _metersAtPoint;

    private GpxRoute(Position[] points, double[] metersAtPoint)
    {
        _points = points;
        _metersAtPoint = metersAtPoint;
    }

    /// <summary>The distance along the whole route in metres.</summary>
    public double LengthMeters => _metersAtPoint[^1];

    /// <summary>Reads the GPX file at <paramref name="path"/>.</summary>
    /// <exception cref="SimulatorException">The file is missing or is not a followable route.</exception>
    public static GpxRoute Load(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path), path) : throw new SimulatorException($"Route file not found: {path}");

    /// <summary>Reads <paramref name="gpx"/>, a track or route with at least two points; <paramref name="source"/> names it in errors.</summary>
    /// <exception cref="SimulatorException">The text is not a followable route.</exception>
    public static GpxRoute Parse(string gpx, string source)
    {
        XDocument document;
        try
        {
            // GPX never needs a DTD; refusing one rules out entity expansion from a route file copied from elsewhere.
            using var reader = XmlReader.Create(new StringReader(gpx), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new SimulatorException($"Route {source} is not valid XML: {exception.Message}", exception);
        }

        // Local names, so GPX 1.0 and 1.1 and files without a namespace all read the same.
        var points = document.Descendants()
            .Where(element => element.Name.LocalName is "trkpt" or "rtept")
            .Select(element => ToPosition(element, source))
            .ToArray();
        if (points.Length < 2)
        {
            throw new SimulatorException($"Route {source} needs at least two points.");
        }

        var metersAtPoint = new double[points.Length];
        for (var i = 1; i < points.Length; i++)
        {
            metersAtPoint[i] = metersAtPoint[i - 1] + Geo.Distance(points[i - 1], points[i]);
        }

        return metersAtPoint[^1] > 0 ? new GpxRoute(points, metersAtPoint) : throw new SimulatorException($"Route {source} has no length: all its points are the same.");
    }

    /// <summary>Where a Device is after travelling <paramref name="meters"/> from the first point, going round again past the end.</summary>
    public Position PositionAt(double meters)
    {
        var along = meters % LengthMeters;
        var index = Array.BinarySearch(_metersAtPoint, along);
        if (index >= 0)
        {
            return _points[index];
        }

        var next = ~index;
        var (from, to) = (_points[next - 1], _points[next]);
        var fraction = (along - _metersAtPoint[next - 1]) / (_metersAtPoint[next] - _metersAtPoint[next - 1]);
        return new Position(from.Lat + ((to.Lat - from.Lat) * fraction), from.Lon + ((to.Lon - from.Lon) * fraction));
    }

    private static Position ToPosition(XElement point, string source)
    {
        if (double.TryParse(point.Attribute("lat")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(point.Attribute("lon")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            && lat is >= -90 and <= 90 && lon is >= -180 and <= 180)
        {
            return new Position(lat, lon);
        }

        throw new SimulatorException($"Route {source} has a point without a valid lat and lon: {point}");
    }
}
