namespace GestorArchivos.Sync.Windows.Infrastructure;

public sealed class RetryPolicy
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    public async Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync<object?>(
            async ct =>
            {
                await action(ct);
                return null;
            },
            cancellationToken);
    }

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        Exception? lastException = null;

        for (var attempt = 0; attempt <= Delays.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await action(cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
            }
            catch (IOException ex)
            {
                lastException = ex;
            }
            catch (TimeoutException ex)
            {
                lastException = ex;
            }

            if (attempt == Delays.Length)
                break;

            await Task.Delay(
                Delays[attempt],
                cancellationToken);
        }

        throw lastException
              ?? new InvalidOperationException(
                  "La operación no pudo completarse.");
    }
}