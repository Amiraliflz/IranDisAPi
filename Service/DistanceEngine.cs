namespace IranDistanceApi.Services;

public sealed class DistanceEngine
{
    private readonly OsrmClient _osrm;

    private const double NearKmZero = 10.0; // if you still want <10 => 0

    public DistanceEngine(OsrmClient osrm)
    {
        _osrm = osrm;
    }

    public sealed record CityPoint(string NameFa, double Lat, double Lon);
    public sealed record MainCity(string NameFa, double Lat, double Lon);

    public async Task<double> MainToCityKmAsync(MainCity main, CityPoint city, CancellationToken ct)
    {
        var km = await _osrm.RouteKmAsync(main.Lat, main.Lon, city.Lat, city.Lon, ct);

        // Optional rule: near distances show as 0
        if (km < NearKmZero)
            return 0;

        return km;
    }
}
