using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace IranDistanceApi.Services;

public sealed class NominatimClient
{
    private readonly HttpClient _http;

    // Public Nominatim: keep requests slow
    private static readonly SemaphoreSlim _throttleGate = new(1, 1);
    private static DateTime _lastCallUtc = DateTime.MinValue;

    public NominatimClient(HttpClient http)
    {
        _http = http;
    }

    // ------------ Public DTOs ------------

    public record CityCandidate(
        long PlaceId,
        string NameFa,
        double Lat,
        double Lon,
        string ProvinceNameFa,
        string DisplayName,
        string Type
    );

    // ------------ MAIN METHODS (you need now) ------------

    /// <summary>
    /// Geocode a main city (center point) in Iran. Works with Persian/English.
    /// Example: "تهران", "Tehran"
    /// </summary>
    public async Task<(double Lat, double Lon, string NameFa)?> GeocodeCityInIranAsync(
        string cityQuery,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cityQuery))
            return null;

        var q = UrlEncoder.Default.Encode($"{NormalizeText(cityQuery)} ایران");

        var url =
            $"/search?q={q}" +
            $"&format=jsonv2" +
            $"&addressdetails=1" +
            $"&limit=1" +
            $"&countrycodes=ir" +
            $"&accept-language=fa";

        var json = await GetStringWithPolicyAsync(url, ct);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return null;

        var item = doc.RootElement[0];

        if (!TryParseDouble(item, "lat", out var lat)) return null;
        if (!TryParseDouble(item, "lon", out var lon)) return null;

        // Prefer a clean Persian-ish name
        var name = item.TryGetProperty("display_name", out var dn)
            ? (dn.GetString() ?? cityQuery).Split(',')[0].Trim()
            : cityQuery;

        name = NormalizeText(name);

        return (lat, lon, name);
    }

    /// <summary>
    /// Reverse geocode to get province/state for a point in Iran.
    /// Returns address.state (often "Tehran Province" / "استان تهران").
    /// </summary>
    public async Task<string?> ReverseGetStateInIranAsync(double lat, double lon, CancellationToken ct)
    {
        var url =
            $"/reverse?format=jsonv2&addressdetails=1" +
            $"&lat={lat.ToString(CultureInfo.InvariantCulture)}" +
            $"&lon={lon.ToString(CultureInfo.InvariantCulture)}" +
            $"&accept-language=fa";

        var json = await GetStringWithPolicyAsync(url, ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("address", out var addr) || addr.ValueKind != JsonValueKind.Object)
            return null;

        // Ensure Iran
        if (addr.TryGetProperty("country_code", out var cc))
        {
            var ccs = (cc.GetString() ?? "").Trim();
            if (!string.Equals(ccs, "ir", StringComparison.OrdinalIgnoreCase))
                return null;
        }

        if (addr.TryGetProperty("state", out var st))
            return NormalizeText(st.GetString() ?? "");

        return null;
    }

    /// <summary>
    /// Get province boundary GeoJSON for NetTopologySuite.
    /// Uses polygon_geojson=1.
    /// </summary>
    public async Task<string?> GetProvinceGeoJsonAsync(string provinceNameFa, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provinceNameFa))
            return null;

        // Try a few query patterns (OSM naming varies)
        var patterns = new[]
        {
            $"استان {provinceNameFa}",
            $"{provinceNameFa} استان ایران",
            $"{provinceNameFa} Province Iran"
        };

        foreach (var p in patterns)
        {
            var q = UrlEncoder.Default.Encode(NormalizeText(p));

            var url =
                $"/search?q={q}" +
                $"&format=jsonv2" +
                $"&polygon_geojson=1" +
                $"&addressdetails=1" +
                $"&limit=5" +
                $"&countrycodes=ir" +
                $"&accept-language=fa";

            var json = await GetStringWithPolicyAsync(url, ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                continue;

            // Pick first item that has geojson
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("geojson", out var geo))
                    return geo.GetRawText();
            }
        }

        return null;
    }

    // ------------ OPTIONAL (kept for future / fallback) ------------

    /// <summary>
    /// Search cities inside a province (tolerant matching).
    /// Not required for origin/destination-only mode, but useful to keep.
    /// </summary>
    public async Task<IReadOnlyList<CityCandidate>> SearchCitiesInProvinceAsync(
        string provinceNameFa,
        string provinceId,
        string cityQuery,
        int limit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cityQuery))
            return Array.Empty<CityCandidate>();

        limit = Math.Clamp(limit, 1, 20);

        var qCity = NormalizeText(cityQuery);
        var qProvFa = NormalizeText(provinceNameFa);
        var q = UrlEncoder.Default.Encode($"{qCity}, {qProvFa}, ایران");

        var url =
            $"/search?q={q}" +
            $"&format=jsonv2" +
            $"&addressdetails=1" +
            $"&limit={limit}" +
            $"&countrycodes=ir" +
            $"&accept-language=fa";

        var json = await GetStringWithPolicyAsync(url, ct);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<CityCandidate>();

        var results = new List<CityCandidate>();

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
            if (!IsCityLike(type)) continue;

            var state = TryReadAddressField(item, "state");
            var display = item.TryGetProperty("display_name", out var dispEl) ? (dispEl.GetString() ?? "") : "";

            if (!MatchesProvince(state, display, provinceNameFa, provinceId))
                continue;

            if (!item.TryGetProperty("place_id", out var pidEl)) continue;
            var placeId = pidEl.GetInt64();

            if (!TryParseDouble(item, "lat", out var lat)) continue;
            if (!TryParseDouble(item, "lon", out var lon)) continue;

            var nameFa = BestNameFromNominatim(item, fallback: qCity);

            results.Add(new CityCandidate(
                placeId,
                nameFa,
                lat,
                lon,
                provinceNameFa,
                display,
                type
            ));
        }

        return results
            .GroupBy(x => NormalizeText(x.NameFa))
            .Select(g => g.First())
            .Take(limit)
            .ToList();
    }

    // ------------ HTTP: throttle + retry ------------

    private async Task<string> GetStringWithPolicyAsync(string relativeUrl, CancellationToken ct)
    {
        await ThrottleAsync(ct);

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
                throw new Exception($"Nominatim error {code} {resp.ReasonPhrase}. Body: {Trim(body, 900)}");

            // backoff: 500ms, 1000ms, 2000ms
            var delayMs = (int)(500 * Math.Pow(2, attempt - 1));
            await Task.Delay(delayMs, ct);
        }

        throw new Exception("Nominatim failed unexpectedly.");
    }

    private static async Task ThrottleAsync(CancellationToken ct)
    {
        await _throttleGate.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            var minInterval = TimeSpan.FromSeconds(1);
            var elapsed = now - _lastCallUtc;

            if (elapsed < minInterval)
                await Task.Delay(minInterval - elapsed, ct);

            _lastCallUtc = DateTime.UtcNow;
        }
        finally
        {
            _throttleGate.Release();
        }
    }

    // ------------ parsing helpers ------------

    private static bool TryParseDouble(JsonElement item, string prop, out double value)
    {
        value = 0;
        if (!item.TryGetProperty(prop, out var el)) return false;

        var s = el.GetString();
        if (string.IsNullOrWhiteSpace(s)) return false;

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string? TryReadAddressField(JsonElement item, string field)
    {
        if (!item.TryGetProperty("address", out var addr)) return null;
        if (addr.ValueKind != JsonValueKind.Object) return null;
        if (!addr.TryGetProperty(field, out var el)) return null;
        return el.GetString();
    }

    private static bool IsCityLike(string type) => type is "city" or "town" or "village";

    private static string BestNameFromNominatim(JsonElement item, string fallback)
    {
        var name =
            TryReadAddressField(item, "city") ??
            TryReadAddressField(item, "town") ??
            TryReadAddressField(item, "village");

        if (!string.IsNullOrWhiteSpace(name))
            return NormalizeText(name!);

        if (item.TryGetProperty("name", out var n))
        {
            var s = n.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                return NormalizeText(s!);
        }

        if (item.TryGetProperty("display_name", out var d))
        {
            var s = d.GetString() ?? "";
            var first = s.Split(',')[0].Trim();
            if (!string.IsNullOrWhiteSpace(first))
                return NormalizeText(first);
        }

        return NormalizeText(fallback);
    }

    // ------------ province matching (tolerant) ------------

    private static bool MatchesProvince(string? state, string displayName, string provinceNameFa, string provinceId)
    {
        var provFa = StripProvinceWords(provinceNameFa);
        var provEn = StripProvinceWords(provinceId);

        var st = StripProvinceWords(state ?? "");
        var disp = StripProvinceWords(displayName ?? "");

        if (!string.IsNullOrWhiteSpace(st))
        {
            if (st.Contains(provFa, StringComparison.OrdinalIgnoreCase)) return true;
            if (st.Contains(provEn, StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (!string.IsNullOrWhiteSpace(disp))
        {
            if (disp.Contains(provFa, StringComparison.OrdinalIgnoreCase)) return true;
            if (disp.Contains(provEn, StringComparison.OrdinalIgnoreCase)) return true;
        }

        // last fallback: if state missing, accept
        return string.IsNullOrWhiteSpace(state);
    }

    private static string StripProvinceWords(string s)
    {
        s = NormalizeText(s).ToLowerInvariant();
        s = s.Replace("استان", " ").Trim();
        s = s.Replace("province", " ").Trim();
        return s;
    }

    // ------------ Persian/Arabic normalization ------------

    private static string NormalizeText(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";

        return s
            .Replace("ي", "ی")
            .Replace("ك", "ک")
            .Replace("‌", " ")   // ZWNJ -> space
            .Replace("ـ", "")    // tatweel
                                 // remove Arabic diacritics
            .Replace("َ", "")
            .Replace("ُ", "")
            .Replace("ِ", "")
            .Replace("ّ", "")
            .Replace("ً", "")
            .Replace("ٌ", "")
            .Replace("ٍ", "")
            .Trim();
    }

    private static string Trim(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
