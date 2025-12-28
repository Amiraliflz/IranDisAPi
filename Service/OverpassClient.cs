using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace IranDistanceApi.Services;

public sealed class OverpassClient
{
    private readonly HttpClient _http;

    public OverpassClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Fetch nearby places (city/town/village) around a coordinate.
    /// This is what you want for "cities nearby a main city".
    /// </summary>
    public async Task<IReadOnlyList<Place>> GetPlacesAroundAsync(
        double centerLat,
        double centerLon,
        double radiusKm,
        int limit,
        CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);

        // radius bounds: 1km .. 200km
        var radiusM = (int)Math.Clamp(radiusKm * 1000.0, 1000, 200_000);

        var latStr = centerLat.ToString(CultureInfo.InvariantCulture);
        var lonStr = centerLon.ToString(CultureInfo.InvariantCulture);

        // Overpass QL: request cities/towns/villages around a point
        var ql = $@"
[out:json][timeout:25];
(
  node[""place""~""city|town|village""](around:{radiusM},{latStr},{lonStr});
);
out {limit};
";

        var json = await PostOverpassAsync(ql, ct);
        var places = ParsePlaces(json);

        // Deduplicate by normalized name; keep first
        return places
            .GroupBy(p => NormalizeFa(p.NameFa))
            .Select(g => g.First())
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Optional: Fetch places inside a province (best-effort).
    /// Uses Overpass area by name. Works sometimes; OSM naming varies.
    /// Keep only if you still need province mode.
    /// </summary>
    public async Task<IReadOnlyList<Place>> GetPlacesInProvinceAsync(
        string provinceNameFa,
        int limit,
        CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 200);

        // Try to build an area from an admin boundary named like the province.
        // This is best-effort because OSM relations/names may vary.
        var prov = EscapeOverpassString(NormalizeFa(provinceNameFa));

        var ql = $@"
[out:json][timeout:25];
area[""name""~""{prov}"" i][""boundary""=""administrative""]->.a;
(
  node[""place""~""city|town|village""](area.a);
);
out {limit};
";

        var json = await PostOverpassAsync(ql, ct);
        var places = ParsePlaces(json);

        return places
            .GroupBy(p => NormalizeFa(p.NameFa))
            .Select(g => g.First())
            .Take(limit)
            .ToList();
    }

    // ----------------------------
    // HTTP with retry/backoff
    // ----------------------------

    private async Task<string> PostOverpassAsync(string overpassQl, CancellationToken ct)
    {
        const int maxAttempts = 4;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var content = new StringContent(overpassQl, Encoding.UTF8, "application/x-www-form-urlencoded");

            using var resp = await _http.PostAsync("/api/interpreter", content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
                return body;

            var code = (int)resp.StatusCode;

            var retryable =
                resp.StatusCode == (HttpStatusCode)429 ||
                resp.StatusCode == HttpStatusCode.RequestTimeout ||
                (code >= 500 && code <= 599);

            if (!retryable || attempt == maxAttempts)
            {
                throw new Exception($"Overpass error {code} {resp.ReasonPhrase}. Body: {Trim(body, 1200)}");
            }

            // Backoff: 800ms, 1600ms, 3200ms
            var delayMs = (int)(800 * Math.Pow(2, attempt - 1));
            await Task.Delay(delayMs, ct);
        }

        throw new Exception("Overpass failed unexpectedly.");
    }

    // ----------------------------
    // Parsing
    // ----------------------------

    private static IReadOnlyList<Place> ParsePlaces(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("elements", out var els) || els.ValueKind != JsonValueKind.Array)
            return Array.Empty<Place>();

        var list = new List<Place>();

        foreach (var el in els.EnumerateArray())
        {
            // Only nodes supported here
            if (!el.TryGetProperty("lat", out var latEl)) continue;
            if (!el.TryGetProperty("lon", out var lonEl)) continue;

            var lat = latEl.GetDouble();
            var lon = lonEl.GetDouble();

            string name = "نام‌نامشخص";

            if (el.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            {
                // Prefer Persian names if present
                if (tags.TryGetProperty("name:fa", out var faEl))
                    name = faEl.GetString() ?? name;
                else if (tags.TryGetProperty("name", out var nEl))
                    name = nEl.GetString() ?? name;
            }

            name = NormalizeFa(name);

            // Avoid empty names
            if (string.IsNullOrWhiteSpace(name))
                name = "نام‌نامشخص";

            list.Add(new Place(name, lat, lon));
        }

        return list;
    }

    // ----------------------------
    // Utilities
    // ----------------------------

    private static string NormalizeFa(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";

        return s
            .Replace("ي", "ی")
            .Replace("ك", "ک")
            .Replace("‌", " ")
            .Replace("ـ", "")
            .Replace("  ", " ")
            .Trim();
    }


    private static string EscapeOverpassString(string s)
    {
        // Overpass regex is sensitive; escape quotes and backslashes
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string Trim(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
