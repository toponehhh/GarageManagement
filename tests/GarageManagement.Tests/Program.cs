using GarageManagement.Api.Domain;

var tests = new GarageManagementBehaviorTests();
tests.CheckInAssignsFirstAvailableSpotAndMarksItOccupied();
tests.CheckInRejectsDuplicateActiveLicensePlate();
tests.CheckOutFreesAssignedSpotAndRecordsTimestamp();
tests.CheckOutRejectsUnknownActiveCar();
tests.AvailableSpotQueryOnlyReturnsAvailableSpots();
tests.SpotStatusCannotBeChangedWhenAnActiveCarOccupiesIt();
tests.FloorsBaysAndSpotsCanBeAddedToTheGarageLayout();
tests.DuplicateParkingSpotIdentifiersAreRejected();
tests.SpotStatusCanBeUpdatedWhenNoActiveCarUsesTheSpot();
tests.EntityFrameworkStorePersistsGarageLayoutSpotStatusAndCarHistory();
tests.CarCanBeLookedUpByLicensePlateNumber();
tests.SpotUseHistoryRecordIsNotSavedUntilArchiveRuns();
tests.CompletedCarParkingRecordsAreArchivedToSpotUseHistory();
tests.OversizedCarRequiresOversizedSpot();
tests.CompactCarCanUseStandardSpot();
tests.CheckOutCalculatesRoundedHourlyParkingFee();
await tests.InMemoryCheckInQueueProcessesSubmittedRequests();

Console.WriteLine("All behavior tests passed.");

internal sealed class GarageManagementBehaviorTests
{
    public void CheckInAssignsFirstAvailableSpotAndMarksItOccupied()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 21, 8, 0, 0, TimeSpan.Zero));
        var service = CreateService(clock);

        var checkIn = service.CheckInCar(new CheckInRequest("ABC-123"));

        Assert.Equal("F1-A-001", checkIn.AssignedSpotId);
        Assert.Equal(clock.UtcNow, checkIn.CheckInTimestamp);
        Assert.Equal(ParkingSpotStatus.Occupied, service.GetSpot("F1-A-001").Status);
    }

    public void CheckInRejectsDuplicateActiveLicensePlate()
    {
        var service = CreateService();

        service.CheckInCar(new CheckInRequest("ABC-123"));

        Assert.Throws<InvalidOperationException>(() => service.CheckInCar(new CheckInRequest("abc-123")));
    }

    public void CheckOutFreesAssignedSpotAndRecordsTimestamp()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 21, 8, 0, 0, TimeSpan.Zero));
        var service = CreateService(clock);
        service.CheckInCar(new CheckInRequest("ABC-123"));
        clock.UtcNow = clock.UtcNow.AddHours(2);

        var completed = service.CheckOutCar("ABC-123");

        Assert.Equal("ABC-123", completed.LicensePlateNumber);
        Assert.Equal("F1-A-001", completed.AssignedSpotId);
        Assert.Equal(clock.UtcNow, completed.CheckOutTimestamp);
        Assert.Equal(ParkingSpotStatus.Available, service.GetSpot("F1-A-001").Status);
    }

    public void CheckOutRejectsUnknownActiveCar()
    {
        var service = CreateService();

        Assert.Throws<KeyNotFoundException>(() => service.CheckOutCar("MISSING"));
    }

    public void AvailableSpotQueryOnlyReturnsAvailableSpots()
    {
        var service = CreateService();
        service.CheckInCar(new CheckInRequest("ABC-123"));

        var available = service.GetAvailableSpots().ToList();

        Assert.DoesNotContain(available, spot => spot.SpotId == "F1-A-001");
        Assert.Contains(available, spot => spot.SpotId == "F1-A-002");
    }

    public void SpotStatusCannotBeChangedWhenAnActiveCarOccupiesIt()
    {
        var service = CreateService();
        service.CheckInCar(new CheckInRequest("ABC-123"));

        Assert.Throws<InvalidOperationException>(() =>
            service.UpdateSpotStatus("F1-A-001", new UpdateSpotStatusRequest(ParkingSpotStatus.Available)));
    }

    public void FloorsBaysAndSpotsCanBeAddedToTheGarageLayout()
    {
        var service = CreateService();

        service.AddFloor(new AddFloorRequest(3));
        service.AddBay(3, new AddBayRequest("C"));
        var spot = service.AddSpot(new AddParkingSpotRequest(3, "C", "009"));

        Assert.Equal("F3-C-009", spot.SpotId);
        Assert.Equal(3, spot.Floor);
        Assert.Equal("C", spot.Bay);
        Assert.Equal(ParkingSpotStatus.Available, spot.Status);
        Assert.Contains(service.GetFloors(), floor => floor.FloorNumber == 3);
        Assert.Contains(service.GetBays(3), bay => bay.BayId == "C");
    }

    public void DuplicateParkingSpotIdentifiersAreRejected()
    {
        var service = CreateService();

        Assert.Throws<InvalidOperationException>(() =>
            service.AddSpot(new AddParkingSpotRequest(1, "A", "001")));
    }

    public void SpotStatusCanBeUpdatedWhenNoActiveCarUsesTheSpot()
    {
        var service = CreateService();

        var updated = service.UpdateSpotStatus("F1-A-001", new UpdateSpotStatusRequest(ParkingSpotStatus.Occupied));

        Assert.Equal(ParkingSpotStatus.Occupied, updated.Status);
        Assert.DoesNotContain(service.GetAvailableSpots(), spot => spot.SpotId == "F1-A-001");
    }

    public void EntityFrameworkStorePersistsGarageLayoutSpotStatusAndCarHistory()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"garage-management-{Guid.NewGuid():N}.db");
        try
        {
            var clock = new FakeClock(new DateTimeOffset(2026, 5, 21, 8, 0, 0, TimeSpan.Zero));
            var firstService = CreateService(clock, databasePath);

            firstService.AddFloor(new AddFloorRequest(3));
            firstService.AddBay(3, new AddBayRequest("C"));
            firstService.AddSpot(new AddParkingSpotRequest(3, "C", "009"));
            firstService.CheckInCar(new CheckInRequest("DB-100"));
            clock.UtcNow = clock.UtcNow.AddMinutes(30);
            firstService.CheckOutCar("DB-100");
            firstService.UpdateSpotStatus("F3-C-009", new UpdateSpotStatusRequest(ParkingSpotStatus.Occupied));

            var secondService = CreateService(new FakeClock(), databasePath);

            Assert.Contains(secondService.GetFloors(), floor => floor.FloorNumber == 3);
            Assert.Contains(secondService.GetBays(3), bay => bay.BayId == "C");
            Assert.Equal(ParkingSpotStatus.Occupied, secondService.GetSpot("F3-C-009").Status);
            Assert.Contains(secondService.GetParkingHistory(), record =>
                record.LicensePlateNumber == "DB-100" &&
                record.AssignedSpotId == "F1-A-001" &&
                record.CheckOutTimestamp is not null);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    public void CarCanBeLookedUpByLicensePlateNumber()
    {
        var service = CreateService();
        service.CheckInCar(new CheckInRequest("LOOK-100"));

        var activeRecord = service.GetCarByLicensePlate("look-100");

        Assert.Equal("LOOK-100", activeRecord.LicensePlateNumber);
        Assert.Equal("F1-A-001", activeRecord.AssignedSpotId);
        Assert.Equal(null, activeRecord.CheckOutTimestamp);

        service.CheckOutCar("LOOK-100");
        var completedRecord = service.GetCarByLicensePlate("LOOK-100");

        Assert.Equal("LOOK-100", completedRecord.LicensePlateNumber);
        Assert.True(completedRecord.CheckOutTimestamp is not null, "Expected checked-out car lookup to include check-out timestamp.");
    }

    public void SpotUseHistoryRecordIsNotSavedUntilArchiveRuns()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"garage-management-{Guid.NewGuid():N}.db");
        try
        {
            var clock = new FakeClock(new DateTimeOffset(2026, 5, 21, 8, 0, 0, TimeSpan.Zero));
            var firstService = CreateService(clock, databasePath);

            firstService.CheckInCar(new CheckInRequest("SPOT-100"));
            clock.UtcNow = clock.UtcNow.AddHours(3);
            firstService.CheckOutCar("SPOT-100");

            var secondService = CreateService(new FakeClock(), databasePath);
            var spotHistory = secondService.GetSpotUseHistory("F1-A-001").ToList();

            Assert.Equal(0, spotHistory.Count);
            Assert.Contains(secondService.GetParkingHistory(), record =>
                record.LicensePlateNumber == "SPOT-100" &&
                record.CheckOutTimestamp == new DateTimeOffset(2026, 5, 21, 11, 0, 0, TimeSpan.Zero));
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    public void CompletedCarParkingRecordsAreArchivedToSpotUseHistory()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"garage-management-{Guid.NewGuid():N}.db");
        try
        {
            var clock = new FakeClock(new DateTimeOffset(2026, 5, 21, 8, 0, 0, TimeSpan.Zero));
            var firstService = CreateService(clock, databasePath);

            firstService.CheckInCar(new CheckInRequest("ARCH-100"));
            clock.UtcNow = clock.UtcNow.AddHours(1);
            firstService.CheckOutCar("ARCH-100");
            firstService.CheckInCar(new CheckInRequest("ACTIVE-100"));

            var archivedCount = firstService.ArchiveCompletedParkingRecords();

            Assert.Equal(1, archivedCount);
            Assert.DoesNotContain(firstService.GetParkingHistory(), record => record.LicensePlateNumber == "ARCH-100");
            Assert.Contains(firstService.GetParkingHistory(), record =>
                record.LicensePlateNumber == "ACTIVE-100" && record.CheckOutTimestamp is null);
            Assert.Equal(1, firstService.GetSpotUseHistory("F1-A-001").Count(record => record.LicensePlateNumber == "ARCH-100"));

            var archivedLookup = firstService.GetCarByLicensePlate("ARCH-100");
            Assert.Equal("ARCH-100", archivedLookup.LicensePlateNumber);
            Assert.True(archivedLookup.CheckOutTimestamp is not null, "Expected archived car lookup to use spot-use history.");

            var secondService = CreateService(new FakeClock(), databasePath);
            Assert.DoesNotContain(secondService.GetParkingHistory(), record => record.LicensePlateNumber == "ARCH-100");
            Assert.Contains(secondService.GetParkingHistory(), record =>
                record.LicensePlateNumber == "ACTIVE-100" && record.CheckOutTimestamp is null);
            Assert.Equal(1, secondService.GetSpotUseHistory("F1-A-001").Count(record => record.LicensePlateNumber == "ARCH-100"));
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    public void OversizedCarRequiresOversizedSpot()
    {
        var service = CreateService();

        Assert.Throws<InvalidOperationException>(() =>
            service.CheckInCar(new CheckInRequest("BIG-100", ParkingSize.Oversized)));

        service.AddFloor(new AddFloorRequest(3));
        service.AddBay(3, new AddBayRequest("C"));
        service.AddSpot(new AddParkingSpotRequest(3, "C", "001", ParkingSize.Oversized));

        var car = service.CheckInCar(new CheckInRequest("BIG-100", ParkingSize.Oversized));

        Assert.Equal("F3-C-001", car.AssignedSpotId);
        Assert.Equal(ParkingSize.Oversized, car.VehicleSize);
    }

    public void CompactCarCanUseStandardSpot()
    {
        var service = CreateService();

        var car = service.CheckInCar(new CheckInRequest("SMALL-100", ParkingSize.Compact));

        Assert.Equal("F1-A-001", car.AssignedSpotId);
        Assert.Equal(ParkingSize.Compact, car.VehicleSize);
        Assert.Equal(ParkingSize.Standard, service.GetSpot(car.AssignedSpotId).Size);
    }

    public void CheckOutCalculatesRoundedHourlyParkingFee()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 21, 8, 0, 0, TimeSpan.Zero));
        var service = CreateService(clock);
        service.CheckInCar(new CheckInRequest("FEE-100", ParkingSize.Standard));
        clock.UtcNow = clock.UtcNow.AddHours(2).AddMinutes(15);

        var completed = service.CheckOutCar("FEE-100");

        Assert.Equal(15m, completed.ParkingFee);
    }

    public async Task InMemoryCheckInQueueProcessesSubmittedRequests()
    {
        var service = CreateService();
        var queue = new CheckInRequestQueue(service);
        await queue.StartAsync(CancellationToken.None);

        try
        {
            var queuedCheckIns = Enumerable.Range(1, 3)
                .Select(index => queue.EnqueueAsync(
                    new CheckInRequest($"QUEUE-{index:000}", ParkingSize.Standard),
                    CancellationToken.None))
                .ToArray();

            var cars = await Task.WhenAll(queuedCheckIns);

            Assert.Equal(3, cars.Length);
            Assert.Equal("F1-A-001", cars[0].AssignedSpotId);
            Assert.Equal("F1-A-002", cars[1].AssignedSpotId);
            Assert.Equal("F1-B-001", cars[2].AssignedSpotId);
            Assert.Equal(3, service.GetActiveCars().Count);
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }

    private static ParkingGarageService CreateService(IClock? clock = null, string? databasePath = null)
    {
        var garage = new Garage(
            "Main Garage",
            [
                new GarageFloor(1, [new GarageBay("A", ["001", "002"]), new GarageBay("B", ["001"])]),
                new GarageFloor(2, [new GarageBay("A", ["001"])])
            ]);

        return databasePath is null
            ? new ParkingGarageService(garage, clock ?? new FakeClock())
            : new ParkingGarageService(garage, clock ?? new FakeClock(), new EntityFrameworkParkingGarageStore(databasePath));
    }
}

internal sealed class FakeClock(DateTimeOffset? initialValue = null) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = initialValue ?? new DateTimeOffset(2026, 5, 21, 8, 0, 0, TimeSpan.Zero);
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    public static void Contains<T>(IEnumerable<T> items, Func<T, bool> predicate)
    {
        if (!items.Any(predicate))
        {
            throw new InvalidOperationException("Expected sequence to contain a matching item.");
        }
    }

    public static void DoesNotContain<T>(IEnumerable<T> items, Func<T, bool> predicate)
    {
        if (items.Any(predicate))
        {
            throw new InvalidOperationException("Expected sequence not to contain a matching item.");
        }
    }

    public static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Expected {typeof(TException).Name}, got {ex.GetType().Name}.", ex);
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}, but no exception was thrown.");
    }

    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
