using IranDistanceApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace IranDistanceApi.Controllers;

[ApiController]
[Route("api/geo")]
public class GeoController : ControllerBase
{
    private readonly JsonDataStore _store;
    private readonly NominatimClient _nominatim;

    public GeoController(JsonDataStore store, NominatimClient nominatim)
    {
        _store = store;
        _nominatim = nominatim;
    }

    [HttpGet("provinces")]
    public IActionResult Provinces([FromQuery] string? query)
    {
        var q = (query ?? "").Trim();
        var res = _store.Provinces
            .Where(p => string.IsNullOrEmpty(q) || p.NameFa.Contains(q))
            .Select(p => new { id = p.Id, nameFa = p.NameFa })
            .ToList();
        return Ok(res);
    }

    // ✅ THIS is what you need to search ALL cities in Tehran/Isfahan
    // Example: /api/geo/cities?provinceId=tehran&query=پر
    [HttpGet("cities")]
    public async Task<IActionResult> Cities([FromQuery] string provinceId, [FromQuery] string query, CancellationToken ct)
    {
        var p = _store.Provinces.SingleOrDefault(x => x.Id == provinceId);
        if (p is null)
            return BadRequest(new { error = "InvalidProvinceId", provinceId });

        var results = await _nominatim.SearchCitiesInProvinceAsync(
            p.NameFa,
            p.Id,
            query,
            20,
            ct
        );
        return Ok(results.Select(r => new
        {
            placeId = r.PlaceId,
            nameFa = r.NameFa,
            lat = r.Lat,
            lon = r.Lon,
            provinceNameFa = r.ProvinceNameFa,
            displayName = r.DisplayName
        }));
    }
}
