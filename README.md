# Parking Garage Management API

ASP.NET Core Web API for managing a parking garage layout, parking spot availability, and car check-in/check-out activity.

Data is stored in SQLite through Entity Framework Core at `src/GarageManagement.Api/database/garage.db` by default. The app creates the schema and seeds the initial garage layout automatically when the database has no parking spots.

A background archive job runs every 24 hours. It moves completed `CarParkingRecord` entries into `SpotUseHistoryRecord` and removes those completed car records so the car parking table stays small while spot-use history remains available.

Spot and vehicle sizes are `Compact`, `Standard`, and `Oversized`. A vehicle can use a spot that is the same size or larger. Parking fees are calculated at `$5.00` per started hour when a car checks out.

Logging uses Serilog. Logs are written to the console and to daily rolling files under `src/GarageManagement.Api/logs/garage-api-.log` when running from the project.

Incoming check-in requests are accepted through an in-memory bounded queue. A single background worker processes queued check-ins against the garage state, which smooths bursts and keeps spot assignment serialized.

## Run

```powershell
& 'C:\Users\huangd\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project src\GarageManagement.Api\GarageManagement.Api.csproj --urls http://localhost:5180
```

To use another database path:

```powershell
& 'C:\Users\huangd\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project src\GarageManagement.Api\GarageManagement.Api.csproj --urls http://localhost:5180 --GarageDatabasePath C:\temp\garage.db
```

## Verify

```powershell
& 'C:\Users\huangd\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project tests\GarageManagement.Tests\GarageManagement.Tests.csproj
& 'C:\Users\huangd\AppData\Local\Microsoft\dotnet\dotnet.exe' build GarageManagement.sln -p:UseAppHost=false
```

## Endpoints

- `GET /api/floors`
- `POST /api/floors`
- `GET /api/floors/{floor}/bays`
- `POST /api/floors/{floor}/bays`
- `GET /api/spots`
- `GET /api/spots/available`
- `GET /api/spots/{spotId}`
- `GET /api/spots/{spotId}/history`
- `POST /api/spots`
- `PATCH /api/spots/{spotId}/status`
- `GET /api/cars/active`
- `GET /api/cars/search?licensePlateNumber={licensePlateNumber}`
- `GET /api/cars/{licensePlateNumber}`
- `GET /api/cars/history`
- `POST /api/cars/check-in`
- `POST /api/cars/{licensePlateNumber}/check-out`

## Example Requests

```powershell
Invoke-RestMethod -Uri 'http://localhost:5180/api/spots/available'

Invoke-RestMethod `
  -Method Post `
  -Uri 'http://localhost:5180/api/cars/check-in' `
  -ContentType 'application/json' `
  -Body '{"licensePlateNumber":"ABC-123","vehicleSize":"Standard"}'

Invoke-RestMethod `
  -Method Post `
  -Uri 'http://localhost:5180/api/cars/ABC-123/check-out'

Invoke-RestMethod `
  -Method Post `
  -Uri 'http://localhost:5180/api/spots' `
  -ContentType 'application/json' `
  -Body '{"floor":3,"bay":"C","spotNumber":"001","size":"Oversized"}'

Invoke-RestMethod -Uri 'http://localhost:5180/api/cars/search?licensePlateNumber=ABC-123'
```
