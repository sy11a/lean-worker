using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Pins the redirect guard contract in .lean-worker/inbox/rg-spec/spec.md (including the v2 and v3 sections) for
/// the opencode plugin (skills/lean-worker/launcher/opencode-plugin.ts). Runs the plugin under node (type-stripped
/// .ts import) via a small .mjs harness, since the guard is logic inside the plugin's "tool.execute.before" hook.
/// </summary>
public sealed class RedirectGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lw-redirect-guard").FullName;
    private readonly List<string> _runDirs = [];

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        foreach (string dir in _runDirs)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private string CreateRunDir()
    {
        string dir = Directory.CreateTempSubdirectory("lw-rundir").FullName;
        _runDirs.Add(dir);
        return dir;
    }

    private static readonly string PluginPath = Path.Combine(AppContext.BaseDirectory, "opencode-plugin.ts");

    // (label, tool, command, expectedOk) -- top table of spec.md, with the two v2 flips applied:
    // "grep -c x f # > note" is now denied (comments are no longer special), and the unterminated heredoc with
    // no ">" anywhere is now allowed by the v2 fast path.
    private static readonly (string Label, string Tool, string Command, bool Ok)[] SpecCases =
    [
        ("allowed:dotnet-build", "bash", "dotnet build 2>&1", true),
        ("allowed:ls-devnull", "bash", "ls 2>/dev/null", true),
        ("allowed:devnull-dup", "bash", "cmd >/dev/null 2>&1", true),
        ("allowed:amp-devnull", "bash", "cmd &>/dev/null", true),
        ("allowed:dup-stderr", "bash", "echo x >&2", true),
        ("allowed:close-fd", "bash", "cmd 2>&-", true),
        ("allowed:inbox-rel", "bash", "echo hi > .lean-worker/inbox/t/out.txt", true),
        ("allowed:inbox-dotslash", "bash", "echo hi >> ./.lean-worker/inbox/t/out.txt", true),
        ("allowed:inbox-absolute", "bash", "echo hi > D/.lean-worker/inbox/t/f", true),
        ("allowed:double-quoted-target", "bash", "echo \"a > b\"", true),
        ("allowed:single-quoted-target", "bash", "echo 'a>b'", true),
        ("allowed:backslash-escaped", "bash", "echo a\\>b", true),
        ("allowed:quoted-format-string", "bash", "git log --format='%h > %s'", true),
        ("allowed:heredoc-body-redirect", "bash", "cat <<EOF\n> x\nEOF", true),
        ("allowed:heredoc-quoted-delim-and-own-line-ok", "bash", "cat <<'EOF' > .lean-worker/inbox/t/f\nline > y\nEOF", true),
        ("allowed:input-redirect", "bash", "wc -l < f.txt", true),
        ("allowed:here-string", "bash", "cat <<< \"a>b\"", true),
        ("allowed:process-substitution-input", "bash", "diff <(ls a) <(ls b)", true),
        ("allowed:plain", "bash", "ls", true),
        ("allowed:non-bash-tool", "read", "echo > x", true),
        ("allowed:unterminated-heredoc-no-gt", "bash", "cat <<EOF\nx", true),

        ("denied:plain-target", "bash", "echo hi > M", false),
        ("denied:comment-then-git-config", "bash", "echo '#x' >> .git/config", false),
        ("denied:no-space-git-config", "bash", "echo x>>.git/config", false),
        ("denied:stderr-file", "bash", "cmd 2> err.log", false),
        ("denied:amp-out", "bash", "cmd &> out", false),
        ("denied:clobber", "bash", "cmd >| f", false),
        ("denied:fd1-append", "bash", "cmd 1>>f", false),
        ("denied:rw-open", "bash", "exec 3<> f", false),
        ("denied:dup-to-file", "bash", "echo x >&f.txt", false),
        ("denied:quoted-git-config", "bash", "echo hi > \".git/config\"", false),
        ("denied:dotdot-escape", "bash", "echo hi > .lean-worker/inbox/../../.git/config", false),
        ("denied:dollar-in-target", "bash", "echo hi > .lean-worker/inbox/$X", false),
        ("denied:tmp", "bash", "echo hi > /tmp/x", false),
        ("denied:inboxx-lookalike", "bash", "echo hi >.lean-worker/inboxx/f", false),
        ("denied:tilde", "bash", "echo > ~/x", false),
        ("denied:after-and", "bash", "ls && echo a > b", false),
        ("denied:inside-command-substitution", "bash", "echo $(echo a > b)", false),
        ("denied:process-substitution-output", "bash", "tee >(cat)", false),
        ("denied:unterminated-quote", "bash", "echo \"unterminated > x", false),
        ("denied:heredoc-own-line-target", "bash", "cat <<EOF > x\nbody\nEOF", false),
        ("denied:no-target-word", "bash", "echo >", false),
        ("denied:comment", "bash", "grep -c x f # > note", false),
    ];

    // Cases whose spec.md target is unambiguous from the "(target)" annotation; others (fail-closed cases with
    // no annotated target) only assert the throw, the message prefix, and the edit-tool/inbox pointer.
    private static readonly Dictionary<string, string> ExpectedTargets = new()
    {
        ["denied:plain-target"] = "M",
        ["denied:comment-then-git-config"] = "unsupported shell syntax: comment",
        ["denied:no-space-git-config"] = ".git/config",
        ["denied:stderr-file"] = "err.log",
        ["denied:amp-out"] = "out",
        ["denied:clobber"] = "f",
        ["denied:fd1-append"] = "f",
        ["denied:rw-open"] = "f",
        ["denied:dup-to-file"] = "f.txt",
        ["denied:quoted-git-config"] = ".git/config",
        ["denied:dotdot-escape"] = ".lean-worker/inbox/../../.git/config",
        ["denied:dollar-in-target"] = ".lean-worker/inbox/$X",
        ["denied:tmp"] = "/tmp/x",
        ["denied:inboxx-lookalike"] = ".lean-worker/inboxx/f",
        ["denied:tilde"] = "~/x",
        ["denied:after-and"] = "b",
        ["denied:inside-command-substitution"] = "b",
        ["denied:process-substitution-output"] = ">(",
        ["denied:heredoc-own-line-target"] = "x",
        ["denied:comment"] = "unsupported shell syntax: comment",
    };

    // Ported from .lean-worker/inbox/rg-review2/conformance.mjs (68 rows), expected verdict adjusted per the
    // v2 section of spec.md: only cont-in-dup, cont-in-heredoc-op, hash-comment and double-slash flip to deny;
    // every other row keeps the table's original expectation (including arith-shift and delim-backslash-common,
    // which the spec calls out as staying "allow").
    private static readonly (string Label, string Command, bool Ok)[] ConformanceCases =
    [
        ("ansic-escaped-quote", "echo $'\\'' >.git/config #'", false),
        ("ansic-plain", "echo $'a>b'", true),
        ("ansic-then-redirect", "echo $'a' > .git/config", false),
        ("locale-dq", "echo $\"a > b\"", true),
        ("locale-dq-escaped", "echo $\"a\\\" > b\" > .git/config", false),
        ("cont-in-op", "echo x >\\\n>.git/config", false),
        ("cont-amp-op", "echo x &\\\n> f", false),
        ("cont-before-target", "echo x \\\n> .git/config", false),
        ("cont-in-target", "echo x > .lean-worker/inbox/a\\\n/../../.git/config", false),
        ("cont-in-dup", "cmd 2>\\\n&1", false),
        ("cont-after-heredoc-op", "cat <<EOF \\\n> .git/config\nEOF", false),
        ("cont-in-heredoc-op", "cat <\\\n<EOF\na > b\nEOF", false),
        ("cont-in-body-delim", "cat <<EOF\nEO\\\nF\necho x > .git/config\nEOF", false),
        ("two-heredocs-order", "cat <<A <<B\nA\nB\necho x > .git/config\nA", false),
        ("two-heredocs-ok", "cat <<A <<B\nx > y\nA\nz > w\nB", true),
        ("delim-partly-quoted", "cat <<\"E\"OF\nEOF\necho x > .git/config\n\"E\"OF", false),
        ("delim-backslash", "cat <<\\EOF\nEOF\necho x > .git/config\n\\EOF", false),
        ("delim-backslash-common", "cat <<\\EOF\na > b\nEOF", true),
        ("delim-space", "cat << EOF\na > b\nEOF", true),
        ("delim-dash-space", "cat <<- 'EOF'\n\ta > b\n\tEOF", true),
        ("delim-quoted-space", "cat <<'E F'\nE F\necho x > .git/config\n'E\n'E", false),
        ("heredoc-in-dq", "echo \"<<EOF\" > .git/config", false),
        ("heredoc-in-sq", "echo '<<EOF'\necho x > .git/config", false),
        ("tab-delim-plain", "cat <<EOF\n\tEOF\necho x > .git/config\nEOF", true),
        ("tab-delim-dash", "cat <<-EOF\n\tEOF\necho x > .git/config", false),
        ("arith-shift", "echo $((1<<2))", true),
        ("arith-shift-hides", "echo $((1<<2))\necho x > .git/config\n2", false),
        ("x>f", "x>f", false),
        ("2>>f", "cmd 2>>f", false),
        ("fdvar", "exec {fd}>.git/config", false),
        (">>&", "echo x >>&1", false),
        ("pipe-amp", "cmd |& tee .lean-worker/inbox/t/f", true),
        ("amp-gt", "cmd &>.git/config", false),
        ("amp-gtgt", "cmd &>>.git/config", false),
        ("gt-amp-file", "cmd >&.git/config", false),
        ("gt-amp-space-digit", "cmd >& 2", true),
        ("amp-gt-digit-is-file", "cmd &>2", false),
        ("fd-gt-amp-quoted-digit", "cmd 2>&\"1\"", true),
        ("brace-group", "{ echo a; } > .git/config", false),
        ("brace-inner", "{ echo a >.git/config; }", false),
        ("subshell", "(echo a)>.git/config", false),
        ("semicolon", "ls;echo a>.git/config", false),
        ("newline", "ls\necho a>.git/config", false),
        ("hash-in-word", "echo a#>.git/config", false),
        ("hash-comment", "echo a #>.git/config", false),
        ("hash-in-param-exp", "echo ${x:- #}>.git/config", false),
        ("hash-in-backticks", "echo `echo #` > .git/config", false),
        ("hash-in-comsub-nl", "echo $(echo a #)\n) > .git/config", false),
        ("dq-comsub-sq", "echo \"$(echo '\"')\" > .git/config #'", false),
        ("dq-comsub-dq", "echo \"$(echo \"a\")\" > .git/config", false),
        ("dotslash-dotdot", "echo x > ./.lean-worker/inbox/x/../../.git/config", false),
        ("trailing-dot", "echo x > .lean-worker/inbox/x/.", true),
        ("double-slash", "echo x > .lean-worker//inbox/x", false),
        ("glued-dq-dotdot", "echo x > .lean-worker/inbox/\"a\"/../../f", false),
        ("sq-dotdot-segments", "echo x > .lean-worker/inbox/'..'/'..'/.git/config", false),
        ("dq-dotdot-segments", "echo x > .lean-worker/inbox/\"..\"/\"..\"/.git/config", false),
        ("split-dotdot", "echo x > .lean-worker/inbox/.''./.''./.git/config", false),
        ("quoted-prefix-glued", "echo x > \".lean-worker/inbox/\"../../.git/config", false),
        ("abs-dir-dotdot", "echo x > D/.lean-worker/inbox/../x", false),
        ("abs-dir-ok", "echo x > D/.lean-worker/inbox/t/f", true),
        ("devnull-glued", "echo x > /dev/null\"\"x", false),
        ("dup", "cmd 2>&1", true),
        ("devnull-dup", "cmd >/dev/null 2>&1", true),
        ("devnull-pipe", "cmd 2>/dev/null | head", true),
        ("dup-pipe", "dotnet test 2>&1 | tail -n 5", true),
        ("devnull-glued-dup", "cmd>/dev/null 2>&1&", true),
        ("procsub-in", "diff <(ls a) <(ls b) 2>/dev/null", true),
        ("herestring", "grep x <<<\"a>b\"", true),
    ];

    // Ported from .lean-worker/inbox/rg-review3/probe.mjs (93 rows), G = ".git/config", I = ".lean-worker/inbox/t/f",
    // D = the plugin's directory (replaced with the real temp root by BuildCasesScript, as for the other sets).
    // Deviations from the probe table, per the v3 section of spec.md: hd-two-order-wrong and
    // q-target-unterminated-sq had wrong expectations in the table and are flipped here (allow, deny); adj-move-fd
    // and adj-dup-quoted-glued are accepted over-blocks, flipped to deny; hd-in-dq-comsub was "allow" in the table
    // but contains both ">" and a heredoc operator together with "$(", which v3 denies outright, so it flips to
    // deny too. Every other row keeps the table's expectation.
    private static readonly (string Label, string Command, bool Ok)[] V3Cases =
    [
        ("ansic-esc-backslash", "echo $'\\\\' > .git/config", false),
        ("ansic-esc-quote-allowed", "echo $'it\\'s' > .lean-worker/inbox/t/f", true),
        ("ansic-dollar-dollar-hides", "cat <<EOF $$'\\' > .git/config\n'\nEOF", false),
        ("ansic-dollar-dollar-comment", "echo $$'\\' > .git/config #'", false),
        ("ansic-escaped-dollar", "echo \\$'\\' > .git/config '", false),
        ("ansic-in-dq-literal", "echo \"$'\" > .git/config \"'\"", false),
        ("ansic-gt-inside", "echo $'a\\'>b'", true),
        ("ansic-unterminated", "echo $'a > b", false),
        ("locale-gt-inside", "echo $\"a>b\" 2>&1", true),
        ("locale-escaped", "echo $\"\\\"\" > .git/config", false),
        ("nest-dq-comsub-comsub", "echo \"$(echo \"$(echo a > .git/config)\")\"", false),
        ("nest-dq-comsub-ok", "x=\"$(echo \"$(ls 2>/dev/null)\")\"", true),
        ("nest-sq-in-comsub-in-dq", "echo \"$(echo ')' > .git/config)\"", false),
        ("nest-paren-in-sq", "echo \"$(echo '(' )\" > .git/config", false),
        ("nest-case-pattern", "echo \"$(case x in a) echo hi > .git/config;; esac)\"", false),
        ("nest-case-pattern-open", "echo \"$(case x in (a) echo hi > .git/config;; esac)\"", false),
        ("nest-comment-paren", "echo \"$(echo # (\n)\" > .git/config; echo \")\"", false),
        ("nest-comment-quote", "echo \"$(echo # '\n)\" > .git/config; echo ')'", false),
        ("nest-ansic-in-comsub", "echo \"$(echo $'\\')' > .git/config)\"", false),
        ("nest-dd-ansic-in-comsub", "echo \"$(echo $$'\\')\" > .git/config ')\"'", false),
        ("nest-dq-escape-in-comsub", "echo \"$(echo \"\\\")\" > .git/config\")\"", false),
        ("nest-procsub-in-dq-comsub", "echo \"$(cat <(ls) > .git/config)\"", false),
        ("nest-subshell", "(cd x && (echo a > .git/config))", false),
        ("nest-comsub-plain", "echo $(echo $(echo a > .git/config))", false),
        ("nest-comsub-ok", "echo \"$(git log --format='%h > %s' 2>&1)\"", true),
        ("hd-two-one-line", "cat <<A <<-B\nx > y\nA\n\tz > w\n\tB", true),
        ("hd-two-order-wrong", "cat <<A <<B\nB\nA\necho x > .git/config\nB", true),
        ("hd-three-semi", "cat <<A; cat <<B; cat <<C > .lean-worker/inbox/t/f\na\nA\nb\nB\nc\nC", true),
        ("hd-redirect-after-body", "cat <<A\nx\nA\necho y > .git/config", false),
        ("hd-newline-in-comsub", "cat <<EOF $(\necho a > .git/config\nEOF\n)\nEOF", false),
        ("hd-newline-in-procsub", "cat <<EOF <(\necho a > .git/config\nEOF\n)\nEOF", false),
        ("hd-newline-in-dq", "cat <<EOF \"\nEOF\n\" > .git/config\nEOF", false),
        ("hd-newline-in-subshell", "cat <<EOF; (\nEOF\necho a > .git/config)", false),
        ("hd-tab-delim-plain", "cat <<EOF\n\tEOF\nb > c\nEOF", true),
        ("hd-tab-delim-dash", "cat <<-EOF\n\t\tEOF\necho x > .git/config", false),
        ("hd-space-delim-dash", "cat <<-EOF\n EOF\necho x > .git/config\nEOF", true),
        ("hd-in-dq", "echo \"<<EOF\" > .git/config", false),
        ("hd-in-sq-next-line", "echo '<<EOF'\necho x > .git/config", false),
        ("hd-in-ansic", "echo $'<<EOF'\necho x > .git/config", false),
        ("hd-arith-cmd-shift", "((x<<y))\necho a > .git/config\ny", false),
        ("hd-arith-cmd-shift-sp", "(( x << y ))\necho a > .git/config\ny", false),
        ("hd-arith-for", "for ((i=0; i<<n; i++)); do :; done\necho a > .git/config\nn", false),
        ("hd-dollar-bracket", "echo $[x<<y ]\necho a > .git/config\ny", false),
        ("hd-let-is-heredoc", "let x<<y\necho a > b\ny", true),
        ("hd-herestring", "cat <<<EOF > .lean-worker/inbox/t/f\necho a > .git/config", false),
        ("hd-delim-glued-gt", "cat <<EOF>.git/config\nx\nEOF", false),
        ("hd-in-dq-comsub", "echo \"$(cat <<EOF\n)\nEOF\n)\" > .lean-worker/inbox/t/f", false),
        ("adj-x>f", "x>.git/config", false),
        ("adj-fd-append", "cmd 2>>.git/config", false),
        ("adj-fdvar", "exec {fd}>.git/config", false),
        ("adj-fdvar-close", "exec {fd}>&-", true),
        ("adj-move-fd", "cmd 3>&1-", false),
        ("adj-dup-quoted-glued", "cmd 2>&\"1\"2", false),
        ("adj-gt-amp-gt", "cmd 2>&1>.git/config", false),
        ("adj-pipe-amp-gt", "cmd |&>.git/config", false),
        ("adj-and-and-gt", "ls&&>.git/config", false),
        ("adj-clobber-amp", "cmd >|.git/config", false),
        ("adj-rw", "exec 3<>.git/config", false),
        ("adj-lt-amp-then-gt", "cmd <&0>.git/config", false),
        ("adj-target-then-paren", "(echo a>.lean-worker/inbox/t/f)>.git/config", false),
        ("adj-gt-gt-space", "echo a > > .git/config", false),
        ("adj-hash-word", "echo a#>.git/config", false),
        ("adj-comment-quote-hides", "echo a #'\necho x > .git/config #'", false),
        ("adj-comment-dq-hides", "ls # \"\necho x > .git/config # \"", false),
        ("adj-brace-group", "{ echo a; }>.git/config", false),
        ("adj-brace-inner", "{ echo a>.lean-worker/inbox/t/f; echo b>.git/config; }", false),
        ("q-dq-before-op", "echo \"a\">.git/config", false),
        ("q-sq-before-op", "echo 'a'>>.git/config", false),
        ("q-target-dq-glued", "echo a >\".git\"/config", false),
        ("q-target-empty-dq-prefix", "echo a >\"\".git/config", false),
        ("q-target-inbox-split", "echo a >.lean-worker/\"inbox\"/t/f", true),
        ("q-target-quoted-gt", "echo a >\".lean-worker/inbox/a>b\"", true),
        ("q-target-sq-in-dq", "echo a >\".lean-worker/inbox/'\"'..'/../x", false),
        ("q-target-dq-in-sq-dotdot", "echo a >'.lean-worker/inbox/\"'/..'\"/../x", false),
        ("q-target-unterminated-dq", "echo a > .lean-worker/inbox/a\"", false),
        ("q-target-unterminated-sq", "echo a > .lean-worker/inbox/a'\n' > .git/config", false),
        ("q-target-ansic", "echo a > $'.git/config'", false),
        ("q-target-locale", "echo a > $\".lean-worker/inbox/t/f\"", false),
        ("q-target-dot-slash-dotdot", "echo x > ./.lean-worker/inbox/x/../..", false),
        ("q-target-trailing-dot", "echo x > .lean-worker/inbox/x/.", true),
        ("q-target-glued-dotdot", "echo x > .lean-worker/inbox/\"a\"/../../f", false),
        ("q-target-abs", "echo x > D/.lean-worker/inbox/t/f", true),
        ("q-target-abs-other", "echo x > /w/repo2/.lean-worker/inbox/t/f", false),
        ("ok-dup", "dotnet build 2>&1", true),
        ("ok-devnull-dup", "cmd >/dev/null 2>&1", true),
        ("ok-devnull-pipe", "cmd 2>/dev/null | head", true),
        ("ok-amp-devnull", "cmd &>/dev/null", true),
        ("ok-to-stderr", "echo x >&2", true),
        ("ok-test-escaped-gt", "test a \\> b && echo y", true),
        ("ok-find-exec", "find . -name '*.cs' -exec grep -l x {} \\; 2>/dev/null", true),
        ("ok-grep-dq-alt", "dotnet test 2>&1 | grep -E \"^(Passed|Failed)\" | tail -n 5", true),
        ("ok-heredoc-inbox", "cat <<'EOF' > .lean-worker/inbox/t/f\na > b\nEOF", true),
        ("ok-if-gt", "if [ \"$(ls | wc -l)\" -gt 0 ]; then echo x >&2; fi", true),
        ("unterminated-dq-target-added", "echo a > .lean-worker/inbox/a\"", false),
    ];

    // Round-1 test gaps named by the task: &>>, <<- with a tab-indented closing delimiter, <<"EOF", <&,
    // redirects after ||, |, inside ( ) and $( ), and [[ a > b ]].
    private static readonly (string Label, string Command, bool Ok, string? Target)[] GapCases =
    [
        ("gap:amp-gtgt-allowed", "cmd &>> .lean-worker/inbox/t/f", true, null),
        ("gap:amp-gtgt-denied", "cmd &>> /tmp/x", false, "/tmp/x"),
        ("gap:heredoc-dash-tab-closing-delim", "cat <<-EOF\n\tx > y\n\tEOF", true, null),
        ("gap:heredoc-double-quoted-delim", "cat <<\"EOF\"\na > b\nEOF", true, null),
        ("gap:dup-input-fd", "exec 3<&4", true, null),
        ("gap:redirect-after-or", "ls || echo a > .git/config", false, ".git/config"),
        ("gap:redirect-after-pipe", "ls | cat > .git/config", false, ".git/config"),
        ("gap:redirect-inside-subshell-parens", "(echo a > .git/config)", false, ".git/config"),
        ("gap:redirect-inside-comsub-parens", "x=$(echo a > .git/config)", false, ".git/config"),
        ("gap:double-bracket-gt-denied", "[[ a > b ]]", false, "b"),
    ];

    [Fact]
    public async Task Every_spec_case_matches_its_expected_outcomeAsync()
    {
        RequireNode();
        var cases = SpecCases.Select(c => (c.Label, c.Tool, c.Command)).ToArray();
        Dictionary<string, HookResult> results = await RunHarnessAsync(BuildCasesScript(cases));

        List<string> failures = [];
        foreach ((string label, string tool, string command, bool ok) in SpecCases)
        {
            CheckCase(failures, results, label, $"{tool} {command.ReplaceLineEndings("\\n")}", ok,
                ExpectedTargets.GetValueOrDefault(label));
        }
        if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
    }

    [Fact]
    public async Task Every_conformance_case_matches_its_v2_expected_outcomeAsync()
    {
        RequireNode();
        Assert.Equal(68, ConformanceCases.Length);
        var cases = ConformanceCases.Select(c => (c.Label, "bash", c.Command)).ToArray();
        Dictionary<string, HookResult> results = await RunHarnessAsync(BuildCasesScript(cases));

        List<string> failures = [];
        foreach ((string label, string command, bool ok) in ConformanceCases)
        {
            CheckCase(failures, results, label, command.ReplaceLineEndings("\\n"), ok, expectedTarget: null);
        }
        if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
    }

    [Fact]
    public async Task Every_v3_case_matches_its_expected_outcomeAsync()
    {
        RequireNode();
        Assert.Equal(94, V3Cases.Length);
        var cases = V3Cases.Select(c => (c.Label, "bash", c.Command)).ToArray();
        Dictionary<string, HookResult> results = await RunHarnessAsync(BuildCasesScript(cases));

        List<string> failures = [];
        foreach ((string label, string command, bool ok) in V3Cases)
        {
            CheckCase(failures, results, label, command.ReplaceLineEndings("\\n"), ok, expectedTarget: null);
        }
        if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
    }

    [Fact]
    public async Task Round_1_gap_cases_match_expected_outcomeAsync()
    {
        RequireNode();
        var cases = GapCases.Select(c => (c.Label, "bash", c.Command)).ToArray();
        Dictionary<string, HookResult> results = await RunHarnessAsync(BuildCasesScript(cases));

        List<string> failures = [];
        foreach ((string label, string command, bool ok, string? target) in GapCases)
        {
            CheckCase(failures, results, label, command.ReplaceLineEndings("\\n"), ok, target);
        }
        if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
    }

    // Round-4 test gaps named by the task: $$, case patterns and ${...} denied with "unsupported shell syntax"
    // on their own (no "#" involved); here-strings carrying a command substitution are allowed; extglob targets
    // (@(...) and !(...)) that walk out of the inbox via ".." are denied; and the "#35" row documents an accepted
    // over-block (denied only because "#" anywhere in the command is treated as an unsupported comment).
    private static readonly (string Label, string Command, bool Ok, string? Target)[] GapCases4 =
    [
        ("gap4:pid-expansion", "echo $$ > .lean-worker/inbox/t/f", false, "unsupported shell syntax"),
        ("gap4:case-pattern", "case x in a) echo a > f;; esac", false, "unsupported shell syntax"),
        ("gap4:param-expansion", "echo ${HOME} > .lean-worker/inbox/t/f", false, "unsupported shell syntax"),
        ("gap4:herestring-comsub-allowed", "jq . <<< \"$(cat f)\" > .lean-worker/inbox/t/f", true, null),
        ("gap4:herestring-comsub-git-log", "grep x <<< \"$(git log)\" 2>&1", true, null),
        ("gap4:extglob-at-dotdot-escape", "echo a > .lean-worker/inbox/@(t)/../../../.git/config", false, ".lean-worker/inbox/@"),
        ("gap4:extglob-bang-dotdot-escape", "echo a > .lean-worker/inbox/!(zz)/../../../.git/config", false, ".lean-worker/inbox/!"),
        ("gap4:accepted-overblock-hash-in-pipe", "git log --oneline 2>&1 | grep \"#35\"", false, null),
    ];

    [Fact]
    public async Task Round_4_gap_cases_match_expected_outcomeAsync()
    {
        RequireNode();
        var cases = GapCases4.Select(c => (c.Label, "bash", c.Command)).ToArray();
        Dictionary<string, HookResult> results = await RunHarnessAsync(BuildCasesScript(cases));

        List<string> failures = [];
        foreach ((string label, string command, bool ok, string? target) in GapCases4)
        {
            CheckCase(failures, results, label, command.ReplaceLineEndings("\\n"), ok, target);
        }
        if (failures.Count > 0) Assert.Fail(string.Join("\n", failures));
    }

    private static void CheckCase(List<string> failures, Dictionary<string, HookResult> results, string label,
        string describedCommand, bool expectedOk, string? expectedTarget)
    {
        if (!results.TryGetValue(label, out HookResult? r))
        {
            failures.Add($"{label} ({describedCommand}): missing result");
            return;
        }
        if (expectedOk)
        {
            if (!r.Ok) failures.Add($"{label} ({describedCommand}): expected allow but denied: {r.Message}");
            return;
        }
        if (r.Ok)
        {
            failures.Add($"{label} ({describedCommand}): expected deny but was allowed");
            return;
        }
        if (!r.Message!.StartsWith("lean-worker: redirect", StringComparison.Ordinal))
        {
            failures.Add($"{label} ({describedCommand}): message missing 'lean-worker: redirect' prefix: {r.Message}");
        }
        if (!r.Message.Contains("edit tool", StringComparison.Ordinal) &&
            !r.Message.Contains(".lean-worker/inbox/", StringComparison.Ordinal))
        {
            failures.Add($"{label} ({describedCommand}): message doesn't point to the edit tool or .lean-worker/inbox/: {r.Message}");
        }
        if (expectedTarget != null && !r.Message.Contains(expectedTarget, StringComparison.Ordinal))
        {
            failures.Add($"{label} ({describedCommand}): message missing target '{expectedTarget}': {r.Message}");
        }
    }

    [Fact]
    public async Task Redirect_denial_logs_a_well_shaped_redirect_denied_line_to_redirect_denied_logAsync()
    {
        RequireNode();
        string runDir = CreateRunDir();
        string script = $$"""
            const { LeanWorkerWrapUp } = await import({{JsonSerializer.Serialize(new Uri(PluginPath).AbsoluteUri)}});
            process.env.LEAN_WORKER_RUN_DIR = {{JsonSerializer.Serialize(runDir)}};
            process.env.LEAN_WORKER_WRAPUP = "1";
            const ctx = await LeanWorkerWrapUp({ directory: {{JsonSerializer.Serialize(_root)}}, worktree: {{JsonSerializer.Serialize(_root)}} });
            try {
              await ctx["tool.execute.before"]({ tool: "bash", sessionID: "s", callID: "c" }, { args: { command: "echo hi > /tmp/x" } });
              console.log(JSON.stringify({ name: "case", ok: true }));
            } catch (e) {
              console.log(JSON.stringify({ name: "case", ok: false, message: e.message }));
            }
            """;
        await RunHarnessAsync(script);

        string redirectLogPath = Path.Combine(runDir, "redirect-denied.log");
        Assert.True(File.Exists(redirectLogPath), $"expected {redirectLogPath} to exist");
        string redirectLog = await File.ReadAllTextAsync(redirectLogPath, TestContext.Current.CancellationToken);
        string[] redirectLines = redirectLog.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(redirectLines);
        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z redirect-denied \S+$",
            redirectLines[0]);
        Assert.Contains("/tmp/x", redirectLines[0], StringComparison.Ordinal);

        string hookLogPath = Path.Combine(runDir, "hook.log");
        Assert.True(File.Exists(hookLogPath), $"expected {hookLogPath} to exist");
        string hookLog = await File.ReadAllTextAsync(hookLogPath, TestContext.Current.CancellationToken);
        string[] hookLines = hookLog.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(hookLines);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z bash$", hookLines[0]);
    }

    [Fact]
    public async Task Wrapup_marker_wins_over_a_denied_redirect_commandAsync()
    {
        RequireNode();
        string runDir = CreateRunDir();
        await File.WriteAllTextAsync(Path.Combine(runDir, "wrapup.json"), """{"reason":"budget spent"}""", TestContext.Current.CancellationToken);
        string script = $$"""
            const { LeanWorkerWrapUp } = await import({{JsonSerializer.Serialize(new Uri(PluginPath).AbsoluteUri)}});
            process.env.LEAN_WORKER_RUN_DIR = {{JsonSerializer.Serialize(runDir)}};
            process.env.LEAN_WORKER_WRAPUP = "1";
            const ctx = await LeanWorkerWrapUp({ directory: {{JsonSerializer.Serialize(_root)}}, worktree: {{JsonSerializer.Serialize(_root)}} });
            try {
              await ctx["tool.execute.before"]({ tool: "bash", sessionID: "s", callID: "c" }, { args: { command: "echo hi > /tmp/x" } });
              console.log(JSON.stringify({ name: "case", ok: true }));
            } catch (e) {
              console.log(JSON.stringify({ name: "case", ok: false, message: e.message }));
            }
            """;
        Dictionary<string, HookResult> results = await RunHarnessAsync(script);
        Assert.False(results["case"].Ok);
        Assert.Equal("budget spent", results["case"].Message);
        Assert.False(File.Exists(Path.Combine(runDir, "redirect-denied.log")));
    }

    [Fact]
    public async Task Hook_exists_when_wrapup_is_zeroAsync()
    {
        RequireNode();
        string script = $$"""
            const { LeanWorkerWrapUp } = await import({{JsonSerializer.Serialize(new Uri(PluginPath).AbsoluteUri)}});
            delete process.env.LEAN_WORKER_RUN_DIR;
            process.env.LEAN_WORKER_WRAPUP = "0";
            const ctx = await LeanWorkerWrapUp({ directory: {{JsonSerializer.Serialize(_root)}}, worktree: {{JsonSerializer.Serialize(_root)}} });
            console.log(JSON.stringify({ name: "case", ok: typeof ctx["tool.execute.before"] === "function" }));
            """;
        Dictionary<string, HookResult> results = await RunHarnessAsync(script);
        Assert.True(results["case"].Ok);
    }

    [Fact]
    public async Task Wrapup_marker_still_throws_its_reason_for_any_tool_including_readAsync()
    {
        RequireNode();
        string runDir = CreateRunDir();
        await File.WriteAllTextAsync(Path.Combine(runDir, "wrapup.json"), """{"reason":"budget spent"}""", TestContext.Current.CancellationToken);
        string script = $$"""
            const { LeanWorkerWrapUp } = await import({{JsonSerializer.Serialize(new Uri(PluginPath).AbsoluteUri)}});
            process.env.LEAN_WORKER_RUN_DIR = {{JsonSerializer.Serialize(runDir)}};
            process.env.LEAN_WORKER_WRAPUP = "1";
            const ctx = await LeanWorkerWrapUp({ directory: {{JsonSerializer.Serialize(_root)}}, worktree: {{JsonSerializer.Serialize(_root)}} });
            try {
              await ctx["tool.execute.before"]({ tool: "read", sessionID: "s", callID: "c" }, { args: {} });
              console.log(JSON.stringify({ name: "case", ok: true }));
            } catch (e) {
              console.log(JSON.stringify({ name: "case", ok: false, message: e.message }));
            }
            """;
        Dictionary<string, HookResult> results = await RunHarnessAsync(script);
        Assert.False(results["case"].Ok);
        Assert.Equal("budget spent", results["case"].Message);
    }

    [Fact]
    public async Task Wrapup_without_marker_file_lets_ls_through_and_logs_the_toolAsync()
    {
        RequireNode();
        string runDir = CreateRunDir();
        string script = $$"""
            const { LeanWorkerWrapUp } = await import({{JsonSerializer.Serialize(new Uri(PluginPath).AbsoluteUri)}});
            process.env.LEAN_WORKER_RUN_DIR = {{JsonSerializer.Serialize(runDir)}};
            process.env.LEAN_WORKER_WRAPUP = "1";
            const ctx = await LeanWorkerWrapUp({ directory: {{JsonSerializer.Serialize(_root)}}, worktree: {{JsonSerializer.Serialize(_root)}} });
            try {
              await ctx["tool.execute.before"]({ tool: "bash", sessionID: "s", callID: "c" }, { args: { command: "ls" } });
              console.log(JSON.stringify({ name: "case", ok: true }));
            } catch (e) {
              console.log(JSON.stringify({ name: "case", ok: false, message: e.message }));
            }
            """;
        Dictionary<string, HookResult> results = await RunHarnessAsync(script);
        Assert.True(results["case"].Ok);

        string hookLog = await File.ReadAllTextAsync(Path.Combine(runDir, "hook.log"), TestContext.Current.CancellationToken);
        string[] lines = hookLog.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        string[] parts = lines[0].Split(' ', 2);
        Assert.True(DateTime.TryParse(parts[0], out _), $"expected an ISO timestamp, got: {lines[0]}");
        Assert.Equal("bash", parts[1]);
    }

    private string BuildCasesScript(IReadOnlyList<(string Label, string Tool, string Command)> cases)
    {
        StringBuilder sb = new();
        sb.AppendLine($"const {{ LeanWorkerWrapUp }} = await import({JsonSerializer.Serialize(new Uri(PluginPath).AbsoluteUri)});");
        sb.AppendLine("delete process.env.LEAN_WORKER_RUN_DIR;");
        sb.AppendLine("delete process.env.LEAN_WORKER_WRAPUP;");
        sb.AppendLine($"const ctx = await LeanWorkerWrapUp({{ directory: {JsonSerializer.Serialize(_root)}, worktree: {JsonSerializer.Serialize(_root)} }});");
        foreach ((string label, string tool, string command) in cases)
        {
            string resolvedCommand = command.Replace("D/.lean-worker/inbox", $"{_root}/.lean-worker/inbox", StringComparison.Ordinal);
            sb.AppendLine("try {");
            sb.AppendLine($"  await ctx[\"tool.execute.before\"]({{ tool: {JsonSerializer.Serialize(tool)}, sessionID: \"s\", callID: \"c\" }}, {{ args: {{ command: {JsonSerializer.Serialize(resolvedCommand)} }} }});");
            sb.AppendLine($"  console.log(JSON.stringify({{ name: {JsonSerializer.Serialize(label)}, ok: true }}));");
            sb.AppendLine("} catch (e) {");
            sb.AppendLine($"  console.log(JSON.stringify({{ name: {JsonSerializer.Serialize(label)}, ok: false, message: e.message }}));");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    private async Task<Dictionary<string, HookResult>> RunHarnessAsync(string script)
    {
        string harnessPath = Path.Combine(_root, $"harness-{Guid.NewGuid():N}.mjs");
        await File.WriteAllTextAsync(harnessPath, script, TestContext.Current.CancellationToken);

        ProcessStartInfo psi = new()
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(harnessPath);

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("could not start node");
        string stdout = await p.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        string stderr = await p.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await p.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(p.ExitCode == 0, $"node harness exited {p.ExitCode}, stderr:\n{stderr}");

        Dictionary<string, HookResult> results = [];
        foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonDocument doc = JsonDocument.Parse(line);
            string name = doc.RootElement.GetProperty("name").GetString()!;
            bool ok = doc.RootElement.GetProperty("ok").GetBoolean();
            string? message = doc.RootElement.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            results[name] = new HookResult(ok, message);
        }
        return results;
    }

    private static void RequireNode()
    {
        try
        {
            using Process p = Process.Start(new ProcessStartInfo("node", "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
            p.WaitForExit();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("node is required on PATH to run RedirectGuardTests", e);
        }
    }

    private sealed record HookResult(bool Ok, string? Message);
}
