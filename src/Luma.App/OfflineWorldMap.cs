namespace Luma.App;

/// <summary>
/// Simplified offline vector coastlines and Web Mercator projection helpers so Luma renders
/// an interactive minimal coordinate canvas before any external map tile requests occur.
/// </summary>
internal static class OfflineWorldMap
{
    public const double MinLat = -85.0;
    public const double MaxLat = 85.0;

    // Coarse simplified landmass polygons (lat, lon) strictly for offline geographic orientation.
    public static readonly (double Lat, double Lon)[][] Landmasses =
    [
        // North America
        [(70,-162),(71,-156),(69,-135),(68,-110),(62,-95),(60,-78),(53,-56),(45,-62),(41,-70),(35,-75),(25,-80),(28,-83),(29,-90),(26,-97),(19,-96),(21,-87),(15,-84),(9,-79),(8,-83),(15,-93),(20,-105),(32,-117),(38,-123),(48,-125),(58,-137),(60,-149),(55,-162),(64,-166),(70,-162)],
        // Greenland
        [(82,-35),(77,-18),(70,-22),(60,-44),(67,-54),(76,-68),(81,-52),(82,-35)],
        // South America
        [(11,-74),(10,-62),(5,-52),(0,-49),(-5,-35),(-13,-38),(-23,-43),(-34,-53),(-42,-64),(-54,-68),(-52,-74),(-42,-74),(-30,-71),(-18,-71),(-5,-81),(4,-77),(11,-74)],
        // Europe & Asia
        [(36,-9),(43,-9),(44,-2),(48,-5),(51,2),(54,8),(58,6),(63,5),(70,20),(70,40),(69,65),(73,85),(75,110),(72,140),(68,175),(60,164),(53,158),(52,141),(42,131),(35,129),(38,125),(31,122),(22,114),(20,106),(10,104),(2,103),(8,98),(16,96),(22,90),(16,81),(8,77),(23,68),(25,57),(29,48),(22,59),(13,45),(29,33),(31,34),(36,36),(37,27),(41,28),(40,22),(38,15),(44,12),(44,8),(40,0),(36,-6),(36,-9)],
        // Africa
        [(36,-6),(36,10),(32,15),(31,32),(21,37),(12,44),(11,51),(2,45),(-10,40),(-25,33),(-34,25),(-34,18),(-22,14),(-6,12),(4,9),(5,-4),(7,-12),(15,-17),(24,-16),(32,-9),(36,-6)],
        // UK & Ireland
        [(50,-5),(54,-3),(58,-6),(58,-2),(53,1),(51,1),(50,-5)],
        // Japan
        [(31,130),(34,132),(35,139),(38,141),(43,144),(41,140),(36,136),(33,130),(31,130)],
        // Maritime SE Asia
        [(5,95),(1,103),(-6,106),(-8,115),(-2,118),(4,117),(6,100),(5,95)],
        [(18,120),(14,122),(8,126),(7,122),(14,120),(18,120)],
        // Australia
        [(-12,131),(-12,136),(-15,136),(-11,142),(-19,147),(-28,153),(-37,150),(-38,142),(-35,137),(-32,128),(-35,118),(-32,115),(-22,114),(-16,123),(-12,131)],
        // New Zealand
        [(-35,173),(-38,178),(-41,175),(-46,167),(-43,170),(-39,174),(-35,173)],
        // Madagascar
        [(-12,49),(-18,50),(-25,47),(-24,44),(-16,45),(-12,49)]
    ];

    public static double LonToNormX(double lon) => (lon + 180.0) / 360.0;

    public static double LatToNormY(double lat)
    {
        var rad = Math.Clamp(lat, MinLat, MaxLat) * Math.PI / 180.0;
        return 0.5 - Math.Log((1.0 + Math.Sin(rad)) / (1.0 - Math.Sin(rad))) / (4.0 * Math.PI);
    }

    public static double NormXToLon(double normX) => Math.Clamp((normX * 360.0) - 180.0, -180.0, 180.0);

    public static double NormYToLat(double normY)
    {
        var n = Math.PI - (2.0 * Math.PI * Math.Clamp(normY, 0.0, 1.0));
        return Math.Clamp((180.0 / Math.PI) * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n))), MinLat, MaxLat);
    }
}

