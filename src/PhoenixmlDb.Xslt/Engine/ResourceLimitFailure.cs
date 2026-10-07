namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Tells a failure that means "stop" from one that means "this expression has no value". Several
/// places recover from an evaluation error by design — a static expression that cannot be
/// evaluated statically, a pattern whose predicate raises a dynamic error (XSLT 3.0 §5.5.4).
/// A regex stopped at its match timeout, or a cancellation, is neither: recovering from it lets
/// the work carry on past the limit that was meant to end it, with a result that then depends
/// on how fast the machine happened to be.
/// </summary>
internal static class ResourceLimitFailure
{
    /// <summary>Whether a failure is a regex match timeout or a cancellation, at any depth.</summary>
    internal static bool Is(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is System.Text.RegularExpressions.RegexMatchTimeoutException or OperationCanceledException)
                return true;
        return false;
    }
}
