using System.Globalization;
using System.Net;
using System.Text.Json;

namespace IranDistanceApi.Services;

public sealed class OsrmClient
{
    private readonly HttpClient _http;

    public OsrmClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<double> RouteKmAsync(
        double fromLat, double fromLon,
        double toLat, double toLon,
        CancellationToken ct)
    {
        var r = await RouteInternalAsync(fromLat, fromLon, toLat, toLon, overview: "false", ct);
        return r.DistanceMeters / 1000.0;
    }

    // ✅ FIXED: return double[][] (not a 1-element tuple)
    public async Task<double[][]> RouteGeoJsonAsync(
        double fromLat, double fromLon,
        double toLat, double toLon,
        CancellationToken ct)
    {
        var r = await RouteInternalAsync(fromLat, fromLon, toLat, toLon, overview: "full", ct);

        if (r.GeometryLonLat is null || r.GeometryLonLat.Length < 2)
            throw new Exception("OSRM route geometry missing/empty (need overview=full & geometries=geojson).");

        return r.GeometryLonLat;
    }

    private sealed record RouteParsed(double DistanceMeters, double[][]? GeometryLonLat);

    private async Task<RouteParsed> RouteInternalAsync(
        double fromLat, double fromLon,
        double toLat, double toLon,
        string overview,
        CancellationToken ct)
    {
        var fromLonStr = fromLon.ToString(CultureInfo.InvariantCulture);
        var fromLatStr = fromLat.ToString(CultureInfo.InvariantCulture);
        var toLonStr = toLon.ToString(CultureInfo.InvariantCulture);
        var toLatStr = toLat.ToString(CultureInfo.InvariantCulture);

        var url =
            $"/route/v1/driving/{fromLonStr},{fromLatStr};{toLonStr},{toLatStr}" +
            $"?alternatives=false&steps=false&geometries=geojson&overview={overview}";

        var json = await GetStringWithPolicyAsync(url, ct);
        return ParseRoute(json);
    }

    private static RouteParsed ParseRoute(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var code = root.TryGetProperty("code", out var codeEl) ? (codeEl.GetString() ?? "") : "";
        if (!string.Equals(code, "Ok", StringComparison.OrdinalIgnoreCase))
        {
            var msg = root.TryGetProperty("message", out var msgEl) ? (msgEl.GetString() ?? "") : "";
            throw new Exception($"OSRM returned code='{code}'. message='{msg}'");
        }

        if (!root.TryGetProperty("routes", out var routesEl) ||
            routesEl.ValueKind != JsonValueKind.Array ||
            routesEl.GetArrayLength() == 0)
            throw new Exception("OSRM response missing routes[0].");

        var r0 = routesEl[0];

        if (!r0.TryGetProperty("distance", out var distEl))
            throw new Exception("OSRM response missing routes[0].distance.");

        var distanceMeters = distEl.GetDouble();

        double[][]? coords = null;

        if (r0.TryGetProperty("geometry", out var geomEl) &&
            geomEl.ValueKind == JsonValueKind.Object &&
            geomEl.TryGetProperty("coordinates", out var coordEl) &&
            coordEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<double[]>(coordEl.GetArrayLength());

            foreach (var pt in coordEl.EnumerateArray())
            {
                if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() < 2)
                    continue;

                var lon = pt[0].GetDouble();
                var lat = pt[1].GetDouble();
                list.Add(new[] { lon, lat });
            }

            coords = list.ToArray();
        }

        return new RouteParsed(distanceMeters, coords);
    }

    private async Task<string> GetStringWithPolicyAsync(string relativeUrl, CancellationToken ct)
    {
        const int maxAttempts = 4;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var resp = await _http.GetAsync(relativeUrl, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
                return body;

            var code = (int)resp.StatusCode;

            var retryable =
                resp.StatusCode == (HttpStatusCode)429 ||
                resp.StatusCode == HttpStatusCode.RequestTimeout ||
                (code >= 500 && code <= 599);

            if (!retryable || attempt == maxAttempts)
                throw new Exception($"OSRM error {code} {resp.ReasonPhrase}. Body: {Trim(body, 1000)}");

            var delayMs = (int)(400 * Math.Pow(2, attempt - 1));
            await Task.Delay(delayMs, ct);
        }

        throw new Exception("OSRM failed unexpectedly.");
    }

    private static string Trim(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
