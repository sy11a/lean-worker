using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The launch option --owner (docs/research/live-status/README.md, section 4.4, rule 1): checked when the
/// options are read, so a key the index could not hold is a usage error before anything starts, and the message
/// never echoes the value.
/// </summary>
public sealed class OwnerOptionTests
{
    private static readonly string[] _task = ["--task", "t.md"];

    [Fact]
    public void A_valid_owner_key_is_kept_as_given_next_to_other_options()
    {
        Options o = Options.Parse([.. _task, "--owner", "claude-session:0f3e-9a12", "--name", "x"]);

        Assert.Equal("claude-session:0f3e-9a12", o.Owner);
        Assert.Equal("x", o.Name);
    }

    [Fact]
    public void Without_the_option_there_is_no_owner() => Assert.Null(Options.Parse(_task).Owner);

    [Fact]
    public void The_longest_key_is_accepted()
    {
        string key = new('k', OwnerKeys.MaxLength);

        Assert.Equal(key, Options.Parse([.. _task, "--owner", key]).Owner);
    }

    [Fact]
    public void A_key_over_200_characters_an_empty_one_and_a_disallowed_character_are_usage_errors()
    {
        string[] bad =
        [
            new string('k', OwnerKeys.MaxLength + 1), string.Empty, "has space", "semi;colon", "quo\"te", "café", "back\\slash", "star*",
            "tab" + (char)9 + "x", "new" + (char)10 + "x", "nul" + (char)0 + "x", (char)27 + "[31mred" + (char)27 + "[0m", "del" + (char)127, "bidi" + (char)0x202E,
        ];

        foreach (string key in bad)
        {
            LaunchException ex = Assert.Throws<LaunchException>(() => Options.Parse([.. _task, "--owner", key]));

            Assert.Contains("--owner", ex.Message, StringComparison.Ordinal);
            Assert.Empty(TerminalAssert.PlainProblems(ex.Message));
            if (key.Length >= 5)
            {
                Assert.DoesNotContain(key, ex.Message, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void An_owner_option_without_a_value_is_a_usage_error()
    {
        LaunchException ex = Assert.Throws<LaunchException>(() => Options.Parse([.. _task, "--owner"]));

        Assert.Contains("--owner needs a value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_usage_text_names_the_option_and_its_default()
    {
        Assert.Contains("--owner <key>", Options.Usage, StringComparison.Ordinal);
        Assert.Contains("LEAN_WORKER_OWNER", Options.Usage, StringComparison.Ordinal);
    }
}
