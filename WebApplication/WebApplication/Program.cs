using Microsoft.AspNetCore.Mvc;
using StackExchange.Redis;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// 1. Получаем строку подключения к Redis из appsettings.json (по умолчанию "localhost:6379")
string redisConnectionString = builder.Configuration.GetConnectionString("RedisConnection")
    ?? "localhost:6379";

// Регистрируем подключение к Redis как Singleton
var redis = ConnectionMultiplexer.Connect(redisConnectionString);
builder.Services.AddSingleton<IConnectionMultiplexer>(redis);

var app = builder.Build();

// Группируем маршрут в соответствии с настройками Nginx
var telemetryApi = app.MapGroup("/api/v1/telemetry");

// МАРШРУТ: Прием координат от самоката
// Самокат отправляет POST запрос на http://192.168.11:5001/api/v1/telemetry/location
telemetryApi.MapPost("/location", async ([FromBody] TelemetryRequest request, IConnectionMultiplexer redisConn) =>
{
    var db = redisConn.GetDatabase();

    // Ключ в Redis, где будут храниться гео-позиции всех активных самокатов
    const string redisGeoKey = "scooters:locations";

    // Ключ для хранения расширенного статуса конкретного самоката (заряд, скорость)
    string scooterStatusKey = $"scooter:status:{request.ScooterId}";

    try
    {
        // Шаг А: Сохраняем координаты в Geo-индекс Redis
        // Аргументы: Ключ, Долгота (Longitude), Широта (Latitude), ID_самоката
        await db.GeoAddAsync(redisGeoKey, request.Longitude, request.Latitude, request.ScooterId.ToString());

        // Шаг Б: Сохраняем дополнительные параметры (заряд батареи и время) в обычный Хеш (Hash)
        var statusData = new HashEntry[]
        {
            new("battery", request.BatteryLevel),
            new("updated_at", DateTime.UtcNow.ToString("o"))
        };
        await db.HashSetAsync(scooterStatusKey, statusData);

        return Results.Ok(new { success = true, message = "Location updated" });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Redis error: {ex.Message}");
    }
});

// МАРШРУТ ДЛЯ МОБИЛЬНОГО ПРИЛОЖЕНИЯ: Найти самокаты рядом с пользователем
// Пример: GET http://localhost:5001/api/v1/telemetry/nearby?lat=55.75&lng=37.61&radiusInMeters=1000
telemetryApi.MapGet("/nearby", async ([FromQuery] double lat, [FromQuery] double lng, [FromQuery] double radiusInMeters, IConnectionMultiplexer redisConn) =>
{
    var db = redisConn.GetDatabase();
    const string redisGeoKey = "scooters:locations";

    try
    {
        // Запрашиваем из Redis самокаты в радиусе ВМЕСТЕ С ИХ КООРДИНАТАМИ
        var results = await db.GeoRadiusAsync(
            redisGeoKey,
            lng,
            lat,
            radiusInMeters,
            GeoUnit.Meters,
            options: GeoRadiusOptions.WithCoordinates
        );

        var scooters = new List<object>();

        foreach (var r in results)
        {
            string id = r.Member.ToString();
            var batteryVal = await db.HashGetAsync($"scooter:status:{id}", "battery");
            int battery = batteryVal.HasValue && int.TryParse(batteryVal.ToString(), out var b) ? b : 100;

            scooters.Add(new
            {
                id = id,
                latitude = r.Position?.Latitude ?? 0,
                longitude = r.Position?.Longitude ?? 0,
                batteryLevel = battery
            });
        }

        return Results.Ok(new
        {
            count = scooters.Count,
            scooters = scooters
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Redis error: {ex.Message}");
    }
});

app.Run("http://127.0.0.1:5001"); // Запускаем строго на порту 5001, как прописано в upstream telemetry_backend в Nginx

// Модель данных (DTO) для координат самоката
public record TelemetryRequest(
    long ScooterId,
    double Latitude,
    double Longitude,
    int BatteryLevel
);
