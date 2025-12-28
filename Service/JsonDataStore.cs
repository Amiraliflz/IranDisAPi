using System.Text.Json;

namespace IranDistanceApi.Services;

public class JsonDataStore
{
    public record Capital(string NameFa, double Lat, double Lon);
    public record Province(string Id, string NameFa, Capital Capital);

    public IReadOnlyList<Province> Provinces { get; }

    public JsonDataStore(IConfiguration config)
    {
        var provincesPath = config["DataFiles:ProvincesPath"] ?? throw new Exception("Missing DataFiles:ProvincesPath");
        Provinces = Load<List<Province>>(provincesPath);
    }

    private static T Load<T>(string path)
    {
        var json = File.ReadAllText(path);
        var data = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return data ?? throw new Exception($"Failed to load {path}");
    }
}
