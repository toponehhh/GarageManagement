using Microsoft.EntityFrameworkCore;
using System.Data.Common;

namespace GarageManagement.Api.Domain;

public sealed class EntityFrameworkParkingGarageStore(string databasePath) : IParkingGarageStore
{
    private readonly string _databasePath = string.IsNullOrWhiteSpace(databasePath)
        ? throw new ArgumentException("Database path is required.", nameof(databasePath))
        : databasePath;

    public ParkingGarageSnapshot InitializeAndLoad(Garage seedGarage)
    {
        EnsureDatabaseDirectoryExists();

        using var context = CreateContext();
        context.Database.EnsureCreated();

        try
        {
            if (!context.ParkingSpots.Any())
            {
                AddSnapshot(context, CreateSnapshotFromGarage(seedGarage));
                context.SaveChanges();
            }

            return LoadSnapshot(context);
        }
        catch (DbException)
        {
            context.Database.EnsureDeleted();
            context.Database.EnsureCreated();
            AddSnapshot(context, CreateSnapshotFromGarage(seedGarage));
            context.SaveChanges();
            return LoadSnapshot(context);
        }
    }

    public void Save(ParkingGarageSnapshot snapshot)
    {
        EnsureDatabaseDirectoryExists();

        using var context = CreateContext();
        context.Database.EnsureCreated();
        using var transaction = context.Database.BeginTransaction();

        context.CarParkingRecords.RemoveRange(context.CarParkingRecords);
        context.SpotUseHistoryRecords.RemoveRange(context.SpotUseHistoryRecords);
        context.ParkingSpots.RemoveRange(context.ParkingSpots);
        context.Bays.RemoveRange(context.Bays);
        context.Floors.RemoveRange(context.Floors);
        context.SaveChanges();

        AddSnapshot(context, snapshot);
        context.SaveChanges();
        transaction.Commit();
    }

    private void EnsureDatabaseDirectoryExists()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_databasePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private GarageDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GarageDbContext>()
            .UseSqlite($"Data Source={_databasePath};Pooling=False")
            .Options;

        return new GarageDbContext(options);
    }

    private static void AddSnapshot(GarageDbContext context, ParkingGarageSnapshot snapshot)
    {
        context.Floors.AddRange(snapshot.Floors
            .Order()
            .Select(floor => new FloorEntity { FloorNumber = floor }));

        context.Bays.AddRange(snapshot.Bays
            .OrderBy(bay => bay.FloorNumber)
            .ThenBy(bay => bay.BayId, StringComparer.OrdinalIgnoreCase)
            .Select(bay => new BayEntity
            {
                FloorNumber = bay.FloorNumber,
                BayId = bay.BayId
            }));

        context.ParkingSpots.AddRange(snapshot.Spots
            .OrderBy(spot => spot.Floor)
            .ThenBy(spot => spot.Bay, StringComparer.OrdinalIgnoreCase)
            .ThenBy(spot => spot.SpotNumber, StringComparer.OrdinalIgnoreCase)
            .Select(spot => new ParkingSpotEntity
            {
                SpotId = spot.SpotId,
                FloorNumber = spot.Floor,
                BayId = spot.Bay,
                SpotNumber = spot.SpotNumber,
                Size = spot.Size.ToString(),
                Status = spot.Status.ToString()
            }));

        context.CarParkingRecords.AddRange(snapshot.ParkingRecords
            .OrderBy(record => record.CheckInTimestamp)
            .Select(record => new CarParkingRecordEntity
            {
                LicensePlateNumber = record.LicensePlateNumber,
                AssignedSpotId = record.AssignedSpotId,
                CheckInTimestamp = record.CheckInTimestamp,
                CheckOutTimestamp = record.CheckOutTimestamp,
                VehicleSize = record.VehicleSize.ToString(),
                ParkingFee = record.ParkingFee
            }));

        context.SpotUseHistoryRecords.AddRange(snapshot.SpotUseHistoryRecords
            .OrderBy(record => record.CheckOutTimestamp)
            .Select(record => new SpotUseHistoryRecordEntity
            {
                SpotId = record.SpotId,
                LicensePlateNumber = record.LicensePlateNumber,
                CheckInTimestamp = record.CheckInTimestamp,
                CheckOutTimestamp = record.CheckOutTimestamp,
                VehicleSize = record.VehicleSize.ToString(),
                ParkingFee = record.ParkingFee
            }));
    }

    private static ParkingGarageSnapshot LoadSnapshot(GarageDbContext context)
    {
        var floors = context.Floors
            .AsNoTracking()
            .OrderBy(floor => floor.FloorNumber)
            .Select(floor => floor.FloorNumber)
            .ToList();

        var spots = context.ParkingSpots
            .AsNoTracking()
            .OrderBy(spot => spot.FloorNumber)
            .ThenBy(spot => spot.BayId)
            .ThenBy(spot => spot.SpotNumber)
            .Select(spot => new ParkingSpot(
                spot.SpotId,
                spot.FloorNumber,
                spot.BayId,
                spot.SpotNumber,
                Enum.Parse<ParkingSize>(spot.Size, ignoreCase: true),
                Enum.Parse<ParkingSpotStatus>(spot.Status, ignoreCase: true)))
            .ToList();

        var spotCountsByBay = spots
            .GroupBy(spot => (spot.Floor, spot.Bay), spot => spot, new FloorBayComparer())
            .ToDictionary(group => group.Key, group => group.Count(), new FloorBayComparer());

        var bays = context.Bays
            .AsNoTracking()
            .OrderBy(bay => bay.FloorNumber)
            .ThenBy(bay => bay.BayId)
            .AsEnumerable()
            .Select(bay => new GarageBaySummary(
                bay.BayId,
                bay.FloorNumber,
                spotCountsByBay.TryGetValue((bay.FloorNumber, bay.BayId), out var spotCount) ? spotCount : 0))
            .ToList();

        var records = context.CarParkingRecords
            .AsNoTracking()
            .OrderBy(record => record.Id)
            .Select(record => new CarParkingRecord(
                record.LicensePlateNumber,
                record.AssignedSpotId,
                record.CheckInTimestamp,
                record.CheckOutTimestamp,
                Enum.Parse<ParkingSize>(record.VehicleSize, ignoreCase: true),
                record.ParkingFee))
            .ToList();

        var spotUseHistory = context.SpotUseHistoryRecords
            .AsNoTracking()
            .OrderBy(record => record.Id)
            .Select(record => new SpotUseHistoryRecord(
                record.SpotId,
                record.LicensePlateNumber,
                record.CheckInTimestamp,
                record.CheckOutTimestamp,
                Enum.Parse<ParkingSize>(record.VehicleSize, ignoreCase: true),
                record.ParkingFee))
            .ToList();

        return new ParkingGarageSnapshot(floors, bays, spots, records, spotUseHistory);
    }

    private static ParkingGarageSnapshot CreateSnapshotFromGarage(Garage garage)
    {
        var floors = garage.Floors.Select(floor => floor.FloorNumber).ToList();
        var bays = garage.Floors
            .SelectMany(floor => floor.Bays.Select(bay => new GarageBaySummary(
                bay.BayId.ToUpperInvariant(),
                floor.FloorNumber,
                bay.SpotNumbers.Count)))
            .ToList();
        var spots = garage.Floors
            .SelectMany(floor => floor.Bays.SelectMany(bay => bay.SpotNumbers.Select(spotNumber =>
            {
                var normalizedBay = bay.BayId.ToUpperInvariant();
                var normalizedSpotNumber = spotNumber.ToUpperInvariant();
                return new ParkingSpot(
                    $"F{floor.FloorNumber}-{normalizedBay}-{normalizedSpotNumber}",
                    floor.FloorNumber,
                    normalizedBay,
                    normalizedSpotNumber,
                    ParkingSize.Standard,
                    ParkingSpotStatus.Available);
            })))
            .ToList();

        return new ParkingGarageSnapshot(floors, bays, spots, [], []);
    }

    private sealed class FloorBayComparer : IEqualityComparer<(int Floor, string Bay)>
    {
        public bool Equals((int Floor, string Bay) x, (int Floor, string Bay) y)
        {
            return x.Floor == y.Floor && x.Bay.Equals(y.Bay, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((int Floor, string Bay) obj)
        {
            return HashCode.Combine(obj.Floor, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Bay));
        }
    }
}

internal sealed class GarageDbContext(DbContextOptions<GarageDbContext> options) : DbContext(options)
{
    public DbSet<FloorEntity> Floors => Set<FloorEntity>();

    public DbSet<BayEntity> Bays => Set<BayEntity>();

    public DbSet<ParkingSpotEntity> ParkingSpots => Set<ParkingSpotEntity>();

    public DbSet<CarParkingRecordEntity> CarParkingRecords => Set<CarParkingRecordEntity>();

    public DbSet<SpotUseHistoryRecordEntity> SpotUseHistoryRecords => Set<SpotUseHistoryRecordEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FloorEntity>(entity =>
        {
            entity.ToTable("Floors");
            entity.HasKey(floor => floor.FloorNumber);
        });

        modelBuilder.Entity<BayEntity>(entity =>
        {
            entity.ToTable("Bays");
            entity.HasKey(bay => new { bay.FloorNumber, bay.BayId });
            entity.HasOne<FloorEntity>()
                .WithMany()
                .HasForeignKey(bay => bay.FloorNumber);
        });

        modelBuilder.Entity<ParkingSpotEntity>(entity =>
        {
            entity.ToTable("ParkingSpots");
            entity.HasKey(spot => spot.SpotId);
            entity.HasOne<BayEntity>()
                .WithMany()
                .HasForeignKey(spot => new { spot.FloorNumber, spot.BayId });
            entity.Property(spot => spot.SpotId).IsRequired();
            entity.Property(spot => spot.BayId).IsRequired();
            entity.Property(spot => spot.SpotNumber).IsRequired();
            entity.Property(spot => spot.Size).IsRequired();
            entity.Property(spot => spot.Status).IsRequired();
        });

        modelBuilder.Entity<CarParkingRecordEntity>(entity =>
        {
            entity.ToTable("CarParkingRecords");
            entity.HasKey(record => record.Id);
            entity.HasOne<ParkingSpotEntity>()
                .WithMany()
                .HasForeignKey(record => record.AssignedSpotId);
            entity.Property(record => record.LicensePlateNumber).IsRequired();
            entity.Property(record => record.AssignedSpotId).IsRequired();
            entity.Property(record => record.CheckInTimestamp).IsRequired();
            entity.Property(record => record.VehicleSize).IsRequired();
        });

        modelBuilder.Entity<SpotUseHistoryRecordEntity>(entity =>
        {
            entity.ToTable("SpotUseHistoryRecords");
            entity.HasKey(record => record.Id);
            entity.HasOne<ParkingSpotEntity>()
                .WithMany()
                .HasForeignKey(record => record.SpotId);
            entity.Property(record => record.SpotId).IsRequired();
            entity.Property(record => record.LicensePlateNumber).IsRequired();
            entity.Property(record => record.CheckInTimestamp).IsRequired();
            entity.Property(record => record.CheckOutTimestamp).IsRequired();
            entity.Property(record => record.VehicleSize).IsRequired();
            entity.Property(record => record.ParkingFee).IsRequired();
        });
    }
}

internal sealed class FloorEntity
{
    public int FloorNumber { get; set; }
}

internal sealed class BayEntity
{
    public int FloorNumber { get; set; }

    public string BayId { get; set; } = string.Empty;
}

internal sealed class ParkingSpotEntity
{
    public string SpotId { get; set; } = string.Empty;

    public int FloorNumber { get; set; }

    public string BayId { get; set; } = string.Empty;

    public string SpotNumber { get; set; } = string.Empty;

    public string Size { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
}

internal sealed class CarParkingRecordEntity
{
    public int Id { get; set; }

    public string LicensePlateNumber { get; set; } = string.Empty;

    public string AssignedSpotId { get; set; } = string.Empty;

    public DateTimeOffset CheckInTimestamp { get; set; }

    public DateTimeOffset? CheckOutTimestamp { get; set; }

    public string VehicleSize { get; set; } = string.Empty;

    public decimal? ParkingFee { get; set; }
}

internal sealed class SpotUseHistoryRecordEntity
{
    public int Id { get; set; }

    public string SpotId { get; set; } = string.Empty;

    public string LicensePlateNumber { get; set; } = string.Empty;

    public DateTimeOffset CheckInTimestamp { get; set; }

    public DateTimeOffset CheckOutTimestamp { get; set; }

    public string VehicleSize { get; set; } = string.Empty;

    public decimal ParkingFee { get; set; }
}
