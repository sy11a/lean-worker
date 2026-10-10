using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

public class GateSpecTests
{
    private static JsonObject Profile(JsonObject gate) => new() { ["gate"] = gate };

    private static JsonObject MinimalGate() => new() { ["command"] = new JsonArray(["sh", "gate.sh"]) };

    [Fact]
    public void Missing_gate_key_returns_null() => Assert.Null(GateSpec.FromProfile([]));

    [Fact]
    public void Null_profile_returns_null() => Assert.Null(GateSpec.FromProfile(profile: null));

    [Fact]
    public void Gate_json_null_returns_null() => Assert.Null(GateSpec.FromProfile(new JsonObject { ["gate"] = null }));

    [Fact]
    public void Non_object_gate_throws()
    {
        LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(new JsonObject { ["gate"] = "x" }));
        Assert.Equal("profile gate must be an object", ex.Message);

        _ = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(new JsonObject { ["gate"] = new JsonArray() }));
    }

    [Fact]
    public void Defaults_are_applied()
    {
        GateSpec spec = GateSpec.FromProfile(Profile(MinimalGate()))!;
        Assert.Equal(["sh", "gate.sh"], spec.Command);
        Assert.Equal("^sarif: (?<report>.+)$", spec.ReportFromLastLine.ToString());
        Assert.Equal("runs[0].results", spec.CountPath);
        Assert.Equal(8000, spec.FeedbackMaxChars);
        Assert.Equal(30, spec.TimeoutMinutes);
        Assert.Equal(5, spec.MaxRounds);
        Assert.Null(spec.MaxTotalUsd);
        Assert.Empty(spec.Env);
    }

    [Fact]
    public void Empty_command_throws()
    {
        JsonObject gate = MinimalGate();
        gate["command"] = new JsonArray();
        LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(Profile(gate)));
        Assert.Contains("gate.command", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_string_command_element_throws()
    {
        JsonObject gate = MinimalGate();
        gate["command"] = new JsonArray(["sh", 5]);
        LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(Profile(gate)));
        Assert.Contains("gate.command", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Regex_without_a_group_throws()
    {
        JsonObject gate = MinimalGate();
        gate["reportFromLastLine"] = "^sarif: .+$";
        LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(Profile(gate)));
        Assert.Contains("gate.reportFromLastLine", ex.Message, StringComparison.Ordinal);
        Assert.Contains("capture group", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_regex_throws()
    {
        JsonObject gate = MinimalGate();
        gate["reportFromLastLine"] = "(";
        LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(Profile(gate)));
        Assert.Contains("gate.reportFromLastLine", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a valid regex", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a[")]
    [InlineData("a[-1]")]
    [InlineData("[0]")]
    [InlineData("a..b")]
    public void Bad_count_path_segment_throws(string path)
    {
        JsonObject gate = MinimalGate();
        gate["countPath"] = path;
        LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(Profile(gate)));
        Assert.Contains("gate.countPath", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("feedbackMaxChars")]
    [InlineData("timeoutMinutes")]
    [InlineData("maxRounds")]
    public void Non_positive_int_throws(string key)
    {
        JsonObject gate = MinimalGate();
        gate[key] = 0;
        LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(Profile(gate)));
        Assert.Contains($"gate.{key}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("positive integer", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_max_total_usd_throws(decimal value)
    {
        JsonObject gate = MinimalGate();
        gate["maxTotalUsd"] = value;
        LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(Profile(gate)));
        Assert.Contains("gate.maxTotalUsd", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_numeric_max_total_usd_throws()
    {
        JsonObject gate = MinimalGate();
        gate["maxTotalUsd"] = "5";
        LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(Profile(gate)));
        Assert.Contains("gate.maxTotalUsd", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Positive_max_total_usd_is_kept()
    {
        JsonObject gate = MinimalGate();
        gate["maxTotalUsd"] = 1.5m;
        GateSpec spec = GateSpec.FromProfile(Profile(gate))!;
        Assert.Equal(1.5m, spec.MaxTotalUsd);
    }

    [Fact]
    public void Invalid_env_entry_throws()
    {
        JsonObject gate = MinimalGate();
        gate["env"] = new JsonArray(["LW_TEST_*", "bad name"]);
        LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(Profile(gate)));
        Assert.Contains("gate.env", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Env_entries_are_kept_in_order()
    {
        JsonObject gate = MinimalGate();
        gate["env"] = new JsonArray(["LW_TEST_*", "FOO"]);
        GateSpec spec = GateSpec.FromProfile(Profile(gate))!;
        Assert.Equal(["LW_TEST_*", "FOO"], spec.Env);
    }

    [Fact]
    public void A_directory_in_the_command_throws_naming_the_index()
    {
        string workDir = Directory.CreateTempSubdirectory("lw-gatespec").FullName;
        string subdir = Path.Combine(workDir, "sub");
        Directory.CreateDirectory(subdir);
        JsonObject gate = new() { ["command"] = new JsonArray(["sh", "sub"]) };
        LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(Profile(gate), workDir));
        Assert.Contains("gate.command[1]", ex.Message, StringComparison.Ordinal);
        Assert.Contains(subdir, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bare_argv0_named_like_a_directory_in_the_working_directory_is_accepted()
    {
        string workDir = Directory.CreateTempSubdirectory("lw-gatespec").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, "dotnet"));
            JsonObject gate = new() { ["command"] = new JsonArray(["dotnet", "build"]) };
            GateSpec spec = GateSpec.FromProfile(Profile(gate), workDir)!;
            Assert.Equal(["dotnet", "build"], spec.Command);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void An_argv0_with_a_directory_part_that_is_a_directory_is_still_refused()
    {
        string workDir = Directory.CreateTempSubdirectory("lw-gatespec").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(workDir, "tools"));
            JsonObject gate = new() { ["command"] = new JsonArray(["./tools"]) };
            LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(Profile(gate), workDir));
            Assert.Contains("gate.command[0]", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void Gate_trust_defaults_to_empty_and_keeps_valid_entries_with_forward_slashes()
    {
        Assert.Empty(GateSpec.FromProfile(Profile(MinimalGate()))!.Trust!);

        JsonObject gate = MinimalGate();
        gate["trust"] = new JsonArray(["Directory.Build.props", "src\\**\\*.props", ".editorconfig"]);
        GateSpec spec = GateSpec.FromProfile(Profile(gate))!;
        Assert.Equal(["Directory.Build.props", "src/**/*.props", ".editorconfig"], spec.Trust);
    }

    [Fact]
    public void Gate_trust_must_be_an_array()
    {
        JsonObject gate = MinimalGate();
        gate["trust"] = "Directory.Build.props";
        LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(Profile(gate)));
        Assert.Contains("gate.trust", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("..")]
    [InlineData("../outside.props")]
    [InlineData("src/../../outside.props")]
    [InlineData("src\\..\\..\\outside.props")]
    public void A_bad_gate_trust_entry_throws_naming_its_index(string entry)
    {
        JsonObject gate = MinimalGate();
        gate["trust"] = new JsonArray(["fine.props", entry]);
        LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(Profile(gate)));
        Assert.Contains("gate.trust[1]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_string_gate_trust_entry_throws_naming_its_index()
    {
        JsonObject gate = MinimalGate();
        gate["trust"] = new JsonArray(["fine.props", 7]);
        LaunchException ex = Assert.Throws<LaunchException>(() => GateSpec.FromProfile(Profile(gate)));
        Assert.Contains("gate.trust[1]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_gate_flag_parses()
    {
        Options o = Options.Parse(["--no-gate"]);
        Assert.True(o.NoGate);
    }

    [Fact]
    public void Gate_max_rounds_flag_parses()
    {
        Options o = Options.Parse(["--gate-max-rounds", "3"]);
        Assert.Equal(3, o.GateMaxRounds);
    }
}
