namespace GarageManagement.Api.Domain;

public sealed record Garage(string Name, IReadOnlyCollection<GarageFloor> Floors);

public sealed record GarageFloor(int FloorNumber, IReadOnlyCollection<GarageBay> Bays);

public sealed record GarageBay(string BayId, IReadOnlyCollection<string> SpotNumbers);

public sealed record GarageFloorSummary(int FloorNumber, int BayCount, int SpotCount);

public sealed record GarageBaySummary(string BayId, int FloorNumber, int SpotCount);

public sealed record AddFloorRequest(int FloorNumber);

public sealed record AddBayRequest(string BayId);

public sealed record AddParkingSpotRequest(int Floor, string Bay, string SpotNumber, ParkingSize Size = ParkingSize.Standard);
