using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string not found.");

builder.Services.AddTransient<IDbConnection>(_ => new NpgsqlConnection(connectionString));

var app = builder.Build();
var rentApi = app.MapGroup("/api/v1/rent");

// Вспомогательная функция SHA-256 хеширования
static string ComputeSha256Hash(string rawData)
{
    byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawData));
    return Convert.ToHexString(bytes).ToLowerInvariant();
}

// 1. НАЧАТЬ ПОЕЗДКУ
rentApi.MapPost("/start", async ([FromBody] StartRideRequest request, IDbConnection db) =>
{
    const string sql = @"
        INSERT INTO public.rides (scooter_id, user_id, ride_start) 
        VALUES (@ScooterId, @UserId, CURRENT_TIMESTAMP) 
        RETURNING ""ID"";";

    try
    {
        var rideId = await db.ExecuteScalarAsync<long>(sql, request);
        string startHash = ComputeSha256Hash($"{rideId}:{request.UserId}:{request.ScooterId}");

        return Results.Ok(new
        {
            success = true,
            message = "Ride started successfully",
            rideId = rideId,
            rideHash = startHash
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Database error: {ex.Message}");
    }
});

// 2. ЗАВЕРШИТЬ ПОЕЗДКУ
rentApi.MapPost("/stop", async ([FromBody] EndRideRequest request, IDbConnection db) =>
{
    db.Open();
    using var transaction = db.BeginTransaction();

    try
    {
        // Генерируем контрольный SHA-256 хеш поездки и транзакции
        string receiptHash = ComputeSha256Hash($"{request.RideId}:{request.UserId}:{request.Duration}:{request.Cost}");

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

        const string insertTransactionSql = @"
            INSERT INTO public.""Transactions"" (""Cli_ID"", ""Sum"", ""Date"", ride_id)
            VALUES (@UserId, @Cost, CURRENT_DATE, @RideId);";

        await db.ExecuteAsync(insertTransactionSql, request, transaction);
        transaction.Commit();

        return Results.Ok(new
        {
            success = true,
            message = "Ride finished and payment processed",
            receiptHash = receiptHash
        });
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

// 3. ПОЛУЧИТЬ ИСТОРИЮ ПОЕЗДОК КЛИЕНТА
rentApi.MapGet("/history/{userId:int}", async (int userId, IDbConnection db) =>
{
    const string sql = @"
        SELECT ""ID"" as Id, scooter_id as ScooterId, user_id as UserId, ride_start as RideStart, 
               ride_end as RideEnd, duration as Duration, distance as Distance, cost::text as Cost 
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

app.Run("http://127.0.0.1:8080");

public record StartRideRequest(long ScooterId, long UserId);

public record EndRideRequest(
    long RideId,
    long UserId,
    long Duration,
    double Distance,
    decimal Cost
);

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
