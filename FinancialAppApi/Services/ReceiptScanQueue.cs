using System.Threading.Channels;

namespace FinancialAppApi.Services;

// Bounded in-process queue of receipt-scan job ids awaiting OCR processing.
//
// This is the fallback path used when Cloud Tasks is not configured. It replaces a
// fire-and-forget Task.Run per upload, which on a 1-vCPU free-tier Cloud Run container
// could spawn an unbounded number of concurrent AI provider calls under a burst of uploads.
// The bounded capacity provides backpressure: once the queue is full, enqueues wait
// (briefly) rather than piling on more concurrent work than the container can handle.
public class ReceiptScanQueue
{
    private readonly Channel<string> _channel;

    public ReceiptScanQueue()
    {
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(50)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    // Waits for capacity if the queue is momentarily full, then enqueues the job id.
    public ValueTask EnqueueAsync(string jobId, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(jobId, ct);

    public ChannelReader<string> Reader => _channel.Reader;
}
