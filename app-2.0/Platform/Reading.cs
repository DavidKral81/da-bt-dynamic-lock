namespace DaBtDynamicLock.Platform;

/// <summary>
/// An answer from Windows together with what went wrong getting it.
///
/// Every call in this layer can fail, and the project's rule is that a failure
/// must be visible where somebody will see it - never swallowed, never turned
/// into a made-up value without saying so. So each reading carries both: a
/// value the caller can act on and, when something went wrong, one line for the
/// log. <see cref="Problem"/> is English on purpose - so is the log.
/// </summary>
public readonly record struct Reading<T>(T Value, string? Problem = null)
{
    /// <summary>True when Windows answered and the answer passed its checks.</summary>
    public bool Ok => Problem is null;

    /// <summary>A reading that failed, with the value the caller should assume.</summary>
    public static Reading<T> Failed(T fallback, string problem) => new(fallback, problem);
}
