using System.Diagnostics;

namespace AiMcpContainers;

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
