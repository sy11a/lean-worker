// The arguments of `status` (docs/research/live-status/README.md, section 4.2): one scope at most
// (--owner <key>, --all, --here, --run <dir>; none means the caller's own runs), one mode at most
// (--json, --watch, --wait or --whoami), and the options that go with them. Every refusal is a
// LaunchException with a fixed text.

using System.Globalization;

namespace LeanWorker;

internal static class StatusOptions
{
    /// <summary>
    /// The longest --interval, in seconds.
    /// </summary>
    private const int MaxInterval = 3600;

    /// <summary>
    /// The largest --expect.
    /// </summary>
    private const int MaxExpect = 10_000;

    public static StatusCommand.StatusArgs Parse(string[] args)
    {
        StatusCommand.StatusArgs a = new(Run: null, Here: false, RunsRoot: null, Since: null, Json: false);
        ArgReader.Read(args, (arg, next) =>
        {
            if ((Flag(a, arg) ?? Valued(a, arg, next) ?? Timed(a, arg, next)) is not { } parsed)
            {
                return false;
            }

            a = parsed;
            return true;
        });
        return CheckWaitOptions(CheckModes(CheckScopes(a)));
    }

    private static StatusCommand.StatusArgs? Flag(StatusCommand.StatusArgs a, string arg)
    {
        return arg switch
        {
            "--here" => a with { Here = true },
            "--all" => a with { All = true },
            "--json" => a with { Json = true },
            "--whoami" => a with { Whoami = true },
            "--key" => a with { Key = true },
            _ => ModeFlag(a, arg),
        };
    }

    /// <summary>
    /// The flags of --wait and --watch.
    /// </summary>
    private static StatusCommand.StatusArgs? ModeFlag(StatusCommand.StatusArgs a, string arg)
    {
        return arg switch
        {
            "--wait" => a with { Wait = true },
            "--watch" => a with { Watch = true },
            "--any" => a with { Any = true },
            "--bell" => a with { Bell = true },
            _ => null,
        };
    }

    private static StatusCommand.StatusArgs? Valued(StatusCommand.StatusArgs a, string arg, Func<string> next)
    {
        return arg switch
        {
            "--run" => a with { Run = next() },
            "--runs-root" => a with { RunsRoot = next() },
            "--since" => a with { Since = next() },
            "--owner" => a with { Owner = next() },
            _ => null,
        };
    }

    /// <summary>
    /// The options of --wait and --watch, each parsed as it is read.
    /// </summary>
    private static StatusCommand.StatusArgs? Timed(StatusCommand.StatusArgs a, string arg, Func<string> next)
    {
        return arg switch
        {
            "--interval" => a with { Interval = Whole(next(), "--interval", "a whole number of seconds", 1, MaxInterval) },
            "--timeout" => a with { Timeout = Positive(ParseDuration(next(), "--timeout"), "--timeout") },
            "--grace" => a with { Grace = ParseDuration(next(), "--grace") },
            "--expect" => a with { Expect = Whole(next(), "--expect", "a whole number of runs", 1, MaxExpect) },
            "--started-after" => a with { StartedAfter = ParseTime(next()) },
            _ => null,
        };
    }

    private static StatusCommand.StatusArgs CheckScopes(StatusCommand.StatusArgs a)
    {
        int scopes = new[] { a.Run is not null, a.Here, a.All, a.Owner is not null }.Count(s => s);
        if (scopes > 1)
        {
            throw new LaunchException("pass one scope: --owner <key>, --all, --here or --run <dir>");
        }

        if (a.Owner is { } owner && !OwnerKeys.Valid(owner))
        {
            throw new LaunchException($"--owner needs a key of 1 to {OwnerKeys.MaxLength} characters of A-Z, a-z, 0-9 and _.:@/-");
        }

        if (a.Run is not null && (a.RunsRoot is not null || a.Since is not null))
        {
            throw new LaunchException("--runs-root and --since go with --here");
        }

        if (a.RunsRoot is not null && !a.Here)
        {
            throw new LaunchException("--runs-root goes with --here");
        }

        return a;
    }

    private static StatusCommand.StatusArgs CheckModes(StatusCommand.StatusArgs a)
    {
        bool scoped = a.Run is not null || a.Here || a.All || a.Owner is not null;
        if (a.Whoami && (scoped || a.Json || a.Since is not null || a.Wait || a.Watch || a.StartedAfter is not null))
        {
            throw new LaunchException("--whoami takes no scope and no other option but --key");
        }

        if (a.Key && !a.Whoami)
        {
            throw new LaunchException("--key goes with --whoami");
        }

        if (new[] { a.Json, a.Wait, a.Watch }.Where(m => m).Skip(1).Any())
        {
            throw new LaunchException("pass one mode: --json, --watch, --wait or --whoami");
        }

        return a;
    }

    private static StatusCommand.StatusArgs CheckWaitOptions(StatusCommand.StatusArgs a)
    {
        if (a.Interval is not null && !a.Wait && !a.Watch)
        {
            throw new LaunchException("--interval goes with --wait or --watch");
        }

        if (!a.Wait && (a.Timeout is not null || a.Grace is not null || a.Expect is not null || a.Any))
        {
            throw new LaunchException("--timeout, --grace, --expect and --any go with --wait");
        }

        if (a.Bell && !a.Watch)
        {
            throw new LaunchException("--bell goes with --watch");
        }

        return a;
    }

    /// <summary>
    /// A duration: a whole number and a unit, s, m, h or d (e.g. 90m, 24h, 7d).
    /// </summary>
    public static TimeSpan ParseDuration(string text) => ParseDuration(text, "--since");

    /// <summary>
    /// A duration for <paramref name="option"/>: a whole number and a unit, s, m, h or d.
    /// </summary>
    public static TimeSpan ParseDuration(string text, string option)
    {
        if (text.Length >= 2 && long.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out long n) && n <= 100_000)
        {
            TimeSpan? span = text[^1] switch
            {
                's' => TimeSpan.FromSeconds(n),
                'm' => TimeSpan.FromMinutes(n),
                'h' => TimeSpan.FromHours(n),
                'd' => TimeSpan.FromDays(n),
                _ => null,
            };
            if (span is { } value)
            {
                return value;
            }
        }

        throw new LaunchException($"{option} needs a duration such as 90m, 24h or 7d, not '{text}'");
    }

    /// <summary>
    /// A time for --started-after: a date and time with Z or an offset, e.g. what `date -u +%FT%TZ`
    /// prints. A time without either is refused: read as UTC it lands hours off west or east of it.
    /// </summary>
    public static DateTimeOffset ParseTime(string text)
    {
        // RoundtripKind tells a time with Z (Utc) or an offset (Local) from one without (Unspecified).
        if (text.Contains('T', StringComparison.Ordinal)
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime kind)
            && kind.Kind is not DateTimeKind.Unspecified
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTimeOffset t))
        {
            return t;
        }

        throw new LaunchException($"--started-after needs a date and time with Z or an offset, such as 2026-11-22T13:02:31Z (date -u +%FT%TZ), not '{text}'");
    }

    private static TimeSpan Positive(TimeSpan span, string option) =>
        span > TimeSpan.Zero ? span : throw new LaunchException($"{option} needs a duration above zero");

    private static int Whole(string text, string option, string what, int min, int max)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= min && n <= max)
        {
            return n;
        }

        throw new LaunchException(string.Create(CultureInfo.InvariantCulture, $"{option} needs {what} from {min} to {max}, not '{text}'"));
    }
}
