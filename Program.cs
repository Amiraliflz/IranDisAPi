using IranDistanceApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// ✅ Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// your services
builder.Services.AddSingleton<JsonDataStore>();

builder.Services.AddHttpClient<NominatimClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Geocoding:NominatimBaseUrl"]!);

    // Nominatim *expects* a valid User-Agent identifying the application.
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        builder.Configuration["Geocoding:UserAgent"]!
    );

    // Many deployments also expect a From header with an email
    var from = builder.Configuration["Geocoding:FromEmail"];
    if (!string.IsNullOrWhiteSpace(from))
        c.DefaultRequestHeaders.TryAddWithoutValidation("From", from);
});
builder.Services.AddHttpClient<NominatimClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Geocoding:NominatimBaseUrl"]!);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(builder.Configuration["Geocoding:UserAgent"]!);

    var from = builder.Configuration["Geocoding:FromEmail"];
    if (!string.IsNullOrWhiteSpace(from))
        c.DefaultRequestHeaders.TryAddWithoutValidation("From", from);
});
builder.Services.AddHttpClient<OsrmClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Osrm:BaseUrl"]!); // e.g. https://router.project-osrm.org
    c.Timeout = TimeSpan.FromSeconds(25);
});


builder.Services.AddHttpClient("overpass", c =>
{
    // BaseAddress is set per-request in OverpassClient (for failover),
    // but we can set a default.
    c.BaseAddress = new Uri("https://overpass-api.de");

    // Increase timeout a bit (Overpass is slow sometimes)
    c.Timeout = TimeSpan.FromSeconds(45);

    // User-Agent matters
    c.DefaultRequestHeaders.UserAgent.ParseAdd(builder.Configuration["Geocoding:UserAgent"]!);
});

builder.Services.AddSingleton<OverpassClient>();
builder.Services.AddHttpClient<OverpassClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Overpass:BaseUrl"]!); // e.g. https://overpass-api.de
    c.Timeout = TimeSpan.FromSeconds(35);
});



builder.Services.AddHttpClient<OsrmClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Routing:OsrmBaseUrl"]!);
});

builder.Services.AddScoped<DistanceEngine>();

var app = builder.Build();

// ✅ Swagger UI (typically enabled for dev; you can remove the if to enable always)
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapControllers();

app.Run();

