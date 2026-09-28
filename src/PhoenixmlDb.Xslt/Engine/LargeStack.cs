namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Runs a transformation on a dedicated thread with a large stack (xslt#197).
/// </summary>
/// <remarks>
/// Each level of stylesheet recursion (a template calling a template, a function calling a
/// function, nested apply-templates) is a chain of roughly 7–15 .NET async frames, and those
/// frames stay on the native stack for as long as every await completes synchronously — which
/// for in-memory evaluation is nearly always. A default thread therefore ran out of stack long
/// before the engine's recursion-depth limit (1200): about 800 levels on
/// Linux, and under 100 on Windows/.NET 8, whose threads get 1 MB. The documented limit was not
/// true on any platform.
/// <para>
/// The thread only runs the synchronous prefix of the transformation and then exits; if an
/// await genuinely yields, its continuation resumes on the thread pool with the recursion's
/// outer frames already unwound into heap-allocated state machines, so the stack depth starts
/// again from zero. The reservation is address space, not memory: pages are committed only as
/// the recursion reaches them.
/// </para>
/// </remarks>
internal static class LargeStack
{
    /// <summary>Stack reserved for a transformation thread.</summary>
    internal const int StackSize = 256 * 1024 * 1024;

    [ThreadStatic]
    private static bool t_onLargeStack;

    internal static Task Run(Func<Task> body)
        => Run(async () => { await body().ConfigureAwait(false); return true; });

    internal static Task<T> Run<T>(Func<Task<T>> body)
    {
        // Nested transformations (fn:transform, xsl:evaluate re-entering the engine) are
        // already on a large stack; another thread would only add a hop.
        if (t_onLargeStack)
            return body();

        Task<T>? task = null;
        Exception? startFailure = null;
        var thread = new Thread(() =>
        {
            t_onLargeStack = true;
            try
            {
                task = body();
            }
#pragma warning disable CA1031 // rethrown on the calling thread below
            catch (Exception ex)
#pragma warning restore CA1031
            {
                startFailure = ex;
            }
        }, StackSize)
        {
            IsBackground = true,
            Name = "PhoenixmlDb.Xslt transform",
        };
        thread.Start();
        thread.Join();
        if (startFailure != null)
            return Task.FromException<T>(startFailure);
        return task!;
    }
}
