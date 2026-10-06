using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// Pins the quote-aware pre-check (maskQuoted in opencode-plugin.ts) against the 108-row conformance table
/// in .lean-worker/inbox/qa-review2/conformance.mjs, judged against bash's own quoting rules.
/// </summary>
public sealed class MaskQuotedConformanceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lw-maskquoted").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly string PluginPath = Path.Combine(AppContext.BaseDirectory, "opencode-plugin.ts");

    private const string C = "unsupported shell syntax: comment";
    private const string A = "unsupported shell syntax: arithmetic command";
    private const string K = "unsupported shell syntax: case";
    private const string P = "unsupported shell syntax: pid";
    private const string DN = " > /dev/null";

    // Transcribed row-for-row from .lean-worker/inbox/qa-review2/conformance.mjs (108 rows).
    private static readonly (string Label, string Command, bool Ok, string? Reason)[] Rows =
    [
        // escapes outside quotes
        ("esc-bs-then-sq", "echo \\\\'#'" + DN, true, null),
        ("esc-sq-hash", "echo \\'#\\'" + DN, false, C),
        ("esc-dq-hash", "echo \\\"#\\\"" + DN, false, C),
        ("esc-dollar-sq", "echo \\$'#'" + DN, true, null),
        ("esc-dollar-dq", "echo \\$\"#\"" + DN, true, null),
        ("esc-dollar-sq-bs", "echo \\$'a\\'#'" + DN, false, C),
        ("esc-hash", "echo \\#" + DN, false, C),
        ("esc-bs-dq", "echo \\\\\"#\"" + DN, true, null),
        ("esc-bs-esc-sq", "echo \\\\\\'#'" + DN, false, C),
        // $$ before quotes
        ("pid-sq", "echo $$'#'" + DN, false, P),
        ("pid3-ansi", "echo $$$'#'" + DN, false, P),
        ("pid-dq", "echo $$\"#\"" + DN, false, P),
        ("pid-locale", "echo $$$\"#\"" + DN, false, P),
        // $'..'
        ("ansi-bsbs-close", "jq $'a\\\\' '#' f" + DN, true, null),
        ("ansi-bsbs-hash-out", "jq $'a\\\\'#' f" + DN, false, C),
        ("ansi-esc-sq", "jq $'\\'#' f" + DN, true, null),
        ("ansi-hex", "jq $'#\\x27' f" + DN, true, null),
        ("ansi-bs-esc-sq", "echo $'\\\\\\'#'" + DN, true, null),
        ("ansi-then-comment", "echo $'\\'' # x" + DN, false, C),
        ("ansi-arith", "jq $'(( .a ))' f" + DN, true, null),
        ("ansi-case", "echo $'case'" + DN, true, null),
        // single quotes
        ("sq-hash", "grep 'a#b' f" + DN, true, null),
        ("sq-bs-literal", "grep 'a\\' '#' f" + DN, true, null),
        ("sq-hash-midword", "grep 'a'#'b' f" + DN, false, C),
        ("sq-arith", "echo 'x' '(( 1 ))'" + DN, true, null),
        ("sq-case", "echo 'case'" + DN, true, null),
        ("sq-then-bare-case", "echo 'a' case" + DN, false, K),
        ("sq-adjacent-case", "echo 'a'case" + DN, false, K),
        ("sq-dq-inside", "echo '\"' # '\"'" + DN, false, C),
        // unquoted $( ) / <( )
        ("comsub-sq", "x=$(grep '#' f) 2>&1", true, null),
        ("procsub-sq", "diff <(grep '#' a) b" + DN, true, null),
        ("comsub-dq-paren", "x=$(echo \")\" '#') 2>&1", true, null),
        ("comsub-comment", "x=$(echo a # c\n) 2>&1", false, C),
        ("comsub-arith-exp", "x=$((1))" + DN, false, "arithmetic expansion"),
        ("procsub-case", "diff <(case) b" + DN, false, K),
        ("comsub-arith-cmd", "echo $( (( 1 )) )" + DN, false, A),
        ("procsub-dq", "diff <(grep \"(( x\" a) b" + DN, true, null),
        // double quotes
        ("dq-hash", "grep \"a#b\" f" + DN, true, null),
        ("dq-esc-dq", "grep \"a\\\"#\" f" + DN, true, null),
        ("dq-bsbs-close", "grep \"a\\\\\"#\" f" + DN, false, null),
        ("dq-sq-inside", "grep \"it's\" '#' f" + DN, true, null),
        ("dq-dollar-sq-literal", "grep \"$'#\" f" + DN, true, null),
        ("locale-dq", "grep $\"#\" f" + DN, true, null),
        ("dq-comsub-bail", "grep \"a$(echo)#\" f" + DN, false, null),
        ("dq-backtick", "grep \"a`b`#\" f" + DN, false, null),
        ("dq-esc-dollar-paren", "grep \"\\$(#\" f" + DN, true, null),
        ("dq-arith", "echo \"((\"" + DN, true, null),
        ("dq-case", "echo \"case\"" + DN, true, null),
        ("dq-lone-dollar", "echo \"$\" '#'" + DN, true, null),
        ("dq-then-comment", "echo \"a\" #\"" + DN, false, C),
        ("dq-sq-dq-midword", "echo \"a'\"#\"'b\"" + DN, false, C),
        ("dq-sq-dq-concat", "echo \"a\"'#'\"b\"" + DN, true, null),
        ("dq-adjacent-case", "echo \"x\"case" + DN, false, K),
        // = and [[ ]]
        ("assign-sq", "x='#' 2>&1", true, null),
        ("assign-dq-arith", "x=\"(( a\" 2>&1", true, null),
        ("assign-bare-hash", "x=a#b 2>&1", false, C),
        ("dbr-sq", "[[ $x == '#' ]] && echo" + DN, true, null),
        ("dbr-dq-case", "[[ $x =~ \"case\" ]] 2>&1", true, null),
        ("dbr-bare-hash", "[[ $x == a#b ]] 2>&1", false, C),
        ("dbr-glob-sq", "[[ $x == *'#'* ]] 2>&1", true, null),
        // CR / tab / newline
        ("sq-cr", "echo 'a\r#'" + DN, true, null),
        ("sq-tab", "echo 'a\t#'" + DN, true, null),
        ("sq-nl", "echo 'a\n#b'" + DN, true, null),
        ("dq-nl", "echo \"a\n#\"" + DN, true, null),
        ("ansi-nl", "echo $'a\n#'" + DN, true, null),
        ("bare-cr-hash", "echo a\r# x" + DN, false, C),
        ("bare-nl-hash", "echo a\n# x" + DN, false, C),
        ("sq-bs-nl", "echo 'a\\\n#'" + DN, false, "line continuation"),
        ("sq-nl-case", "echo 'a\ncase b'" + DN, true, null),
        ("sq-close-nl-hash", "echo 'a'\n#x" + DN, false, C),
        // extglob
        ("extglob-sq", "ls !(a|'#')" + DN, true, null),
        ("extglob-dq", "ls @(\"#\"|b)" + DN, true, null),
        ("extglob-bare", "ls +(#)" + DN, false, C),
        ("extglob-target", "echo a > .lean-worker/inbox/@('#')/f", false, ".lean-worker/inbox/@"),
        // braces
        ("brace-sq", "echo {a,'#'}" + DN, true, null),
        ("brace-bare", "echo {a,#}" + DN, false, C),
        ("brace-sq-arith", "echo {'((',b}" + DN, true, null),
        // empty quotes
        ("empty-sq", "echo '' '#'" + DN, true, null),
        ("empty-dq", "echo \"\" \"#\"" + DN, true, null),
        ("empty-sq-midword", "echo ''#''" + DN, false, C),
        ("empty-ansi", "echo $'' '#'" + DN, true, null),
        ("empty-locale", "echo $\"\" '#'" + DN, true, null),
        ("empty-then-comment", "echo '' # x" + DN, false, C),
        // bail cases
        ("heredoc-bail", "cat <<'E'" + DN + "\n'#'\nE", false, null),
        ("herestring-bail", "cat <<< '#'" + DN, false, C),
        ("unterminated-sq", "echo '#" + DN, false, null),
        ("unterminated-dq", "echo \"#" + DN, false, null),
        ("unterminated-ansi", "echo $'#" + DN, false, null),
        ("dq-comsub-sq", "echo \"$(echo '#')\"" + DN, false, null),
        ("dq-backtick-bail", "echo \"`echo`\" '#'" + DN, false, null),
        ("trailing-bs-hash", "echo '#'" + DN + " \\", false, null),
        ("trailing-bs", "echo a" + DN + " \\", false, "\\"),
        // .git/config target
        ("git-config-sq-hash", "echo '#x' >> .git/config", false, ".git/config"),
        ("git-config-dq-arith", "echo \"((x\" > .git/config", false, ".git/config"),
        ("git-config-sq-case", "echo 'case' > .git/config", false, ".git/config"),
        ("git-config-quoted-target", "echo \"#\" > \".git/config\"", false, ".git/config"),
        ("git-config-ansi", "echo $'#' > .git/config", false, ".git/config"),
        ("inbox-sq-hash", "echo '#' > .lean-worker/inbox/t/f", true, null),
        // other pre-checks still read the raw command (backtick now reads the skeleton)
        ("raw-arith-exp", "echo '$(('" + DN, false, "arithmetic expansion"),
        ("raw-param", "echo '${'" + DN, false, "parameter expansion"),
        ("raw-old-arith", "echo '$['" + DN, false, "old arithmetic"),
        ("raw-pid", "echo '$$'" + DN, false, P),
        ("sq-backtick", "echo '`'" + DN, true, null),
        ("raw-heredoc-subscript", "cat <<E" + DN + "\n[\nE", false, "heredoc with subscript"),
        // no ">" -> no pre-checks
        ("no-gt-sq", "grep '#' f", true, null),
        ("no-gt-comment", "echo # x", true, null),
        // word-boundary checks of case on the skeleton
        ("underscore-case", "echo esac_case" + DN, true, null),
        ("sq-underscore-case", "echo '_'case" + DN, false, K),
    ];

    [Fact]
    public void Rows_has_108_entries()
    {
        Assert.Equal(108, Rows.Length);
    }

    [Fact]
    public async Task MaskQuoted_conformance_table_matches_expected_outcomeAsync()
    {
        RequireNode();
        Dictionary<string, HookResult> results = await RunHarnessAsync(BuildScript());

        List<string> failures = [];
        foreach ((string label, string command, bool ok, string? reason) in Rows)
        {
            if (!results.TryGetValue(label, out HookResult? r))
            {
                failures.Add($"{label} ({command.ReplaceLineEndings("\\n")}): missing result");
                continue;
            }
            if (r.Ok != ok)
            {
                failures.Add($"{label} ({command.ReplaceLineEndings("\\n")}): expected {(ok ? "allow" : $"deny({reason})")}, got {(r.Ok ? "allow" : r.Message)}");
                continue;
            }
            if (!ok && reason != null && (r.Message == null || !r.Message.Contains(reason, StringComparison.Ordinal)))
            {
                failures.Add($"{label} ({command.ReplaceLineEndings("\\n")}): expected deny({reason}), got deny({r.Message})");
            }
        }
        if (failures.Count > 0) Assert.Fail($"{failures.Count} mismatch(es):\n" + string.Join("\n", failures));
    }

    private string BuildScript()
    {
        StringBuilder sb = new();
        sb.AppendLine($"const {{ LeanWorkerWrapUp }} = await import({JsonSerializer.Serialize(new Uri(PluginPath).AbsoluteUri)});");
        sb.AppendLine("delete process.env.LEAN_WORKER_RUN_DIR;");
        sb.AppendLine("delete process.env.LEAN_WORKER_WRAPUP;");
        sb.AppendLine($"const ctx = await LeanWorkerWrapUp({{ directory: {JsonSerializer.Serialize(_root)}, worktree: {JsonSerializer.Serialize(_root)} }});");
        foreach ((string label, string command, _, _) in Rows)
        {
            sb.AppendLine("try {");
            sb.AppendLine($"  await ctx[\"tool.execute.before\"]({{ tool: \"bash\", sessionID: \"s\", callID: \"c\" }}, {{ args: {{ command: {JsonSerializer.Serialize(command)} }} }});");
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
            throw new InvalidOperationException("node is required on PATH to run MaskQuotedConformanceTests", e);
        }
    }

    private sealed record HookResult(bool Ok, string? Message);
}
