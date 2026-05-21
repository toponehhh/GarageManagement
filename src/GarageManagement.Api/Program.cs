using GarageManagement.Api.Domain;
using Serilog;
using Serilog.Events;
using System.Text.Json.Serialization;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: Path.Combine("logs", "garage-api-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("Starting Parking Garage Management API.");

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(_ => new Garage(
    "Main Garage",
    [
        new GarageFloor(1, [new GarageBay("A", ["001", "002", "003"]), new GarageBay("B", ["001", "002"])]),
        new GarageFloor(2, [new GarageBay("A", ["001", "002"]), new GarageBay("B", ["001"])])
    ]));
builder.Services.AddSingleton<IParkingGarageStore>(sp =>
{
    var environment = sp.GetRequiredService<IWebHostEnvironment>();
    var configuration = sp.GetRequiredService<IConfiguration>();
    var databasePath = configuration["GarageDatabasePath"]
        ?? Path.Combine(environment.ContentRootPath, "database", "garage.db");

    return new EntityFrameworkParkingGarageStore(databasePath);
});
builder.Services.AddSingleton<ParkingGarageService>();
builder.Services.AddSingleton<CheckInRequestQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CheckInRequestQueue>());
builder.Services.AddHostedService<ParkingHistoryArchiveService>();

var app = builder.Build();

app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "Handled {RequestMethod} {RequestPath} with {StatusCode} in {Elapsed:0.0000} ms";
});

app.MapGet("/", () => Results.Ok(new
{
    name = "Parking Garage Management API",
    endpoints = new[]
    {
        "GET /api/floors",
        "POST /api/floors",
        "GET /api/floors/{floor}/bays",
        "POST /api/floors/{floor}/bays",
        "GET /api/spots",
        "GET /api/spots/available",
        "GET /api/spots/{spotId}",
        "GET /api/spots/{spotId}/history",
        "POST /api/spots",
        "PATCH /api/spots/{spotId}/status",
        "GET /api/cars/active",
        "GET /api/cars/search?licensePlateNumber={licensePlateNumber}",
        "GET /api/cars/{licensePlateNumber}",
        "GET /api/cars/history",
        "POST /api/cars/check-in",
        "POST /api/cars/{licensePlateNumber}/check-out"
    }
}));

app.MapGet("/api/floors", (ParkingGarageService garage) =>
    Results.Ok(garage.GetFloors()));

app.MapPost("/api/floors", (ParkingGarageService garage, AddFloorRequest request) =>
    ToResult(() => Results.Created($"/api/floors/{request.FloorNumber}", garage.AddFloor(request))));

app.MapGet("/api/floors/{floor:int}/bays", (ParkingGarageService garage, int floor) =>
    ToResult(() => Results.Ok(garage.GetBays(floor))));

app.MapPost("/api/floors/{floor:int}/bays", (ParkingGarageService garage, int floor, AddBayRequest request) =>
    ToResult(() => Results.Created($"/api/floors/{floor}/bays/{request.BayId}", garage.AddBay(floor, request))));

app.MapGet("/api/spots", (ParkingGarageService garage) =>
    Results.Ok(garage.GetAllSpots()));

app.MapGet("/api/spots/available", (ParkingGarageService garage) =>
    Results.Ok(garage.GetAvailableSpots()));

app.MapGet("/api/spots/{spotId}", (ParkingGarageService garage, string spotId) =>
    ToResult(() => Results.Ok(garage.GetSpot(spotId))));

app.MapGet("/api/spots/{spotId}/history", (ParkingGarageService garage, string spotId) =>
    ToResult(() => Results.Ok(garage.GetSpotUseHistory(spotId))));

app.MapPost("/api/spots", (ParkingGarageService garage, AddParkingSpotRequest request) =>
    ToResult(() =>
    {
        var spot = garage.AddSpot(request);
        return Results.Created($"/api/spots/{spot.SpotId}", spot);
    }));

app.MapPatch("/api/spots/{spotId}/status", (ParkingGarageService garage, string spotId, UpdateSpotStatusRequest request) =>
    ToResult(() => Results.Ok(garage.UpdateSpotStatus(spotId, request))));

app.MapGet("/api/cars/active", (ParkingGarageService garage) =>
    Results.Ok(garage.GetActiveCars()));

app.MapGet("/api/cars/search", (ParkingGarageService garage, string licensePlateNumber) =>
    ToResult(() => Results.Ok(garage.GetCarByLicensePlate(licensePlateNumber))));

app.MapGet("/api/cars/{licensePlateNumber}", (ParkingGarageService garage, string licensePlateNumber) =>
    ToResult(() => Results.Ok(garage.GetCarByLicensePlate(licensePlateNumber))));

app.MapGet("/api/cars/history", (ParkingGarageService garage) =>
    Results.Ok(garage.GetParkingHistory()));

app.MapPost("/api/cars/check-in", async (CheckInRequestQueue queue, CheckInRequest request, CancellationToken cancellationToken) =>
    await ToResultAsync(async () =>
    {
        var car = await queue.EnqueueAsync(request, cancellationToken);
        return Results.Created($"/api/cars/active/{Uri.EscapeDataString(car.LicensePlateNumber)}", car);
    }));

app.MapPost("/api/cars/{licensePlateNumber}/check-out", (ParkingGarageService garage, string licensePlateNumber) =>
    ToResult(() => Results.Ok(garage.CheckOutCar(licensePlateNumber))));

app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Parking Garage Management API terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}

static IResult ToResult(Func<IResult> action)
{
    try
    {
        return action();
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ErrorResponse(ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new ErrorResponse(ex.Message));
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new ErrorResponse(ex.Message));
    }
}

static async Task<IResult> ToResultAsync(Func<Task<IResult>> action)
{
    try
    {
        return await action();
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new ErrorResponse(ex.Message));
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new ErrorResponse(ex.Message));
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new ErrorResponse(ex.Message));
    }
}

internal sealed record ErrorResponse(string Message);
