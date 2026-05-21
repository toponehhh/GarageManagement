using System.Threading.Channels;

namespace GarageManagement.Api.Domain;

public sealed class CheckInRequestQueue : BackgroundService
{
    private const int QueueCapacity = 256;
    private readonly ParkingGarageService _garage;
    private readonly ILogger<CheckInRequestQueue>? _logger;
    private readonly Channel<QueuedCheckInRequest> _queue = Channel.CreateBounded<QueuedCheckInRequest>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    public CheckInRequestQueue(ParkingGarageService garage, ILogger<CheckInRequestQueue>? logger = null)
    {
        _garage = garage;
        _logger = logger;
    }

    public async Task<Car> EnqueueAsync(CheckInRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<Car>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedRequest = new QueuedCheckInRequest(request, completion);

        await _queue.Writer.WriteAsync(queuedRequest, cancellationToken);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var queuedRequest in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var car = _garage.CheckInCar(queuedRequest.Request);
                queuedRequest.Completion.SetResult(car);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Queued check-in failed for license plate {LicensePlateNumber}.", queuedRequest.Request.LicensePlateNumber);
                queuedRequest.Completion.SetException(ex);
            }
        }
    }

    private sealed record QueuedCheckInRequest(
        CheckInRequest Request,
        TaskCompletionSource<Car> Completion);
}
