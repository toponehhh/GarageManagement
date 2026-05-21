namespace GarageManagement.Api.Domain;

public enum ParkingSpotStatus
{
    Available,
    Occupied
}

public enum ParkingSize
{
    Compact,
    Standard,
    Oversized
}

public sealed record ParkingSpot(
    string SpotId,
    int Floor,
    string Bay,
    string SpotNumber,
    ParkingSize Size,
    ParkingSpotStatus Status);

public sealed record UpdateSpotStatusRequest(ParkingSpotStatus Status);

public sealed record CheckInRequest(string LicensePlateNumber, ParkingSize VehicleSize = ParkingSize.Standard);

public sealed record Car(
    string LicensePlateNumber,
    string AssignedSpotId,
    DateTimeOffset CheckInTimestamp,
    ParkingSize VehicleSize);

public sealed record CarParkingRecord(
    string LicensePlateNumber,
    string AssignedSpotId,
    DateTimeOffset CheckInTimestamp,
    DateTimeOffset? CheckOutTimestamp,
    ParkingSize VehicleSize,
    decimal? ParkingFee);

public sealed record SpotUseHistoryRecord(
    string SpotId,
    string LicensePlateNumber,
    DateTimeOffset CheckInTimestamp,
    DateTimeOffset CheckOutTimestamp,
    ParkingSize VehicleSize,
    decimal ParkingFee);
