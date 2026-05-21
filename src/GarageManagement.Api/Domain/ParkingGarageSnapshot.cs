namespace GarageManagement.Api.Domain;

public sealed record ParkingGarageSnapshot(
    IReadOnlyCollection<int> Floors,
    IReadOnlyCollection<GarageBaySummary> Bays,
    IReadOnlyCollection<ParkingSpot> Spots,
    IReadOnlyCollection<CarParkingRecord> ParkingRecords,
    IReadOnlyCollection<SpotUseHistoryRecord> SpotUseHistoryRecords);

public interface IParkingGarageStore
{
    ParkingGarageSnapshot InitializeAndLoad(Garage seedGarage);

    void Save(ParkingGarageSnapshot snapshot);
}
