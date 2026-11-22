using System.Diagnostics;
using System.Text;

namespace AiMcpContainers;

// One atomic budget shared by both pipes. Cancellation interrupts blocked stdin
// and process waits as soon as either reader crosses the limit.
internal sealed class ProviderOutput(int maxBytes, CancellationTokenSource stop)
{
    private long _bytes;
    private int _exceeded;
    public bool Exceeded => Volatile.Read(ref _exceeded) != 0;

    public async Task<string> ReadAsync(Stream stream)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(buffer, stop.Token)) != 0)
        {
            if (Interlocked.Add(ref _bytes, count) > maxBytes)
            {
                Interlocked.Exchange(ref _exceeded, 1);
                stop.Cancel();
                stop.Token.ThrowIfCancellationRequested();
            }
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    public CodexResult CancelledResult(CancellationToken caller, string provider, TimeSpan timeout) =>
        caller.IsCancellationRequested
            ? new(false, "", "cancelled", "Request was cancelled.")
            : Exceeded
                ? new(false, "", "output_limit_exceeded", "Provider output exceeded the configured byte limit.")
                : new(false, "", "timeout", $"{provider} timed out after {timeout.TotalSeconds:F0}s.");
}

internal static class ProviderProcess
{
    // A killed descendant can still hold a pipe open. Cleanup must not become
    // another unbounded wait, and every reader task's failure must be observed.
    public static async Task<bool> ObserveOutputAsync(Process process, params Task[] readers)
    {
        var output = Task.WhenAll(readers);
        try
        {
            await output.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException)
        {
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            _ = output.ContinueWith(t => { _ = t.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return false;
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        return true;
    }
}
