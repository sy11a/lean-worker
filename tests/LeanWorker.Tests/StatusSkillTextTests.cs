using System.Text.RegularExpressions;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The skill text about `status` (SKILL.md and the templates next to it): a recipe in it must be one the launcher takes
/// (every `status` command line in a code span is fed to the option parser, with its placeholders filled in), every
/// flag it mentions must be one `status` knows, the exit codes it names for --wait are the ones the waiter returns, and
/// the launcher's profiles subcommand is written `profiles`, never as the option `--profiles` (issue #138). No test in
/// this project read the skill text before; this one finds it the way TemplateTests finds the templates.
/// </summary>
public sealed partial class StatusSkillTextTests
{
    private const string Section = "### Runs in flight";

    // What StatusCommand returns for an option it refuses.
    private const int UsageError = 2;

    // What the text writes between angle brackets or in a shell variable, and a value the parser takes for it.
    private static readonly Dictionary<string, string> _placeholders = new(StringComparer.Ordinal)
    {
        ["<key>"] = "k:1",
        ["<n>"] = "3",
        ["<dur>"] = "25m",
        ["<t0>"] = "2026-11-22T13:02:31Z",
        ["$t0"] = "2026-11-22T13:02:31Z",
    };

    private static string SkillDir()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "skills", "lean-worker", "SKILL.md")))
        {
            d = d.Parent;
        }

        return Path.Combine(d!.FullName, "skills", "lean-worker");
    }

    // SKILL.md and the markdown templates: the text a delegating session reads.
    private static List<(string File, string Text)> Texts()
    {
        string dir = SkillDir();
        string[] files = [Path.Combine(dir, "SKILL.md"), .. Directory.GetFiles(Path.Combine(dir, "templates"), "*.md")];
        return [.. files.Select(f => (Path.GetRelativePath(dir, f), File.ReadAllText(f)))];
    }

    // Every text file under the skill directory, build output left out.
    private static IEnumerable<(string File, string Text)> EveryTextFile()
    {
        string dir = SkillDir();
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(dir, file);
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("bin", StringComparer.Ordinal) || parts.Contains("obj", StringComparer.Ordinal))
            {
                continue;
            }

            byte[] bytes = File.ReadAllBytes(file);
            if (!bytes.Contains((byte)0))
            {
                yield return (relative, System.Text.Encoding.UTF8.GetString(bytes));
            }
        }
    }

    private static IEnumerable<string> Spans(string text) => SpanPattern().Matches(text).Select(m => m.Groups["span"].Value);

    private static string RunsInFlight(string skill)
    {
        int start = skill.IndexOf(Section, StringComparison.Ordinal);
        Assert.True(start >= 0, "SKILL.md has no '" + Section + "' section");
        Match next = HeadingPattern().Match(skill, start + Section.Length);
        return next.Success ? skill[start..next.Index] : skill[start..];
    }

    // The status command lines in code spans: what follows the word `status` (after `lw` or at the start of the span),
    // up to a closing parenthesis, as arguments with their placeholders filled in.
    private static List<(string File, string Span, string[] Args)> Recipes()
    {
        List<(string, string, string[])> recipes = [];
        foreach ((string file, string text) in Texts())
        {
            foreach (string span in Spans(text))
            {
                Match m = RecipePattern().Match(span);
                if (m.Success)
                {
                    recipes.Add((file, span, Arguments(m.Groups["tail"].Value)));
                }
            }
        }

        return recipes;
    }

    private static string[] Arguments(string tail)
    {
        string cut = tail.Split(')')[0].Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal);
        List<string> args = [];
        foreach (Match m in TokenPattern().Matches(cut))
        {
            string token = m.Value.Trim('"');
            if (token.StartsWith('<') || token.StartsWith('$'))
            {
                Assert.True(_placeholders.TryGetValue(token, out string? value), $"the test does not know the placeholder {token}");
                token = value;
            }

            args.Add(token);
        }

        return [.. args];
    }

    private static bool Has(string[] args, string flag) => args.Contains(flag, StringComparer.Ordinal);

    [GeneratedRegex("`(?<span>[^`\\n]+)`", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex SpanPattern();

    [GeneratedRegex(@"^#{2,3} ", RegexOptions.Multiline, matchTimeoutMilliseconds: 5000)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"(?:^|\blw\s+)status(?=\s|$)(?<tail>.*)$", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex RecipePattern();

    [GeneratedRegex("\"[^\"]*\"|\\S+", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"date\s+(?:-\w+\s+)*\+(?<format>[^\s)]+)", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex DateFormat();

    [GeneratedRegex(@"--[a-z][a-z-]*", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex FlagPattern();

    [GeneratedRegex(@"--\s+--profiles", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ProfilesAsOption();

    [GeneratedRegex(@"\*\*(?<n>\d)\*\*", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex BoldNumber();

    [Fact]
    public void No_file_under_the_skill_directory_tells_the_reader_to_pass_profiles_as_an_option()
    {
        List<(string File, string Text)> files = [.. EveryTextFile()];

        Assert.Contains(files, f => f.File is "SKILL.md");
        Assert.Empty(files.Where(f => ProfilesAsOption().IsMatch(f.Text)).Select(f => f.File));
        Assert.Empty(files.Where(f => f.Text.Contains("-- --profiles", StringComparison.Ordinal)).Select(f => f.File));
    }

    [Fact]
    public void The_skill_text_names_the_profiles_subcommand_without_dashes()
    {
        string skill = File.ReadAllText(Path.Combine(SkillDir(), "SKILL.md"));

        Assert.Contains("-- profiles", skill, StringComparison.Ordinal);
        LaunchException ex = Assert.Throws<LaunchException>(() => Options.Parse(["--profiles"]));
        Assert.Contains("unknown option", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_skill_text_has_the_recipes_the_design_asks_for()
    {
        List<(string File, string Span, string[] Args)> recipes = Recipes();

        Assert.Contains(recipes, r => r.Args.SequenceEqual(["--here"], StringComparer.Ordinal) && r.File is "SKILL.md");
        Assert.Contains(recipes, r => r.Args.SequenceEqual(["--here"], StringComparer.Ordinal) && r.File.EndsWith("orchestrator-rule.md", StringComparison.Ordinal));
        Assert.Contains(recipes, r => r.Args.Length is 0);
        Assert.Contains(recipes, r => r.Args.SequenceEqual(["--whoami", "--key"], StringComparer.Ordinal));
        Assert.Contains(recipes, r => Has(r.Args, "--wait") && Has(r.Args, "--started-after") && Has(r.Args, "--expect") && Has(r.Args, "--timeout"));
        Assert.Contains(recipes, r => Has(r.Args, "--watch"));
    }

    [Fact]
    public void Every_status_command_line_in_the_skill_text_is_one_the_option_parser_takes()
    {
        List<(string File, string Span, string[] Args)> recipes = Recipes();

        Assert.NotEmpty(recipes);
        foreach ((string file, string span, string[] args) in recipes)
        {
            Exception? refused = Record.Exception(() => StatusOptions.Parse(args));
            Assert.True(refused is null, $"{file}: `{span}` is a usage error: {refused?.Message}");
        }
    }

    [Fact]
    public void Every_status_flag_the_skill_text_mentions_is_one_status_knows()
    {
        string skill = File.ReadAllText(Path.Combine(SkillDir(), "SKILL.md"));
        string[] flags =
        [
            .. Spans(RunsInFlight(skill)).Where(s => s.StartsWith("--", StringComparison.Ordinal)).SelectMany(s => FlagPattern().Matches(s).Select(m => m.Value)),
            .. Recipes().SelectMany(r => r.Args.Where(a => a.StartsWith("--", StringComparison.Ordinal))),
        ];

        Assert.NotEmpty(flags);
        foreach (string flag in flags.Distinct(StringComparer.Ordinal))
        {
            LaunchException? refused = null;
            try
            {
                _ = StatusOptions.Parse([flag]);
            }
            catch (LaunchException ex)
            {
                refused = ex;
            }

            Assert.False(refused?.Message.StartsWith("unknown option", StringComparison.Ordinal) is true, $"status does not know {flag}");
        }
    }

    [Fact]
    public void A_code_span_that_is_a_status_flag_with_a_value_is_a_value_the_parser_takes()
    {
        string skill = File.ReadAllText(Path.Combine(SkillDir(), "SKILL.md"));
        string[][] contexts = [[], ["--here", "--wait"], ["--here", "--watch"], ["--here"], ["--whoami"]];
        int checkedSpans = 0;

        // A span of a flag alone names the flag (the flag test covers it); one with a value must have a value the parser takes.
        foreach (string span in Spans(RunsInFlight(skill)).Where(s => s.StartsWith("--", StringComparison.Ordinal) && !s.Contains('$', StringComparison.Ordinal)))
        {
            string[] args = Arguments(span);
            if (args.Length < 2)
            {
                continue;
            }

            bool taken = contexts.Any(c => Record.Exception(() => StatusOptions.Parse([.. c, .. args])) is null);
            Assert.True(taken, $"`{span}` is not taken by the option parser in any mode");
            checkedSpans++;
        }

        Assert.True(checkedSpans >= 3, "the section's flag spans were not found");
    }

    [Fact]
    public void The_exit_codes_the_skill_text_names_for_the_waiter_are_the_ones_it_returns()
    {
        string skill = File.ReadAllText(Path.Combine(SkillDir(), "SKILL.md"));
        string section = RunsInFlight(skill);
        string listing = section[section.IndexOf("**0**", StringComparison.Ordinal)..];

        string[] named = [.. BoldNumber().Matches(listing).Select(m => m.Groups["n"].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // 2 is the command's usage error (StatusCommand returns it for a refused option), which the text names too.
        int[] returned = [StatusWait.AllSuccess, StatusWait.SomeFailed, UsageError, StatusWait.SomeWrappedUp, StatusWait.NothingMatched, StatusWait.TimedOut, StatusWait.Unresolved];
        Assert.Equal([.. returned.Select(c => c.ToString(System.Globalization.CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal)], named);
    }

    [Fact]
    public async Task The_exit_code_the_skill_text_names_for_a_usage_error_is_the_one_the_command_returnsAsync()
    {
        using StatusIndexRig rig = new();

        (int code, string output) = await rig.CallAsync("--here", "--started-after", "yesterday");

        Assert.Equal(UsageError, code);
        Assert.Contains("usage:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_any_bullet_tells_how_to_re_arm_and_the_command_it_means_is_one_the_parser_takes()
    {
        string skill = File.ReadAllText(Path.Combine(SkillDir(), "SKILL.md"));
        string section = RunsInFlight(skill);
        string bullet = section[section.IndexOf("- **`--any`**", StringComparison.Ordinal)..];

        Assert.Contains("re-arm", bullet, StringComparison.Ordinal);
        Assert.Contains("without `--started-after` and `--expect`", bullet, StringComparison.Ordinal);

        // The batch recipe, then the same scope as the bullet says: with --any, without --started-after and --expect.
        (string File, string Span, string[] Args) batch = Recipes().First(r => Has(r.Args, "--wait") && Has(r.Args, "--started-after") && Has(r.Args, "--expect"));
        string[] rearm = [.. WithoutOption(WithoutOption(batch.Args, "--started-after"), "--expect"), "--any"];

        Assert.DoesNotContain("--started-after", rearm, StringComparer.Ordinal);
        Assert.DoesNotContain("--expect", rearm, StringComparer.Ordinal);
        Assert.Contains("--owner", rearm, StringComparer.Ordinal);
        StatusCommand.StatusArgs parsed = StatusOptions.Parse(rearm);
        Assert.True(parsed.Wait && parsed.Any);
        Assert.Null(parsed.StartedAfter);
        Assert.Null(parsed.Expect);
        Assert.Equal("k:1", parsed.Owner);
    }

    [Fact]
    public void The_t0_recipe_prints_a_time_with_Z_and_the_parser_takes_what_it_prints()
    {
        string skill = File.ReadAllText(Path.Combine(SkillDir(), "SKILL.md"));
        string[] spans = [.. Spans(RunsInFlight(skill)).Where(s => s.Contains("date ", StringComparison.Ordinal))];

        string recipe = Assert.Single(spans);
        Match m = DateFormat().Match(recipe);
        Assert.True(m.Success, $"`{recipe}` is not a `date` call with a +format");
        Assert.Contains("-u", recipe, StringComparison.Ordinal);

        // What `date -u +<format>` prints for one instant: %F is the date, %T the time, the rest is copied.
        DateTimeOffset instant = new(2026, 11, 23, 15, 35, 41, TimeSpan.Zero);
        string printed = m.Groups["format"].Value
            .Replace("%F", instant.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%T", instant.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        Assert.Equal("2026-11-23T15:35:41Z", printed);
        Assert.Equal(instant, StatusOptions.Parse(["--here", "--wait", "--started-after", printed]).StartedAfter);
    }

    [Fact]
    public void Every_wait_recipe_with_started_after_in_the_skill_text_gets_its_time_from_the_t0_variable()
    {
        List<(string File, string Span, string[] Args)> recipes = [.. Recipes().Where(r => Has(r.Args, "--started-after"))];

        Assert.NotEmpty(recipes);
        foreach ((string file, string span, string[] args) in recipes)
        {
            Assert.True(span.Contains("\"$t0\"", StringComparison.Ordinal) || span.Contains("<t0>", StringComparison.Ordinal), $"{file}: `{span}` does not take the time from t0");
            _ = Assert.NotNull(StatusOptions.Parse(args).StartedAfter);
        }
    }

    private static string[] WithoutOption(string[] args, string option)
    {
        int at = Array.IndexOf(args, option);
        return at < 0 ? args : [.. args[..at], .. args[(at + 2)..]];
    }

    [Fact]
    public void The_skill_text_gives_both_runtimes_a_timeout_the_waiter_takes_and_a_tool_timeout_above_it()
    {
        string skill = File.ReadAllText(Path.Combine(SkillDir(), "SKILL.md"));
        string section = RunsInFlight(skill);

        Assert.Contains("`--timeout 25m`", section, StringComparison.Ordinal);
        Assert.Contains("`--timeout 120m`", section, StringComparison.Ordinal);
        Assert.Contains("run_in_background", section, StringComparison.Ordinal);
        Assert.Contains("7500000", section, StringComparison.Ordinal);
        Assert.True(StatusWait.DefaultTimeout < TimeSpan.FromMilliseconds(7_500_000));
        Assert.Equal(TimeSpan.FromMinutes(25), StatusOptions.ParseDuration("25m", "--timeout"));
    }
}
