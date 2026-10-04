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
/// <para>
/// Where a thread cannot be started, the transformation runs inline on the caller's stack, with
/// the default depth headroom. That covers browser WebAssembly and WASI, which have no threads
/// (every transform threw PlatformNotSupportedException in Blazor WebAssembly in 2.5.1, xslt#237),
/// and a Start() that fails for another reason, such as a stack reservation the process cannot
/// make.
/// </para>
/// </remarks>
internal static class LargeStack
{
    /// <summary>Stack reserved for a transformation thread.</summary>
    internal const int StackSize = 256 * 1024 * 1024;

    [ThreadStatic]
    private static bool t_onLargeStack;

    // Test seams, scoped to the calling async flow so parallel tests cannot see each other's.
    // ForceInline simulates a platform without threads; StartOverride replaces Thread.Start().
    internal static readonly AsyncLocal<bool> ForceInline = new();
    internal static readonly AsyncLocal<Action<Thread>?> StartOverride = new();

    /// <summary>
    /// Whether a transformation that runs inline, with no large-stack thread, may yield to unwind
    /// its stack: on browser-wasm (the event loop resumes it on a fresh stack), or under
    /// <see cref="ForceInline"/> in tests. Not on WASI, whose single-threaded scheduling is not
    /// relied on.
    /// </summary>
    internal static bool CanYieldForStack
        => !t_onLargeStack && (OperatingSystem.IsBrowser() || ForceInline.Value);

    private static bool CanStartThreads
        => !ForceInline.Value && !OperatingSystem.IsBrowser() && !OperatingSystem.IsWasi();

    internal static Task Run(Func<Task> body)
        => Run(async () => { await body().ConfigureAwait(false); return true; });

    internal static Task<T> Run<T>(Func<Task<T>> body)
    {
        if (t_onLargeStack || !CanStartThreads)
            return body();

        // The caller is NOT blocked while the body runs: the task returned here completes when
        // the body's task does. Joining the thread held the calling thread (in a server, a pool
        // thread) for the whole transformation, and a few long transformations starved the host
        // (measured: 504s arriving 13 s late against a 1 s limit, delayed health checks).
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            t_onLargeStack = true;
            Task<T> task;
            try
            {
                task = body();
            }
#pragma warning disable CA1031 // handed to the caller through the returned task
            catch (Exception ex)
#pragma warning restore CA1031
            {
                completion.TrySetException(ex);
                return;
            }
            // Once the body first truly awaits, its continuations run on the thread pool, as
            // before; this thread ends when the body's synchronous part does.
            task.ContinueWith(static (t, state) =>
            {
                var tcs = (TaskCompletionSource<T>)state!;
                try { tcs.TrySetResult(t.GetAwaiter().GetResult()); }
                catch (OperationCanceledException oce) { tcs.TrySetCanceled(oce.CancellationToken); }
#pragma warning disable CA1031 // handed to the caller through the returned task
                catch (Exception ex) { tcs.TrySetException(ex); }
#pragma warning restore CA1031
            }, completion, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }, StackSize)
        {
            IsBackground = true,
            Name = "PhoenixmlDb.Xslt transform",
        };
        try
        {
            if (StartOverride.Value is { } start)
                start(thread);
            else
                thread.Start();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or ThreadStartException or OutOfMemoryException)
        {
            return body();
        }
        return completion.Task;
    }
}

/// <summary>
/// What a recursion level awaits before it starts: complete (run on) unless the stack needs
/// unwinding, in which case it ALWAYS suspends and the level resumes from the scheduler on a fresh
/// stack. The scheduling is Task.Yield's own; only the guarantee is added.
/// </summary>
#pragma warning disable CA1815 // an awaitable, never compared
internal readonly struct StackYield
{
    private readonly bool _yield;

    internal StackYield(bool yield) => _yield = yield;

    // Resuming anywhere is the point, so there is no context to keep or drop.
    public StackYield ConfigureAwait(bool continueOnCapturedContext) => this;

    public Awaiter GetAwaiter() => new(_yield);

    internal readonly struct Awaiter : System.Runtime.CompilerServices.ICriticalNotifyCompletion
    {
        private readonly bool _yield;

        internal Awaiter(bool yield) => _yield = yield;

        public bool IsCompleted => !_yield;

        public void GetResult() { }

        public void OnCompleted(Action continuation) => Task.Yield().GetAwaiter().OnCompleted(continuation);

        public void UnsafeOnCompleted(Action continuation) => Task.Yield().GetAwaiter().UnsafeOnCompleted(continuation);
    }
}
#pragma warning restore CA1815
