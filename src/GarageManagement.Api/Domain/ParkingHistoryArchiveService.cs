namespace GarageManagement.Api.Domain;

public sealed class ParkingHistoryArchiveService(
    ParkingGarageService garage,
    ILogger<ParkingHistoryArchiveService> logger) : BackgroundService
{
    private static readonly TimeSpan ArchiveInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ArchiveInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var archivedCount = garage.ArchiveCompletedParkingRecords();
                logger.LogInformation("Archived {ArchivedCount} completed car parking records into spot-use history.", archivedCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to archive completed car parking records.");
            }
        }
    }
}
