// The arguments of `status` (docs/research/live-status/README.md, section 4.2): one scope at most
// (--owner <key>, --all, --here, --run <dir>; none means the caller's own runs), or the --whoami
// mode, and the options that go with them. Every refusal is a LaunchException with a fixed text.

using System.Globalization;

namespace LeanWorker;

internal static class StatusOptions
{
    /// <summary>
    /// Modes of later slices: named in the usage error instead of "unknown option".
    /// </summary>
    private static readonly string[] _later = ["--wait", "--watch"];

    public static StatusCommand.StatusArgs Parse(string[] args)
    {
        StatusCommand.StatusArgs a = new(Run: null, Here: false, RunsRoot: null, Since: null, Json: false);
        ArgReader.Read(args, (arg, next) =>
        {
            if (_later.Contains(arg, StringComparer.Ordinal))
            {
                throw new LaunchException($"{arg} is not available yet; this version lists runs once");
            }

            if (Flag(a, arg) is { } flagged)
            {
                a = flagged;
                return true;
            }

            if (Valued(a, arg, next) is not { } valued)
            {
                return false;
            }

            a = valued;
            return true;
        });
        return CheckModes(CheckScopes(a));
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
        if (a.Whoami && (scoped || a.Json || a.Since is not null))
        {
            throw new LaunchException("--whoami takes no scope and no other option but --key");
        }

        if (a.Key && !a.Whoami)
        {
            throw new LaunchException("--key goes with --whoami");
        }

        return a;
    }

    /// <summary>
    /// A duration: a whole number and a unit, s, m, h or d (e.g. 90m, 24h, 7d).
    /// </summary>
    public static TimeSpan ParseDuration(string text)
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

        throw new LaunchException($"--since needs a duration such as 90m, 24h or 7d, not '{text}'");
    }
}
