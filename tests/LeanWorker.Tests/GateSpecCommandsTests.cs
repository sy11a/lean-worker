using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The <c>gate.commands</c> grammar of <see cref="GateSpec.FromProfileAll"/>: one spec per entry, the
/// shared chain-level keys copied into each, and a named refusal for every key on the wrong level.
/// </summary>
public class GateSpecCommandsTests
{
    private static JsonObject Profile(string gateJson) => new() { ["gate"] = JsonNode.Parse(gateJson) };

    private static List<GateSpec> Parse(string gateJson) => GateSpec.FromProfileAll(Profile(gateJson), Directory.GetCurrentDirectory())!;

    [Fact]
    public void No_gate_key_gives_no_specs()
    {
        Assert.Null(GateSpec.FromProfileAll([], Directory.GetCurrentDirectory()));
        Assert.Null(GateSpec.FromProfileAll(profile: null, Directory.GetCurrentDirectory()));
    }

    [Fact]
    public void A_single_command_profile_gives_one_unnamed_spec_equal_to_FromProfile()
    {
        const string Gate = /*lang=json,strict*/ """{"command":["sh","gate.sh"],"countPath":"a.b","outputs":["o.sarif"],"maxRounds":3,"env":["X"],"trust":["t.props"]}""";

        GateSpec all = Assert.Single(Parse(Gate));
        GateSpec single = GateSpec.FromProfile(Profile(Gate), Directory.GetCurrentDirectory())!;

        Assert.Null(all.Name);
        Assert.Null(single.Name);
        Assert.Equal(single.Command, all.Command);
        Assert.Equal(single.CountPath, all.CountPath);
        Assert.Equal(single.ReportFromLastLine.ToString(), all.ReportFromLastLine.ToString());
        Assert.Equal(single.Outputs, all.Outputs);
        Assert.Equal(single.Trust, all.Trust);
        Assert.Equal(single.Env, all.Env);
        Assert.Equal((single.MaxRounds, single.MaxTotalUsd, single.TimeoutMinutes, single.FeedbackMaxChars), (all.MaxRounds, all.MaxTotalUsd, all.TimeoutMinutes, all.FeedbackMaxChars));
    }

    [Fact]
    public void Three_entries_give_three_named_specs_with_the_shared_keys_copied_into_each()
    {
        List<GateSpec> specs = Parse(/*lang=json,strict*/ """
            {
              "commands": [
                { "name": "lint", "command": ["sh", "lint.sh"] },
                { "name": "arch", "command": ["sh", "arch.sh", "x"], "countPath": "n.list", "reportFromLastLine": "^r=(.+)$", "outputs": ["out/a.sarif"] },
                { "name": "test-2", "command": ["sh", "test.sh"], "timeoutMinutes": 45 }
              ],
              "maxRounds": 4, "maxTotalUsd": 9, "env": ["GATE_*"], "trust": ["Directory.Build.props"], "feedbackMaxChars": 900, "timeoutMinutes": 20
            }
            """);

        Assert.Equal(["lint", "arch", "test-2"], specs.Select(s => s.Name), StringComparer.Ordinal);
        Assert.Equal(["sh", "arch.sh", "x"], specs[1].Command);
        Assert.All(specs, s =>
        {
            Assert.Equal(4, s.MaxRounds);
            Assert.Equal(9m, s.MaxTotalUsd);
            Assert.Equal(["GATE_*"], s.Env);
            Assert.Equal(["Directory.Build.props"], s.Trust);
            Assert.Equal(900, s.FeedbackMaxChars);
        });
        Assert.Equal([20, 20, 45], specs.Select(s => s.TimeoutMinutes));
        Assert.Equal(["runs[0].results", "n.list", "runs[0].results"], specs.Select(s => s.CountPath), StringComparer.Ordinal);
        Assert.Equal("^r=(.+)$", specs[1].ReportFromLastLine.ToString());
        Assert.Equal("^sarif: (?<report>.+)$", specs[0].ReportFromLastLine.ToString());
        Assert.Equal(["out/a.sarif"], specs[1].Outputs, StringComparer.Ordinal);
        Assert.Empty(specs[0].Outputs!);
    }

    [Fact]
    public void The_gate_level_timeout_defaults_to_30_for_every_command()
    {
        List<GateSpec> specs = Parse(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},{"name":"b","command":["sh"],"timeoutMinutes":5}]}""");

        Assert.Equal([30, 5], specs.Select(s => s.TimeoutMinutes));
    }

    // Every refusal is a launch error with a precise message; per-entry ones name the entry index.
    [Theory]
    [InlineData(/*lang=json,strict*/ """{"command":["sh"],"commands":[{"name":"a","command":["sh"]}]}""", "profile gate cannot have both command and commands")]
    [InlineData(/*lang=json,strict*/ """{"commands":[]}""", "profile gate.commands must be a non-empty array of objects")]
    [InlineData(/*lang=json,strict*/ """{"commands":{"name":"a"}}""", "profile gate.commands must be a non-empty array of objects")]
    [InlineData(/*lang=json,strict*/ """{"commands":"a"}""", "profile gate.commands must be a non-empty array of objects")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},"b"]}""", "profile gate.commands must be a non-empty array of objects")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"command":["sh"]}]}""", "profile gate.commands[0].name is required and must be a non-empty string")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"","command":["sh"]}]}""", "profile gate.commands[0].name is required and must be a non-empty string")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":7,"command":["sh"]}]}""", "profile gate.commands[0].name is required and must be a non-empty string")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"Lint","command":["sh"]}]}""", "profile gate.commands[0].name must match ^[a-z][a-z0-9-]{0,31}$: 'Lint'")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"1a","command":["sh"]}]}""", "profile gate.commands[0].name must match ^[a-z][a-z0-9-]{0,31}$: '1a'")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a/b","command":["sh"]}]}""", "profile gate.commands[0].name must match ^[a-z][a-z0-9-]{0,31}$: 'a/b'")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"abcdefghijklmnopqrstuvwxyz0123456","command":["sh"]}]}""", "profile gate.commands[0].name must match ^[a-z][a-z0-9-]{0,31}$: 'abcdefghijklmnopqrstuvwxyz0123456'")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},{"name":"a","command":["sh"]}]}""", "profile gate.commands[1].name 'a' is already used by an earlier command")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},{"name":"b","command":["sh"],"maxRounds":3}]}""", "profile gate.commands[1].maxRounds is set on gate, not on a command")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},{"name":"b","command":["sh"],"maxTotalUsd":3}]}""", "profile gate.commands[1].maxTotalUsd is set on gate, not on a command")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},{"name":"b","command":["sh"],"env":["X"]}]}""", "profile gate.commands[1].env is set on gate, not on a command")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},{"name":"b","command":["sh"],"trust":["t.props"]}]}""", "profile gate.commands[1].trust is set on gate, not on a command")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},{"name":"b","command":["sh"],"feedbackMaxChars":100}]}""", "profile gate.commands[1].feedbackMaxChars is set on gate, not on a command")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},{"name":"b","command":["sh"],"commands":[]}]}""", "profile gate.commands[1].commands is not allowed; gate.commands does not nest")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]}],"outputs":["o.sarif"]}""", "profile gate.outputs cannot be set when gate.commands is; it belongs on a command")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]}],"reportFromLastLine":"^r=(.+)$"}""", "profile gate.reportFromLastLine cannot be set when gate.commands is; it belongs on a command")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]}],"countPath":"a.b"}""", "profile gate.countPath cannot be set when gate.commands is; it belongs on a command")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},{"name":"b","command":[]}]}""", "profile gate.commands[1].command must be a non-empty array of strings")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},{"name":"b"}]}""", "profile gate.commands[1].command must be a non-empty array of strings")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},{"name":"b","command":["sh",5]}]}""", "profile gate.commands[1].command must be a non-empty array of non-empty strings")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},{"name":"b","command":["sh"],"countPath":"a["}]}""", "profile gate.commands[1].countPath has an invalid segment: 'a['")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},{"name":"b","command":["sh"],"reportFromLastLine":"^x$"}]}""", "profile gate.commands[1].reportFromLastLine must have at least one capture group")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},{"name":"b","command":["sh"],"outputs":["*.sarif"]}]}""", "profile gate.commands[1].outputs[0] must not contain glob characters: *.sarif")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},{"name":"b","command":["sh"],"outputs":"o"}]}""", "profile gate.commands[1].outputs must be an array of paths")]
    [InlineData(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh"]},{"name":"b","command":["sh"],"timeoutMinutes":0}]}""", "profile gate.commands[1].timeoutMinutes must be a positive integer")]
    public void A_misplaced_or_malformed_key_is_refused_with_its_message(string gateJson, string expected) =>
        Assert.Equal(expected, Assert.Throws<LaunchException>(() => Parse(gateJson)).Message);

    [Fact]
    public void A_name_of_32_characters_is_accepted() =>
        Assert.Equal("abcdefghijklmnopqrstuvwxyz012345", Assert.Single(Parse(/*lang=json,strict*/ """{"commands":[{"name":"abcdefghijklmnopqrstuvwxyz012345","command":["sh"]}]}""")).Name);

    [Fact]
    public void The_directory_refusal_applies_to_every_commands_argv()
    {
        string workDir = Directory.CreateTempSubdirectory("lw-gatespec-commands").FullName;
        try
        {
            string subdir = Path.Combine(workDir, "sub");
            _ = Directory.CreateDirectory(subdir);
            JsonObject profile = Profile(/*lang=json,strict*/ """{"commands":[{"name":"a","command":["sh","a.sh"]},{"name":"b","command":["sh","sub"]}]}""");

            LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfileAll(profile, workDir));

            Assert.Equal("profile gate.commands[1].command[1] is a directory: " + subdir, ex.Message);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
