namespace LeanWorker;

/// <summary>
/// What `status --wait` and `status --watch` need besides the reader (its /proc, clock and boot id)
/// and the environment (its variables and index folder): the sleep between two reads, the terminal
/// and the token that stops them. A test passes a sleep that moves its clock, a terminal of its own
/// and a token it cancels, so neither mode needs real time or a real terminal.
/// </summary>
internal sealed record StatusHost(Func<TimeSpan, CancellationToken, Task> Sleep, IStatusTerminal Terminal, CancellationToken Cancel)
{
    /// <summary>
    /// The process's own: sleeping on <paramref name="clock"/>'s timers, System.Console, no token.
    /// </summary>
    public static StatusHost For(TimeProvider clock) =>
        new(async (delay, token) => await Task.Delay(delay, clock, token).ConfigureAwait(false), new ConsoleTerminal(), CancellationToken.None);
}
