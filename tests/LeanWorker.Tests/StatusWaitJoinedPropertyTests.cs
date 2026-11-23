using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// A row joined to a unit of `status --wait` only makes the verdict stricter (the review of #147, finding 1): over every
/// state a unit's own row and a joined row can end in, a joined row never turns an exit code that is not 0 into 0, and a
/// 0 turns into 0, 1 or 8 and nothing else. A forger who writes a row that names the unit's launcher or chain id gains
/// nothing by it. The joined row comes by the chain id (another launcher, its own life) or by the launcher (the pointer
/// names the unit's launcher, so the row lives and dies with it).
/// </summary>
[Collection("launcher-process-state")]
public sealed class StatusWaitJoinedPropertyTests
{
    // How the unit's own run ends, and the exit code it gives alone.
    private static readonly (string How, int Code)[] _own =
    [
        ("success", 0),
        ("error", 1),
        ("wrapped-up", 3),
        ("exit4", 1),
        ("gone", 1),
        ("lost", 1),
        ("unknown", 8),
    ];

    private static readonly string[] _byChainId = ["success", "error", "wrapped-up", "exit4", "gone", "lost", "orphaned", "unknown", "live", "unlisted"];

    private static readonly string[] _byLauncher = ["success", "error", "wrapped-up", "exit4", "garbled", "missing", "gone-dir", "unlisted", "live"];

    private static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);

    private static void Own(StatusLiveRig rig, StatusLiveRig.Live run, string how)
    {
        if (how is "unknown")
        {
            rig.Blur(run);
        }
        else
        {
            StatusWaitScenarios.Apply(rig, run, how);
        }
    }

    // The unit's own run, a plain one; the joined row (when there is one) appears at 5 s, is brought to its state at
    // 10 s and the unit's run ends at 20 s.
    private static async Task<int> CodeAsync(string own, string? joined, bool byLauncher)
    {
        using StatusLiveRig rig = new();
        StatusLiveRig.Live a = rig.Add("plain-run");
        StatusLiveRig.Live? j = null;
        if (joined is not null)
        {
            rig.At(Seconds(5), () => j = byLauncher
                ? rig.Add("joined-by-launcher", launcher: a.Pid)
                : rig.Add("joined-by-chain", startedAgo: TimeSpan.FromMinutes(4), chain: a.Run.Id, round: 2));
            rig.At(Seconds(10), () => Bring(rig, j!, joined, byLauncher));
        }

        rig.At(Seconds(20), () => Own(rig, a, own));

        StatusLiveRig.Result r = await rig.CallAsync("--owner", StatusLiveRig.Key, "--wait", "--grace", "1m", "--timeout", "10m", "--interval", "10");

        Assert.Null(r.Error);
        return r.Code;
    }

    private static void Bring(StatusLiveRig rig, StatusLiveRig.Live j, string how, bool byLauncher)
    {
        if (!byLauncher)
        {
            if (how is "unknown")
            {
                rig.Blur(j);
            }
            else if (how is "unlisted")
            {
                rig.Unlist(j);
            }
            else if (how is not "live")
            {
                StatusWaitScenarios.Apply(rig, j, how);
            }

            return;
        }

        // A row that shares the unit's launcher cannot end it by itself: it records what its file says now, and the
        // launcher's end (the unit's own) is what ends it.
        if (how is "success" or "error" or "wrapped-up")
        {
            rig.Record(j, how);
        }
        else if (how is "exit4")
        {
            rig.Record(j, "success", exitCode: 4);
        }
        else if (how is "garbled")
        {
            StatusLiveRig.Garble(j);
        }
        else if (how is "missing")
        {
            StatusLiveRig.Unwrite(j);
        }
        else if (how is "gone-dir")
        {
            Directory.Delete(j.Dir, recursive: true);
        }
        else if (how is "unlisted")
        {
            rig.Unlist(j);
        }
    }

    // Every call has the rig's own limit; the whole loop has this one.
    private static readonly TimeSpan _limit = TimeSpan.FromMinutes(4);

    [Fact]
    public async Task The_harness_gives_each_own_state_the_exit_code_it_has_without_a_joined_rowAsync()
    {
        foreach ((string how, int code) in _own)
        {
            int actual = await CodeAsync(how, joined: null, byLauncher: false).WaitAsync(_limit, TimeProvider.System, TestContext.Current.CancellationToken);
            Assert.True(code == actual, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"own {how} alone: expected {code}, got {actual}"));
        }
    }

    [Fact]
    public async Task A_row_joined_by_the_chain_id_never_turns_a_verdict_that_is_not_0_into_0_and_a_0_only_into_1_or_8Async()
    {
        List<string> broken = await BrokenAsync(_byChainId, byLauncher: false).WaitAsync(_limit, TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.Empty(broken);
    }

    [Fact]
    public async Task A_row_joined_by_the_launcher_never_turns_a_verdict_that_is_not_0_into_0_and_a_0_only_into_1_or_8Async()
    {
        List<string> broken = await BrokenAsync(_byLauncher, byLauncher: true).WaitAsync(_limit, TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.Empty(broken);
    }

    // The pairs (own state, joined state) whose exit code breaks the rule: a joined row that gives 0 where the unit alone
    // gave something else, or that makes a 0 anything but 0, 1 or 8, or that ends the wait with a code nobody returns.
    private static async Task<List<string>> BrokenAsync(string[] joinedStates, bool byLauncher)
    {
        List<string> broken = [];
        foreach ((string own, int alone) in _own)
        {
            foreach (string joined in joinedStates)
            {
                int code = await CodeAsync(own, joined, byLauncher);
                bool known = code is 0 or 1 or 3 or 8;
                bool lifted = alone is not 0 && code is 0;
                bool loosened = alone is 0 && code is not (0 or 1 or 8);
                bool allowed = known && !lifted && !loosened;
                if (!allowed)
                {
                    broken.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"own {own} (alone {alone}) + joined {joined} {(byLauncher ? "by launcher" : "by chain id")} = {code}"));
                }
            }
        }

        return broken;
    }

}
