using IranDistanceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace IranDistanceApi.Controllers;

[ApiController]
[Route("api/distance")]
public class DistanceController : ControllerBase
{
    private readonly NominatimClient _nominatim;
    private readonly OverpassClient _overpass;
    private readonly DistanceEngine _engine;

    private const double DefaultRadiusKm = 40.0;

    public DistanceController(NominatimClient nominatim, OverpassClient overpass, DistanceEngine engine)
    {
        _nominatim = nominatim;
        _overpass = overpass;
        _engine = engine;
    }

    public sealed record CalcRequest(string Origin, string Destination, int? LimitEach);

    [HttpPost("calc")]
    public async Task<IActionResult> Calc([FromBody] CalcRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Origin))
            return BadRequest(new { error = "OriginRequired" });

        if (string.IsNullOrWhiteSpace(req.Destination))
            return BadRequest(new { error = "DestinationRequired" });

        var limit = (req.LimitEach.HasValue && req.LimitEach.Value > 0) ? req.LimitEach.Value : 5;
        limit = Math.Clamp(limit, 1, 50);

        // Main cities
        var o = await _nominatim.GeocodeCityInIranAsync(req.Origin, ct);
        if (o is null) return BadRequest(new { error = "OriginNotFound", origin = req.Origin });

        var d = await _nominatim.GeocodeCityInIranAsync(req.Destination, ct);
        if (d is null) return BadRequest(new { error = "DestinationNotFound", destination = req.Destination });

        var originMain = new DistanceEngine.MainCity(o.Value.NameFa, o.Value.Lat, o.Value.Lon);
        var destMain = new DistanceEngine.MainCity(d.Value.NameFa, d.Value.Lat, d.Value.Lon);

        // Nearby cities around each main city
        var originNearby = await _overpass.GetPlacesAroundAsync(originMain.Lat, originMain.Lon, DefaultRadiusKm, limit, ct);
        var destNearby = await _overpass.GetPlacesAroundAsync(destMain.Lat, destMain.Lon, DefaultRadiusKm, limit, ct);

        var outOrigin = new Dictionary<string, string>();
        var outDest = new Dictionary<string, string>();

        foreach (var p in originNearby
                     .Where(x => !string.IsNullOrWhiteSpace(x.NameFa))
                     .DistinctBy(x => NormalizeSimple(x.NameFa))
                     .Take(limit))
        {
            var city = new DistanceEngine.CityPoint(p.NameFa, p.Lat, p.Lon);

            try
            {
                var km = await _engine.MainToCityKmAsync(originMain, city, ct);
                outOrigin[p.NameFa] = $"{Math.Round(km)} کیلومتر";
            }
            catch (Exception ex)
            {
                outOrigin[p.NameFa] = $"خطا: {ex.Message}";
            }
        }

        foreach (var p in destNearby
                     .Where(x => !string.IsNullOrWhiteSpace(x.NameFa))
                     .DistinctBy(x => NormalizeSimple(x.NameFa))
                     .Take(limit))
        {
            var city = new DistanceEngine.CityPoint(p.NameFa, p.Lat, p.Lon);

            try
            {
                var km = await _engine.MainToCityKmAsync(destMain, city, ct);
                outDest[p.NameFa] = $"{Math.Round(km)} کیلومتر";
            }
            catch (Exception ex)
            {
                outDest[p.NameFa] = $"خطا: {ex.Message}";
            }
        }

        return Ok(new Dictionary<string, object>
        {
            [originMain.NameFa] = outOrigin,
            [destMain.NameFa] = outDest
        });
    }

    private static string NormalizeSimple(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return s.Replace("ي", "ی").Replace("ك", "ک").Replace("‌", " ").Trim();
    }
}
