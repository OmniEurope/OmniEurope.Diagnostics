// SPDX-License-Identifier: EUPL-1.2
namespace OmniEurope.Diagnostics.Logs;

/// <summary>Synchronous file work behind an asynchronous signature, with the task semantics callers expect.</summary>
internal static class SyncTask
{
    /// <summary>
    /// Runs <paramref name="work"/> now, unless <paramref name="cancellationToken"/> is already cancelled: a cancelled
    /// token gives a cancelled task, a failure (a full disk, say) a faulted task rather than a throwing call.
    /// </summary>
    public static Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);
        try
        {
            return Task.FromResult(work());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }
}
