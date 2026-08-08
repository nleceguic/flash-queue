namespace FlashQueue.Tests.Unit.Support;

internal static class Polling
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, TimeSpan? interval = null)
    {
        var step = interval ?? TimeSpan.FromMilliseconds(20);
        using var cts = new CancellationTokenSource(timeout);

        while (!condition())
        {
            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"La condición no se cumplió dentro de {timeout}.");
            }

            await Task.Delay(step, CancellationToken.None);
        }
    }
}
