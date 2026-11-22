using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Nested runs in the index scopes (docs/research/live-status/README.md, section 4.4): a run launched inside
/// another run is listed right after its parent, indented two spaces per level, at most 8 levels, found through
/// parent_run transitively, and a parent_run cycle cannot loop. The caller inside a worker is that run.
/// </summary>
public sealed class StatusIndexNestingTests : IDisposable
{
    private const string RunningGap = "running  ";

    private static readonly string[] _mine = ["claude-session:sess-A"];

    private readonly StatusIndexRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static TimeSpan Ago(int minutes) => TimeSpan.FromMinutes(minutes);

    private static string Level(int i) => "lvl" + i.ToString(CultureInfo.InvariantCulture);

    // The name of a running row without its indent: after the state and the two-space column gap.
    private static string NameOf(string row)
    {
        Assert.StartsWith(RunningGap, row, StringComparison.Ordinal);
        return row[RunningGap.Length..].TrimStart().Split(' ')[0];
    }

    // The indent of a run's name in the plain output: the spaces between the column gap and the name.
    private static int Indent(string[] rows, string name)
    {
        string row = rows.Single(r => NameOf(r) == name);
        string rest = row[RunningGap.Length..];
        return rest.Length - rest.TrimStart().Length;
    }

    private async Task<string[]> PlainRowsAsync(Func<string, string?> env)
    {
        (int code, string output) = await _rig.CallAsync(env);
        Assert.Equal(0, code);
        return StatusIndexRig.Rows(output);
    }

    [Fact]
    public async Task A_nested_run_is_listed_right_after_its_parent_one_level_deeper_down_to_the_grandchildAsync()
    {
        StatusIndexRig.Added parent = _rig.Add("parent-run", _mine, startedAgo: Ago(30));
        _ = _rig.Add("other-run", _mine, startedAgo: Ago(25));
        StatusIndexRig.Added child = _rig.Add("child-run", ["run:" + parent.Id], parent: parent.Id, startedAgo: Ago(20));
        _ = _rig.Add("grandchild-run", ["run:" + child.Id], parent: child.Id, startedAgo: Ago(10));

        string[] rows = await PlainRowsAsync(StatusIndexRig.Session("sess-A"));

        Assert.Equal(["parent-run", "child-run", "grandchild-run", "other-run"], [.. rows.Select(r => NameOf(r))]);
        Assert.Equal(0, Indent(rows, "parent-run"));
        Assert.Equal(2, Indent(rows, "child-run"));
        Assert.Equal(4, Indent(rows, "grandchild-run"));
        Assert.Equal(0, Indent(rows, "other-run"));
    }

    [Fact]
    public async Task Json_lists_the_nested_runs_in_the_same_order_with_their_depth_and_parent_runAsync()
    {
        StatusIndexRig.Added parent = _rig.Add("parent-run", _mine, startedAgo: Ago(30));
        _ = _rig.Add("other-run", _mine, startedAgo: Ago(25));
        _ = _rig.Add("child-run", ["run:" + parent.Id], parent: parent.Id, startedAgo: Ago(20));

        (_, string json) = await _rig.CallAsync(StatusIndexRig.Session("sess-A"), "--json");

        Assert.Equal(["parent-run", "child-run", "other-run"], StatusIndexRig.JsonNames(json));
        List<JsonObject> runs = StatusIndexRig.JsonRows(json);
        Assert.Null(runs[0]["depth"]);
        Assert.Equal(1, runs[1]["depth"]!.GetValue<int>());
        Assert.Equal(parent.Id, runs[1]["run"]!["parent_run"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_nesting_depth_is_capped_at_8_levelsAsync()
    {
        string[] ids = [.. Enumerable.Range(0, 10).Select(i => StatusIndexRig.Id(StatusDocs.Now - Ago(100 - i), Level(i)))];
        _ = _rig.Add(Level(0), _mine, startedAgo: Ago(100), id: ids[0]);
        for (int i = 1; i < 10; i++)
        {
            _ = _rig.Add(Level(i), ["run:" + ids[i - 1]], parent: ids[i - 1], startedAgo: Ago(100 - i), id: ids[i]);
        }

        string[] rows = await PlainRowsAsync(StatusIndexRig.Session("sess-A"));
        (_, string json) = await _rig.CallAsync(StatusIndexRig.Session("sess-A"), "--json");

        Assert.Equal(10, rows.Length);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(2 * Math.Min(i, StatusLayout.MaxDepth), Indent(rows, Level(i)));
        }

        Assert.Equal(8, StatusLayout.MaxDepth);
        Assert.Equal(8, StatusIndexRig.JsonRows(json).Max(r => r["depth"]?.GetValue<int>() ?? 0));
    }

    [Fact]
    public async Task A_parent_run_cycle_does_not_loop_and_every_run_is_listed_onceAsync()
    {
        string a = StatusIndexRig.Id(StatusDocs.Now - Ago(30), "cyc-a");
        string b = StatusIndexRig.Id(StatusDocs.Now - Ago(20), "cyc-b");
        _ = _rig.Add("cyc-a", _mine, parent: b, startedAgo: Ago(30), id: a);
        _ = _rig.Add("cyc-b", _mine, parent: a, startedAgo: Ago(20), id: b);
        _ = _rig.Add("under-a", ["run:" + a], parent: a, startedAgo: Ago(10));

        string[] rows = await PlainRowsAsync(StatusIndexRig.Session("sess-A"));

        Assert.Equal(["cyc-a", "cyc-b", "under-a"], [.. rows.Select(r => NameOf(r)).Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task A_run_that_names_itself_as_its_parent_is_listed_once_at_the_top_levelAsync()
    {
        string id = StatusIndexRig.Id(StatusDocs.Now - Ago(10), "self-parent");
        _ = _rig.Add("self-parent", _mine, parent: id, startedAgo: Ago(10), id: id);

        string[] rows = await PlainRowsAsync(StatusIndexRig.Session("sess-A"));

        _ = Assert.Single(rows);
        Assert.Equal(0, Indent(rows, "self-parent"));
    }

    [Fact]
    public async Task A_run_whose_parent_is_not_listed_stays_at_the_top_levelAsync()
    {
        _ = _rig.Add("orphan-child", _mine, parent: "20250101-000000-removed", startedAgo: Ago(10));

        string[] rows = await PlainRowsAsync(StatusIndexRig.Session("sess-A"));

        Assert.Equal(0, Indent(rows, "orphan-child"));
    }

    [Fact]
    public async Task A_caller_inside_a_worker_is_that_run_and_sees_its_children_but_not_the_inherited_sessions_runsAsync()
    {
        StatusIndexRig.Added parent = _rig.Add("parent-run", _mine, startedAgo: Ago(30));
        _ = _rig.Add("session-sibling", _mine, startedAgo: Ago(25));
        StatusIndexRig.Added child = _rig.Add("child-run", ["run:" + parent.Id], parent: parent.Id, startedAgo: Ago(20));
        _ = _rig.Add("grandchild-run", ["run:" + child.Id], parent: child.Id, startedAgo: Ago(10));
        Func<string, string?> env = StatusIndexRig.Session("sess-A", ("LEAN_WORKER_RUN_ID", parent.Id));

        (_, string plain) = await _rig.CallAsync(env);
        string[] rows = StatusIndexRig.Rows(plain);

        Assert.StartsWith("lean-worker status: owner run:" + parent.Id + ", 2 live", StatusIndexRig.Lines(plain)[0], StringComparison.Ordinal);
        Assert.Equal(2, rows.Length);
        Assert.Equal(0, Indent(rows, "child-run"));
        Assert.Equal(2, Indent(rows, "grandchild-run"));
    }
}
