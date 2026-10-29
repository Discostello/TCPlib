using System.Data;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("Host=192.168.1.10;Database=main;Username=postgres;Password=6A6DXfm")
    ?? throw new InvalidOperationException("Connection string not found.");

builder.Services.AddTransient<IDbConnection>(_ => new NpgsqlConnection(connectionString));

var app = builder.Build();

var rentApi = app.MapGroup("/api/v1/rent");

// 1. НАЧАТЬ ПОЕЗДКУ (Фиксация старта в public.rides)
// Маршрут: POST http://192.168.1.10:8080/api/v1/rent/start
rentApi.MapPost("/start", async ([FromBody] StartRideRequest request, IDbConnection db) =>
{
    // Запрос к вашей таблице public.rides. Используем двойные кавычки для "ID", если они критичны в БД
    const string sql = @"
        INSERT INTO public.rides (scooter_id, user_id, ride_start) 
        VALUES (@ScooterId, @UserId, CURRENT_TIMESTAMP) 
        RETURNING ""ID"";";

    try
    {
        var rideId = await db.ExecuteScalarAsync<long>(sql, request);

        return Results.Ok(new
        {
            success = true,
            message = "Ride started successfully",
            rideId = rideId
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Database error: {ex.Message}");
    }
});

// 2. ЗАВЕРШИТЬ ПОЕЗДКУ (Обновление public.rides + создание public."Transactions")
// Маршрут: POST http://192.168.1.10:8080/api/v1/rent/stop
rentApi.MapPost("/stop", async ([FromBody] EndRideRequest request, IDbConnection db) =>
{
    // Открываем транзакцию в C#, так как нужно обновить поездку и списать деньги одновременно
    db.Open();
    using var transaction = db.BeginTransaction();

    try
    {
        // Шаг А: Обновляем запись поездки в public.rides
        const string updateRideSql = @"
            UPDATE public.rides 
            SET ride_end = CURRENT_TIMESTAMP, 
                duration = @Duration, 
                distance = @Distance, 
                cost = @Cost::money
            WHERE ""ID"" = @RideId AND ride_end IS NULL;";

        var rowsAffected = await db.ExecuteAsync(updateRideSql, request, transaction);

        if (rowsAffected == 0)
        {
            return Results.BadRequest(new { success = false, message = "Ride not found or already finished" });
        }

        // Шаг Б: Создаем запись о списании денег в public."Transactions"
        const string insertTransactionSql = @"
            INSERT INTO public.""Transactions"" (""Cli_ID"", ""Sum"", ""Date"", ride_id)
            VALUES (@UserId, @Cost, CURRENT_DATE, @RideId);";

        await db.ExecuteAsync(insertTransactionSql, request, transaction);

        // Если оба запроса прошли успешно — сохраняем изменения в БД
        transaction.Commit();

        return Results.Ok(new { success = true, message = "Ride finished and payment processed" });
    }
    catch (Exception ex)
    {
        transaction.Rollback();
        return Results.Problem($"Transaction failed: {ex.Message}");
    }
    finally
    {
        db.Close();
    }
});

// 3. ПОЛУЧИТЬ ИСТОРИЮ ПОЕЗДОК КЛИЕНТА (GET /api/v1/rent/history/{userId})
rentApi.MapGet("/history/{userId:int}", async (int userId, IDbConnection db) =>
{
    const string sql = @"
        SELECT ""ID"" as Id, scooter_id, user_id, ride_start, ride_end, duration, distance, cost 
        FROM public.rides 
        WHERE user_id = @UserId 
        ORDER BY ride_start DESC;";

    try
    {
        var history = await db.QueryAsync<RideResponse>(sql, new { UserId = userId });
        return Results.Ok(history);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Database error: {ex.Message}");
    }
});

app.Run("http://192.168.1.10:8080");

// Модели данных (DTO) для входящих запросов от самокатов/приложения
public record StartRideRequest(long ScooterId, long UserId);

public record EndRideRequest(
    long RideId,
    long UserId, // cli_id для транзакции
    long Duration,
    double Distance,
    decimal Cost
);

// Модель для ответа истории поездок
public record RideResponse(
    long Id,
    long ScooterId,
    long UserId,
    DateTime RideStart,
    DateTime? RideEnd,
    long? Duration,
    double? Distance,
    string Cost
);
