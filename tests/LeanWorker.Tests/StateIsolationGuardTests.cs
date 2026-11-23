using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The guard that keeps the suite out of the developer's own run index: every launch writes a pointer file under
/// $LEAN_WORKER_STATE_DIR/runs, else $XDG_STATE_HOME/lean-worker/runs, else ~/.local/state/lean-worker/runs, and the
/// tests that launch (in this process or in a child) must point the first variable at a folder of their own. The
/// helpers are checked by launching with HOME and XDG_STATE_HOME pointed at empty folders (the "real" index of these
/// tests) and finding both still empty; the launching test files are checked by reading their source.
/// </summary>
[Collection("launcher-process-state")]
public sealed partial class StateIsolationGuardTests : IDisposable
{
    private const string CollectionName = "launcher-process-state";

    // The ways a test file starts a launcher itself: a call of Launcher.RunAsync, a LaunchRun, the public RunStatus.Open, the launcher's dll.
    private static readonly string[] _directCalls = ["Launcher.RunAsync(", "new LaunchRun(", "RunStatus.Open(", "\"LeanWorker.dll\""];

    // The helpers that start a launcher and point its run index at a folder of the test themselves.
    private static readonly string[] _helperCalls = ["RunAsyncGolden.RunAsync(", "LauncherProcess.StartInfo(", "LauncherProcess.RunAsync("];

    private readonly TempDirs _dirs = new();

    public void Dispose() => _dirs.Dispose();

    private static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private static bool HasPointers(string home)
    {
        bool folder = Directory.EnumerateDirectories(home, "lean-worker", SearchOption.AllDirectories).Any();
        return folder || Directory.EnumerateFiles(home, "*.json", SearchOption.AllDirectories).Any();
    }

    [Fact]
    public async Task A_launch_through_the_run_helper_leaves_the_home_and_xdg_state_folders_emptyAsync()
    {
        string home = _dirs.Create("lw-guard-home");
        string xdg = _dirs.Create("lw-guard-xdg");
        using EnvScope env = EnvScope.Clean(("HOME", home), ("XDG_STATE_HOME", xdg), (StateDirScope.Variable, null));

        (int code, string stdout) = await RunAsyncGolden.RunAsync(_dirs.NewRoot(), RunAsyncGolden.SuccessStream);

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(RunAsyncGolden.RunDirFrom(stdout), "status.json")));
        Assert.False(HasPointers(home), "a pointer reached $HOME/.local/state");
        Assert.False(HasPointers(xdg), "a pointer reached $XDG_STATE_HOME");
        Assert.Null(Environment.GetEnvironmentVariable(StateDirScope.Variable));
    }

    [Fact]
    public async Task The_same_launch_without_the_helpers_folder_does_write_to_the_xdg_folder_so_the_guard_can_failAsync()
    {
        string home = _dirs.Create("lw-guard-home");
        string xdg = _dirs.Create("lw-guard-xdg");
        using EnvScope env = EnvScope.Clean(("HOME", home), ("XDG_STATE_HOME", xdg));

        (int code, _) = await RunAsyncGolden.RunAsync(_dirs.NewRoot(), RunAsyncGolden.SuccessStream, environment: [(StateDirScope.Variable, null)]);

        Assert.Equal(0, code);
        Assert.True(HasPointers(xdg));
        Assert.False(HasPointers(home));
    }

    [Fact]
    public async Task A_launch_through_the_run_helper_writes_its_pointer_to_the_folder_the_test_gaveAsync()
    {
        string state = _dirs.Create("lw-guard-state");

        _ = await RunAsyncGolden.RunAsync(_dirs.NewRoot(), RunAsyncGolden.SuccessStream, stateDir: state);

        _ = Assert.Single(Directory.GetFiles(Path.Combine(state, "runs")));
    }

    [Fact]
    public void The_command_sandbox_points_the_run_index_into_itself_and_gives_the_old_value_back()
    {
        string? before = Environment.GetEnvironmentVariable(StateDirScope.Variable);
        string indexInside;
        string stateDir;
        using (CommandSandbox sandbox = new())
        {
            stateDir = sandbox.StateDir;
            indexInside = StatusIndex.Folder(name => Environment.GetEnvironmentVariable(name))!;
            Assert.Equal(sandbox.StateDir, Environment.GetEnvironmentVariable(StateDirScope.Variable));
        }

        Assert.Equal(Path.Combine(stateDir, "runs"), indexInside);
        Assert.Equal(before, Environment.GetEnvironmentVariable(StateDirScope.Variable));
    }

    [Fact]
    public void A_state_dir_scope_sets_the_variable_and_gives_the_old_value_back_and_removes_its_own_folder()
    {
        using EnvScope outer = new((StateDirScope.Variable, "/outer/state"));
        string root;
        using (StateDirScope scope = new())
        {
            root = scope.Root;
            Assert.Equal(root, Environment.GetEnvironmentVariable(StateDirScope.Variable));
            Assert.Equal(Path.Combine(root, "runs"), StatusIndex.Folder(name => Environment.GetEnvironmentVariable(name)));
            Assert.True(Directory.Exists(root));
        }

        Assert.Equal("/outer/state", Environment.GetEnvironmentVariable(StateDirScope.Variable));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_state_dir_scope_over_a_folder_of_the_test_leaves_the_folder_alone()
    {
        string mine = _dirs.Create("lw-guard-mine");

        using (StateDirScope scope = new(mine))
        {
            Assert.Equal(mine, scope.Root);
        }

        Assert.True(Directory.Exists(mine));
    }

    private static Dictionary<string, string> Sources()
    {
        string dir = SourceDir();
        Assert.SkipUnless(Directory.Exists(dir), "the test sources are not next to this build");
        Dictionary<string, string> files = Directory.GetFiles(dir, "*.cs").ToDictionary(f => Path.GetFileName(f), f => File.ReadAllText(f), StringComparer.Ordinal);
        int launching = files.Values.Count(s => Launches(Blank(s, keepStrings: true)));
        Assert.True(launching >= 10, "the scan found only " + launching.ToString(CultureInfo.InvariantCulture) + " launching files; it looks at the wrong folder");
        return files;
    }

    [Fact]
    public void Every_test_file_that_launches_in_this_process_or_in_a_child_points_the_run_index_at_a_folder_of_its_own() =>
        Assert.Empty(Violations(Sources(), "run index"));

    [Fact]
    public void Every_test_class_that_launches_is_in_the_launcher_process_state_collection() =>
        Assert.Empty(Violations(Sources(), "collection"));

    [Fact]
    public void The_launch_helpers_give_the_launcher_a_run_index_of_their_own_in_code()
    {
        Dictionary<string, string> files = Sources();

        Assert.Contains("StateDirScope", Blank(files["RunAsyncGolden.cs"], keepStrings: false), StringComparison.Ordinal);
        Assert.Contains("StateDirScope.Variable", Blank(files["LauncherProcess.cs"], keepStrings: false), StringComparison.Ordinal);
    }

    [Fact]
    public void A_test_class_that_launches_through_a_helper_without_the_collection_is_found()
    {
        // What DenyFloorTests was before it got the attribute: public class, RunAsyncGolden.RunAsync, no collection.
        string[] found = Violations(Files(("A.cs", "public class DenyFloorLike\n{\n    [Fact]\n    public async Task T() => _ = await RunAsyncGolden.RunAsync(root, script);\n}\n")));

        string line = Assert.Single(found);
        Assert.Contains("DenyFloorLike", line, StringComparison.Ordinal);
        Assert.Contains("collection", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_test_class_in_the_collection_that_launches_through_a_helper_is_not_found()
    {
        const string Source = "[Collection(\"launcher-process-state\")]\npublic sealed class Fine\n{\n    [Fact]\n    public async Task T() => _ = await RunAsyncGolden.RunAsync(root, script);\n}\n";

        Assert.Empty(Violations(Files(("A.cs", Source))));
    }

    [Fact]
    public void The_collection_of_one_part_of_a_partial_class_counts_for_the_other_part()
    {
        const string First = "[Collection(\"launcher-process-state\")]\npublic sealed partial class Split\n{\n}\n";
        const string Second = "public sealed partial class Split\n{\n    [Fact]\n    public async Task T() => _ = await RunAsyncGolden.RunAsync(root, script);\n}\n";

        Assert.Empty(Violations(Files(("A.cs", First), ("B.cs", Second))));
    }

    [Fact]
    public void A_direct_launch_that_names_StateDirScope_only_in_a_comment_or_a_string_is_found()
    {
        const string Comment = "[Collection(\"launcher-process-state\")]\npublic sealed class C\n{\n    // StateDirScope would go here\n    [Fact]\n    public async Task T() => _ = await Launcher.RunAsync(o);\n}\n";
        const string Text = "[Collection(\"launcher-process-state\")]\npublic sealed class D\n{\n    /* using StateDirScope scope = new(); */\n    private const string S = \"StateDirScope\";\n    [Fact]\n    public async Task T() => _ = await Launcher.RunAsync(o);\n}\n";

        string[] found = Violations(Files(("A.cs", Comment), ("B.cs", Text)));

        Assert.Equal(2, found.Length);
        Assert.All(found, v => Assert.Contains("run index", v, StringComparison.Ordinal));
    }

    [Fact]
    public void A_direct_launch_that_really_uses_a_StateDirScope_or_the_variable_is_not_found()
    {
        const string Scope = "[Collection(\"launcher-process-state\")]\npublic sealed class C\n{\n    [Fact]\n    public async Task T()\n    {\n        using StateDirScope state = new();\n        _ = await Launcher.RunAsync(o);\n    }\n}\n";
        const string Variable = "[Collection(\"launcher-process-state\")]\npublic sealed class D\n{\n    [Fact]\n    public async Task T()\n    {\n        using EnvScope env = new((\"LEAN_WORKER_STATE_DIR\", dir));\n        _ = await Launcher.RunAsync(o);\n    }\n}\n";

        Assert.Empty(Violations(Files(("A.cs", Scope), ("B.cs", Variable))));
    }

    private static Dictionary<string, string> Files(params (string Name, string Source)[] files) =>
        files.ToDictionary(f => f.Name, f => f.Source, StringComparer.Ordinal);

    private static bool Launches(string text) =>
        _directCalls.Concat(_helperCalls).Any(c => text.Contains(c, StringComparison.Ordinal));

    // What is wrong with the test classes that launch: one line per file or class, naming "run index" or "collection"
    // (only the lines that name `kind`, when it is given).
    private static string[] Violations(Dictionary<string, string> sources, string kind = "")
    {
        string[] members = [.. sources.Values.SelectMany(s => PublicClasses(Blank(s, keepStrings: true))).Where(c => c.InCollection).Select(c => c.Name)];
        List<string> found = [];
        foreach ((string file, string source) in sources)
        {
            string text = Blank(source, keepStrings: true);
            string code = Blank(source, keepStrings: false);
            bool hasTests = text.Contains("[Fact", StringComparison.Ordinal) || text.Contains("[Theory", StringComparison.Ordinal);
            if (!hasTests || !Launches(text))
            {
                continue;
            }

            // A direct launch needs the folder in the file's own code: StateDirScope, or the variable's name as a value.
            bool direct = _directCalls.Any(c => text.Contains(c, StringComparison.Ordinal));
            bool isolated = code.Contains("StateDirScope", StringComparison.Ordinal) || text.Contains("\"" + StateDirScope.Variable + "\"", StringComparison.Ordinal);
            if (direct && !isolated)
            {
                found.Add(file + ": launches without a StateDirScope or the " + StateDirScope.Variable + " variable in its code (the run index is the developer's own)");
            }

            found.AddRange(PublicClasses(text).Where(c => !members.Contains(c.Name, StringComparer.Ordinal)).Select(c => file + ": " + c.Name + " launches but is not in the " + CollectionName + " collection"));
        }

        return [.. found.Where(v => v.Contains(kind, StringComparison.Ordinal))];
    }

    private sealed record ClassDecl(string Name, bool InCollection);

    // The public classes declared at the left margin, with whether their attributes name the collection.
    private static IEnumerable<ClassDecl> PublicClasses(string text) =>
        ClassPattern().Matches(text).Select(m => new ClassDecl(m.Groups["name"].Value, m.Groups["attrs"].Value.Contains("\"" + CollectionName + "\"", StringComparison.Ordinal)));

    // The source with comments blanked, and strings too unless they are kept: what is left is what the compiler sees.
    private static string Blank(string source, bool keepStrings) =>
        TokenPattern().Replace(source, m => Blanked(m, keepStrings));

    private static string Blanked(Match token, bool keepStrings)
    {
        if (token.Groups["line"].Success || token.Groups["block"].Success)
        {
            return " ";
        }

        return keepStrings ? token.Value : "\"\"";
    }

    [GeneratedRegex(""""(?<raw>"""[\s\S]*?""")|(?<verbatim>@"(?:[^"]|"")*")|(?<regular>"(?:[^"\\\r\n]|\\.)*")|(?<char>'(?:[^'\\\r\n]|\\.)+')|(?<line>//[^\r\n]*)|(?<block>/\*[\s\S]*?\*/)"""", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"(?m)^(?<attrs>(?:\[[^\]\r\n]*\][ \t]*\r?\n)*)public\s+(?:sealed\s+|abstract\s+|partial\s+)*class\s+(?<name>\w+)", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ClassPattern();
}
