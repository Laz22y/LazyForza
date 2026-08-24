using System.Threading.Channels;

namespace LazyForza.Modules.EstateRace;

internal sealed class LatestValueSendQueue<T>
{
    private readonly Channel<T> values = Channel.CreateBounded<T>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public bool TryWrite(T value) => values.Writer.TryWrite(value);

    public async Task RunAsync(
        Func<T, CancellationToken, Task> send,
        CancellationToken cancellationToken)
    {
        while (await values.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!values.Reader.TryRead(out var value)) continue;
            while (values.Reader.TryRead(out var newer)) value = newer;
            await send(value, cancellationToken).ConfigureAwait(false);
        }
    }
}
