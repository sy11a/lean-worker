namespace LeanWorker.Tests;

/// <summary>
/// What happens to a run of a StatusLiveRig, by a short name, so a table of scenarios reads as one line each:
/// how the run ends (success, error, timeout, wrapped-up, signalled), how it disappears (gone, lost, orphaned),
/// how a success end records its exit code (exit0, exit4, exit5, noexit).
/// </summary>
internal static class StatusWaitScenarios
{
    /// <summary>
    /// Makes <paramref name="how"/> happen to <paramref name="run"/>.
    /// </summary>
    public static void Apply(StatusLiveRig rig, StatusLiveRig.Live run, string how)
    {
        if (how is "success" or "error" or "timeout" or "wrapped-up")
        {
            rig.End(run, how);
        }
        else if (how is "signalled")
        {
            rig.End(run, "launcher-signalled", exitCode: 1);
        }
        else if (how is "exit0" or "exit4" or "exit5")
        {
            rig.End(run, "success", exitCode: how[4] - '0');
        }
        else if (how is "noexit")
        {
            rig.End(run, "success", omitExitCode: true);
        }
        else if (how is "gone")
        {
            rig.Vanish(run);
        }
        else if (how is "lost")
        {
            rig.Lose(run);
        }
        else if (how is "orphaned")
        {
            rig.Orphan(run);
        }
        else
        {
            throw new ArgumentException("no such scenario: " + how, nameof(how));
        }
    }

    /// <summary>
    /// The state word the listing shows for a run that <see cref="Apply"/> ended that way.
    /// </summary>
    public static string StateWord(string how)
    {
        if (how is "signalled")
        {
            return "failed";
        }

        return how is "exit0" or "exit4" or "exit5" or "noexit" ? "success" : how;
    }

    /// <summary>
    /// The runs of a table row (names separated by spaces), each added to the rig and ended
    /// ten seconds after the one before; "unknown" is a run whose launcher cannot be read and never ends.
    /// </summary>
    public static List<StatusLiveRig.Live> Run(StatusLiveRig rig, string table)
    {
        List<StatusLiveRig.Live> runs = [];
        string[] steps = table.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < steps.Length; i++)
        {
            string how = steps[i];
            StatusLiveRig.Live run = rig.Add("run-" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            runs.Add(run);
            if (how is "unknown")
            {
                rig.Blur(run);
            }
            else
            {
                rig.At(TimeSpan.FromSeconds(10 * (i + 1)), () => Apply(rig, run, how));
            }
        }

        return runs;
    }
}
