namespace GarageManagement.Api.Domain;

public sealed class ParkingGarageService
{
    private const decimal HourlyRate = 5m;
    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly IParkingGarageStore? _store;
    private readonly SortedDictionary<int, SortedSet<string>> _layout = [];
    private readonly Dictionary<string, ParkingSpot> _spots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Car> _activeCars = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CarParkingRecord> _history = [];
    private readonly List<SpotUseHistoryRecord> _spotUseHistory = [];

    public ParkingGarageService(Garage garage, IClock clock, IParkingGarageStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(garage);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _store = store;

        var snapshot = _store?.InitializeAndLoad(garage) ?? CreateSnapshotFromGarage(garage);
        LoadSnapshot(snapshot);
    }

    public IReadOnlyCollection<GarageFloorSummary> GetFloors()
    {
        lock (_gate)
        {
            return _layout
                .Select(floor => new GarageFloorSummary(
                    floor.Key,
                    floor.Value.Count,
                    _spots.Values.Count(spot => spot.Floor == floor.Key)))
                .ToList();
        }
    }

    public GarageFloorSummary AddFloor(AddFloorRequest request)
    {
        lock (_gate)
        {
            AddFloorCore(request.FloorNumber);
            PersistCore();
            return new GarageFloorSummary(request.FloorNumber, 0, 0);
        }
    }

    public IReadOnlyCollection<GarageBaySummary> GetBays(int floor)
    {
        lock (_gate)
        {
            var bays = GetBaySet(floor);
            return bays
                .Select(bay => new GarageBaySummary(
                    bay,
                    floor,
                    _spots.Values.Count(spot => spot.Floor == floor && spot.Bay.Equals(bay, StringComparison.OrdinalIgnoreCase))))
                .ToList();
        }
    }

    public GarageBaySummary AddBay(int floor, AddBayRequest request)
    {
        lock (_gate)
        {
            AddBayCore(floor, request.BayId);
            PersistCore();
            return new GarageBaySummary(NormalizeBay(request.BayId), floor, 0);
        }
    }

    public IReadOnlyCollection<ParkingSpot> GetAllSpots()
    {
        lock (_gate)
        {
            return OrderedSpots().ToList();
        }
    }

    public IReadOnlyCollection<ParkingSpot> GetAvailableSpots()
    {
        lock (_gate)
        {
            return OrderedSpots()
                .Where(spot => spot.Status == ParkingSpotStatus.Available)
                .ToList();
        }
    }

    public ParkingSpot GetSpot(string spotId)
    {
        lock (_gate)
        {
            return GetSpotCore(spotId);
        }
    }

    public ParkingSpot AddSpot(AddParkingSpotRequest request)
    {
        lock (_gate)
        {
            var spot = AddSpotCore(request.Floor, request.Bay, request.SpotNumber, request.Size);
            PersistCore();
            return spot;
        }
    }

    public ParkingSpot UpdateSpotStatus(string spotId, UpdateSpotStatusRequest request)
    {
        lock (_gate)
        {
            var spot = GetSpotCore(spotId);
            var hasActiveCar = _activeCars.Values.Any(car =>
                car.AssignedSpotId.Equals(spot.SpotId, StringComparison.OrdinalIgnoreCase));

            if (hasActiveCar && request.Status == ParkingSpotStatus.Available)
            {
                throw new InvalidOperationException("A spot assigned to an active car can only be freed by checking the car out.");
            }

            var updated = spot with { Status = request.Status };
            _spots[spot.SpotId] = updated;
            PersistCore();
            return updated;
        }
    }

    public Car CheckInCar(CheckInRequest request)
    {
        lock (_gate)
        {
            var licensePlate = NormalizeLicensePlate(request.LicensePlateNumber);
            if (_activeCars.ContainsKey(licensePlate))
            {
                throw new InvalidOperationException($"Car {licensePlate} is already checked in.");
            }

            var spot = OrderedSpots().FirstOrDefault(spot =>
                spot.Status == ParkingSpotStatus.Available &&
                IsSpotCompatibleWithVehicle(spot.Size, request.VehicleSize))
                ?? throw new InvalidOperationException($"No available parking spots compatible with {request.VehicleSize} vehicles.");

            var occupiedSpot = spot with { Status = ParkingSpotStatus.Occupied };
            _spots[occupiedSpot.SpotId] = occupiedSpot;

            var car = new Car(licensePlate, occupiedSpot.SpotId, _clock.UtcNow, request.VehicleSize);
            _activeCars[licensePlate] = car;
            _history.Add(new CarParkingRecord(
                car.LicensePlateNumber,
                car.AssignedSpotId,
                car.CheckInTimestamp,
                null,
                car.VehicleSize,
                null));
            PersistCore();

            return car;
        }
    }

    public CarParkingRecord CheckOutCar(string licensePlateNumber)
    {
        lock (_gate)
        {
            var licensePlate = NormalizeLicensePlate(licensePlateNumber);
            if (!_activeCars.Remove(licensePlate, out var activeCar))
            {
                throw new KeyNotFoundException($"Car {licensePlate} is not checked in.");
            }

            var spot = GetSpotCore(activeCar.AssignedSpotId);
            _spots[spot.SpotId] = spot with { Status = ParkingSpotStatus.Available };

            var completed = new CarParkingRecord(
                activeCar.LicensePlateNumber,
                activeCar.AssignedSpotId,
                activeCar.CheckInTimestamp,
                _clock.UtcNow,
                activeCar.VehicleSize,
                CalculateParkingFee(activeCar.CheckInTimestamp, _clock.UtcNow));

            var historyIndex = _history.FindLastIndex(record =>
                record.LicensePlateNumber.Equals(licensePlate, StringComparison.OrdinalIgnoreCase) &&
                record.CheckOutTimestamp is null);

            if (historyIndex >= 0)
            {
                _history[historyIndex] = completed;
            }
            else
            {
                _history.Add(completed);
            }

            PersistCore();
            return completed;
        }
    }

    public IReadOnlyCollection<Car> GetActiveCars()
    {
        lock (_gate)
        {
            return _activeCars.Values
                .OrderBy(car => car.CheckInTimestamp)
                .ThenBy(car => car.LicensePlateNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public CarParkingRecord GetCarByLicensePlate(string licensePlateNumber)
    {
        lock (_gate)
        {
            var licensePlate = NormalizeLicensePlate(licensePlateNumber);
            return _history
                .LastOrDefault(record => record.LicensePlateNumber.Equals(licensePlate, StringComparison.OrdinalIgnoreCase))
                ?? _spotUseHistory
                    .LastOrDefault(record => record.LicensePlateNumber.Equals(licensePlate, StringComparison.OrdinalIgnoreCase))
                    ?.ToCarParkingRecord()
                ?? throw new KeyNotFoundException($"Car {licensePlate} has no parking record.");
        }
    }

    public IReadOnlyCollection<CarParkingRecord> GetParkingHistory()
    {
        lock (_gate)
        {
            return _history
                .OrderBy(record => record.CheckInTimestamp)
                .ThenBy(record => record.LicensePlateNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public int ArchiveCompletedParkingRecords()
    {
        lock (_gate)
        {
            var completedRecords = _history
                .Where(record => record.CheckOutTimestamp is not null)
                .ToList();

            if (completedRecords.Count == 0)
            {
                return 0;
            }

            foreach (var record in completedRecords)
            {
                AddSpotUseHistoryIfMissing(new SpotUseHistoryRecord(
                    record.AssignedSpotId,
                    record.LicensePlateNumber,
                    record.CheckInTimestamp,
                    record.CheckOutTimestamp!.Value,
                    record.VehicleSize,
                    record.ParkingFee ?? 0m));
            }

            _history.RemoveAll(record => record.CheckOutTimestamp is not null);
            PersistCore();
            return completedRecords.Count;
        }
    }

    public IReadOnlyCollection<SpotUseHistoryRecord> GetSpotUseHistory(string spotId)
    {
        lock (_gate)
        {
            var normalizedSpotId = NormalizeRequired(spotId, nameof(spotId)).ToUpperInvariant();
            GetSpotCore(normalizedSpotId);

            return _spotUseHistory
                .Where(record => record.SpotId.Equals(normalizedSpotId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(record => record.CheckOutTimestamp)
                .ThenBy(record => record.LicensePlateNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private void AddFloorCore(int floorNumber)
    {
        if (floorNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(floorNumber), "Floor number must be greater than zero.");
        }

        if (!_layout.TryAdd(floorNumber, new SortedSet<string>(StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Floor {floorNumber} already exists.");
        }
    }

    private void LoadSnapshot(ParkingGarageSnapshot snapshot)
    {
        foreach (var floor in snapshot.Floors.Order())
        {
            _layout[floor] = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var bay in snapshot.Bays.OrderBy(bay => bay.FloorNumber).ThenBy(bay => bay.BayId, StringComparer.OrdinalIgnoreCase))
        {
            if (!_layout.TryGetValue(bay.FloorNumber, out var bays))
            {
                bays = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                _layout[bay.FloorNumber] = bays;
            }

            bays.Add(NormalizeBay(bay.BayId));
        }

        foreach (var spot in snapshot.Spots.OrderBy(spot => spot.Floor).ThenBy(spot => spot.Bay).ThenBy(spot => spot.SpotNumber))
        {
            if (!_layout.TryGetValue(spot.Floor, out var bays))
            {
                bays = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                _layout[spot.Floor] = bays;
            }

            bays.Add(NormalizeBay(spot.Bay));
            _spots[spot.SpotId] = spot;
        }

        _history.AddRange(snapshot.ParkingRecords.OrderBy(record => record.CheckInTimestamp));
        _spotUseHistory.AddRange(snapshot.SpotUseHistoryRecords.OrderBy(record => record.CheckOutTimestamp));
        foreach (var record in _history.Where(record => record.CheckOutTimestamp is null))
        {
            _activeCars[record.LicensePlateNumber] = new Car(
                record.LicensePlateNumber,
                record.AssignedSpotId,
                record.CheckInTimestamp,
                record.VehicleSize);
        }
    }

    private void PersistCore()
    {
        _store?.Save(CreateSnapshotFromCurrentState());
    }

    private void AddSpotUseHistoryIfMissing(SpotUseHistoryRecord candidate)
    {
        var alreadyExists = _spotUseHistory.Any(record =>
            record.SpotId.Equals(candidate.SpotId, StringComparison.OrdinalIgnoreCase) &&
            record.LicensePlateNumber.Equals(candidate.LicensePlateNumber, StringComparison.OrdinalIgnoreCase) &&
            record.CheckInTimestamp == candidate.CheckInTimestamp &&
            record.CheckOutTimestamp == candidate.CheckOutTimestamp);

        if (!alreadyExists)
        {
            _spotUseHistory.Add(candidate);
        }
    }

    private ParkingGarageSnapshot CreateSnapshotFromCurrentState()
    {
        var floors = _layout.Keys.ToList();
        var bays = _layout
            .SelectMany(floor => floor.Value.Select(bay => new GarageBaySummary(
                bay,
                floor.Key,
                _spots.Values.Count(spot => spot.Floor == floor.Key && spot.Bay.Equals(bay, StringComparison.OrdinalIgnoreCase)))))
            .ToList();

        return new ParkingGarageSnapshot(
            floors,
            bays,
            OrderedSpots().ToList(),
            _history.ToList(),
            _spotUseHistory.ToList());
    }

    private static ParkingGarageSnapshot CreateSnapshotFromGarage(Garage garage)
    {
        var floors = garage.Floors.Select(floor => floor.FloorNumber).ToList();
        var bays = garage.Floors
            .SelectMany(floor => floor.Bays.Select(bay => new GarageBaySummary(
                NormalizeBay(bay.BayId),
                floor.FloorNumber,
                bay.SpotNumbers.Count)))
            .ToList();
        var spots = garage.Floors
            .SelectMany(floor => floor.Bays.SelectMany(bay => bay.SpotNumbers.Select(spotNumber =>
            {
                var normalizedBay = NormalizeBay(bay.BayId);
                var normalizedSpotNumber = NormalizeSpotNumber(spotNumber);
                return new ParkingSpot(
                    BuildSpotId(floor.FloorNumber, normalizedBay, normalizedSpotNumber),
                    floor.FloorNumber,
                    normalizedBay,
                    normalizedSpotNumber,
                    ParkingSize.Standard,
                    ParkingSpotStatus.Available);
            })))
            .ToList();

        return new ParkingGarageSnapshot(floors, bays, spots, [], []);
    }

    private void AddBayCore(int floor, string bay)
    {
        var bays = GetBaySet(floor);
        var normalizedBay = NormalizeBay(bay);
        if (!bays.Add(normalizedBay))
        {
            throw new InvalidOperationException($"Bay {normalizedBay} already exists on floor {floor}.");
        }
    }

    private ParkingSpot AddSpotCore(int floor, string bay, string spotNumber, ParkingSize size)
    {
        var bays = GetBaySet(floor);
        var normalizedBay = NormalizeBay(bay);
        if (!bays.Contains(normalizedBay))
        {
            throw new KeyNotFoundException($"Bay {normalizedBay} does not exist on floor {floor}.");
        }

        var normalizedSpotNumber = NormalizeSpotNumber(spotNumber);
        var spotId = BuildSpotId(floor, normalizedBay, normalizedSpotNumber);
        if (_spots.ContainsKey(spotId))
        {
            throw new InvalidOperationException($"Parking spot {spotId} already exists.");
        }

        var spot = new ParkingSpot(spotId, floor, normalizedBay, normalizedSpotNumber, size, ParkingSpotStatus.Available);
        _spots[spot.SpotId] = spot;
        return spot;
    }

    private SortedSet<string> GetBaySet(int floor)
    {
        if (!_layout.TryGetValue(floor, out var bays))
        {
            throw new KeyNotFoundException($"Floor {floor} does not exist.");
        }

        return bays;
    }

    private ParkingSpot GetSpotCore(string spotId)
    {
        var normalizedSpotId = NormalizeRequired(spotId, nameof(spotId)).ToUpperInvariant();
        return _spots.TryGetValue(normalizedSpotId, out var spot)
            ? spot
            : throw new KeyNotFoundException($"Parking spot {normalizedSpotId} does not exist.");
    }

    private IEnumerable<ParkingSpot> OrderedSpots()
    {
        return _spots.Values
            .OrderBy(spot => spot.Floor)
            .ThenBy(spot => spot.Bay, StringComparer.OrdinalIgnoreCase)
            .ThenBy(spot => spot.SpotNumber, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSpotCompatibleWithVehicle(ParkingSize spotSize, ParkingSize vehicleSize)
    {
        return spotSize >= vehicleSize;
    }

    private static decimal CalculateParkingFee(DateTimeOffset checkIn, DateTimeOffset checkOut)
    {
        var duration = checkOut - checkIn;
        if (duration <= TimeSpan.Zero)
        {
            return 0m;
        }

        return (decimal)Math.Ceiling(duration.TotalHours) * HourlyRate;
    }

    private static string BuildSpotId(int floor, string bay, string spotNumber)
    {
        return $"F{floor}-{bay}-{spotNumber}".ToUpperInvariant();
    }

    private static string NormalizeBay(string bay)
    {
        return NormalizeRequired(bay, nameof(bay)).ToUpperInvariant();
    }

    private static string NormalizeSpotNumber(string spotNumber)
    {
        return NormalizeRequired(spotNumber, nameof(spotNumber)).ToUpperInvariant();
    }

    private static string NormalizeLicensePlate(string licensePlate)
    {
        return NormalizeRequired(licensePlate, nameof(licensePlate)).ToUpperInvariant();
    }

    private static string NormalizeRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }

        return value.Trim();
    }
}

internal static class SpotUseHistoryRecordExtensions
{
    public static CarParkingRecord ToCarParkingRecord(this SpotUseHistoryRecord record)
    {
        return new CarParkingRecord(
            record.LicensePlateNumber,
            record.SpotId,
            record.CheckInTimestamp,
            record.CheckOutTimestamp,
            record.VehicleSize,
            record.ParkingFee);
    }
}
