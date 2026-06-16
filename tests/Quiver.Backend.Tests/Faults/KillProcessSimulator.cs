namespace Quiver.Backend.Tests.Faults;

/// <summary>
/// BA-9 fault injector: models an OS-level process kill of the host process.
///
/// On Windows true process-kill is unfriendly inside the xUnit runner (file
/// handles are inherited by the test process and an exclusive FileStream lock
/// can't be reopened by the same process unless the handle is released first).
/// We therefore approximate kill by:
///   1. Calling <c>Dispose</c> so OS file handles are released. This is the
///      same shutdown path we'd see on a graceful process exit; backends that
///      rely on Dispose for durability flushes are already broken.
///   2. Forcing two GC cycles so any finalizer-backed handles unwind before
///      the caller reopens the directory.
///
/// The contract under test: any data made durable before this call (i.e.
/// anything that returned successfully from <see cref="IGraphTransaction.Commit"/>)
/// MUST still be recoverable when the directory is reopened. Anything
/// uncommitted MUST NOT be visible after reopen.
/// </summary>
internal static class KillProcessSimulator
{
    public static void SimulateKill(ref IGraphStorageBackend? backend)
    {
        var b = backend;
        backend = null;
        try { b?.Dispose(); } catch { /* kill — losing the in-flight close is the point */ }

        for (int i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
