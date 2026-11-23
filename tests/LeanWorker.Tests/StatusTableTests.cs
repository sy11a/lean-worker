using System.Text.Json.Nodes;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The frame of `status --watch` (docs/research/live-status/README.md, sections 4.2 and 4.3), a pure function of its
/// inputs: the columns and their widths, the order in which columns are dropped as the window narrows, the frame
/// below the minimum width, rows cut to the window's height, the colours and the bold of unacknowledged rows.
/// Expected lines are written out from the design's widths (STATE 11, NAME 16 to 28, PROFILE 13, RT 2, MODEL 13,
/// CALLS 5, IN/OUT 11, COST 7, BUDG 4, ELAPSED 7, LAST 12, two spaces between columns).
/// </summary>
public sealed class StatusTableTests
{
    private const string Header = StatusTableRows.Scope + "   live 1  ended 1  attention 0   12:00:00";

    private static string Row(params (string Text, int Width, bool Right)[] cells) =>
        string.Join("  ", cells.Select(c => c.Right ? c.Text.PadLeft(c.Width) : c.Text.PadRight(c.Width))).TrimEnd();

    private static List<string> TwoRows(int width, int height = 40) =>
        StatusTableRows.Frame(StatusTableRows.Data([StatusTableRows.Running(), StatusTableRows.Ended("docs-install-w1")]), width, height);

    [Fact]
    public void At_121_columns_every_column_is_shown_with_the_widths_of_the_design()
    {
        List<string> frame = TwoRows(121);

        Assert.Equal(4, frame.Count);
        Assert.Equal(Header.PadRight(121), frame[0]);
        Assert.Equal(Row(("STATE", 11, false), ("NAME", 16, false), ("PROFILE", 13, false), ("RT", 2, false), ("MODEL", 13, false), ("CALLS", 5, true),
            ("IN/OUT", 11, true), ("COST", 7, true), ("BUDG", 4, true), ("ELAPSED", 7, true), ("LAST", 12, false)), frame[1]);
        Assert.Equal(Row(("running", 11, false), ("fix-save-w1", 16, false), ("code-minimax", 13, false), ("oc", 2, false), ("MiniMax-M3", 13, false),
            ("12", 5, true), ("402k/9.9k", 11, true), ("$0.41", 7, true), ("14%", 4, true), ("3m41s", 7, true), ("bash 4s", 12, false)), frame[2]);
        Assert.Equal(Row(("success", 11, false), ("docs-install-w1", 16, false), ("code-minimax", 13, false), ("oc", 2, false), ("MiniMax-M3", 13, false),
            ("12", 5, true), ("402k/9.9k", 11, true), ("$0.41", 7, true), ("14%", 4, true), ("3m41s", 7, true), ("-", 12, false)), frame[3]);
    }

    [Fact]
    public void At_80_columns_the_profile_in_out_and_model_columns_are_dropped_and_the_name_takes_the_two_spare_columns()
    {
        List<string> frame = TwoRows(80);

        Assert.Equal(Header.PadRight(80), frame[0]);
        Assert.Equal(Row(("STATE", 11, false), ("NAME", 18, false), ("RT", 2, false), ("CALLS", 5, true), ("COST", 7, true), ("BUDG", 4, true),
            ("ELAPSED", 7, true), ("LAST", 12, false)), frame[1]);
        Assert.Equal(Row(("running", 11, false), ("fix-save-w1", 18, false), ("oc", 2, false), ("12", 5, true), ("$0.41", 7, true), ("14%", 4, true),
            ("3m41s", 7, true), ("bash 4s", 12, false)), frame[2]);
        Assert.Equal("running      fix-save-w1         oc     12    $0.41   14%    3m41s  bash 4s", frame[2]);
    }

    [Fact]
    public void At_54_columns_only_the_five_fixed_columns_are_left()
    {
        List<string> frame = TwoRows(54);

        // The header is a cell like any other: cut with a tilde to the width.
        Assert.Equal(Header[..53] + "~", frame[0]);
        Assert.Equal(Row(("STATE", 11, false), ("NAME", 16, false), ("CALLS", 5, true), ("COST", 7, true), ("ELAPSED", 7, true)), frame[1]);
        Assert.Equal(Row(("running", 11, false), ("fix-save-w1", 16, false), ("12", 5, true), ("$0.41", 7, true), ("3m41s", 7, true)), frame[2]);
        Assert.Equal(54, frame[1].Length);
    }

    [Theory]
    [InlineData(135, true, "STATE NAME OWNER PROFILE RT MODEL CALLS IN/OUT COST BUDG ELAPSED LAST")]
    [InlineData(134, true, "STATE NAME PROFILE RT MODEL CALLS IN/OUT COST BUDG ELAPSED LAST")]
    [InlineData(121, true, "STATE NAME PROFILE RT MODEL CALLS IN/OUT COST BUDG ELAPSED LAST")]
    [InlineData(120, true, "STATE NAME RT MODEL CALLS IN/OUT COST BUDG ELAPSED LAST")]
    [InlineData(121, false, "STATE NAME PROFILE RT MODEL CALLS IN/OUT COST BUDG ELAPSED LAST")]
    [InlineData(120, false, "STATE NAME RT MODEL CALLS IN/OUT COST BUDG ELAPSED LAST")]
    [InlineData(106, false, "STATE NAME RT MODEL CALLS IN/OUT COST BUDG ELAPSED LAST")]
    [InlineData(105, false, "STATE NAME RT MODEL CALLS COST BUDG ELAPSED LAST")]
    [InlineData(93, false, "STATE NAME RT MODEL CALLS COST BUDG ELAPSED LAST")]
    [InlineData(92, false, "STATE NAME RT CALLS COST BUDG ELAPSED LAST")]
    [InlineData(78, false, "STATE NAME RT CALLS COST BUDG ELAPSED LAST")]
    [InlineData(77, false, "STATE NAME CALLS COST BUDG ELAPSED LAST")]
    [InlineData(74, false, "STATE NAME CALLS COST BUDG ELAPSED LAST")]
    [InlineData(73, false, "STATE NAME CALLS COST ELAPSED LAST")]
    [InlineData(68, false, "STATE NAME CALLS COST ELAPSED LAST")]
    [InlineData(67, false, "STATE NAME CALLS COST ELAPSED")]
    [InlineData(54, false, "STATE NAME CALLS COST ELAPSED")]
    public void Columns_are_dropped_in_the_order_owner_profile_in_out_model_rt_budget_last_as_the_window_narrows(int width, bool owner, string titles)
    {
        StatusRow row = StatusTableRows.Running() with { OwnerKey = "claude-session:0f3e9a12-0000" };

        List<string> frame = StatusTableRows.Frame(StatusTableRows.Data([row], owner: owner), width);

        Assert.Equal(titles.Split(' '), StatusTableRows.Titles(frame));
    }

    // The name has 16 columns and what is left once the other columns that fit are counted, 28 at most.
    [Theory]
    [InlineData(54, 16)]
    [InlineData(60, 22)]
    [InlineData(67, 28)]
    [InlineData(68, 16)]
    [InlineData(70, 18)]
    [InlineData(80, 18)]
    [InlineData(100, 23)]
    [InlineData(120, 28)]
    [InlineData(160, 28)]
    public void The_name_takes_what_is_left_from_16_to_28_columns_and_a_longer_name_is_cut_with_a_tilde(int width, int nameWidth)
    {
        StatusRow row = StatusTableRows.Running(new string('x', 60));

        List<string> frame = StatusTableRows.Frame(StatusTableRows.Data([row]), width);

        string name = StatusTableRows.Plain(frame[2]).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        Assert.Equal(new string('x', nameWidth - 1) + "~", name);
    }

    [Fact]
    public void A_name_that_fits_is_not_cut_and_a_nested_run_is_indented_two_spaces_per_level()
    {
        StatusRow child = StatusTableRows.Running("child-run") with { Depth = 2 };

        List<string> frame = StatusTableRows.Frame(StatusTableRows.Data([StatusTableRows.Running("parent-run"), child]), 80);

        string state = "running" + new string(' ', 4) + "  ";
        Assert.StartsWith(state + "parent-run ", frame[2], StringComparison.Ordinal);
        Assert.StartsWith(state + new string(' ', 4) + "child-run ", frame[3], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(53)]
    [InlineData(45)]
    [InlineData(39)]
    public void Below_54_columns_the_frame_only_says_the_terminal_is_too_narrow(int width)
    {
        List<string> frame = TwoRows(width);

        string line = Assert.Single(frame);
        Assert.Equal("terminal too narrow (54 columns needed)".PadRight(width), line);
    }

    [Theory]
    [InlineData(38)]
    [InlineData(20)]
    [InlineData(5)]
    public void Where_even_the_too_narrow_text_does_not_fit_it_is_cut_with_a_tilde_to_the_width(int width)
    {
        string line = Assert.Single(TwoRows(width));

        Assert.Equal(width, line.Length);
        Assert.EndsWith("~", line, StringComparison.Ordinal);
        Assert.StartsWith("terminal too narrow (54 columns needed)"[..(width - 1)], line, StringComparison.Ordinal);
    }

    [Fact]
    public void No_line_of_any_frame_is_wider_than_the_window_and_every_row_is_one_line()
    {
        StatusRow cjk = StatusTableRows.Running("日本語のとても長い名前日本語のとても長い名前日本語のとても長い名前");
        StatusRow emoji = StatusTableRows.Running("🚀🚀🚀🚀🚀🚀🚀🚀🚀🚀🚀🚀🚀🚀🚀🚀🚀🚀🚀🚀");
        StatusRow nested = StatusTableRows.Running("nested-with-a-very-long-name-indeed-yes") with { Depth = 12, OwnerKey = "claude-session:abcdefghijkl" };
        StatusRow noStatus = new("/runs/x", "no-status", StatusStates.Unreadable, Liveness.Unknown, Status: null, Detail: new string('d', 400), StartedAt: null, EndedAt: null, Attention: true, Quiet: null);
        StatusRow[] rows = [StatusTableRows.Running(), cjk, emoji, nested, noStatus, StatusTableRows.Ended("failed-one", "error")];

        for (int width = 1; width <= 200; width++)
        {
            foreach (bool owner in new[] { false, true })
            {
                List<string> frame = StatusTableRows.Frame(StatusTableRows.Data(rows, color: true, owner: owner, rows[5]), width, 40);

                Assert.All(frame, line => Assert.True(StatusTableRows.Columns(line) <= width, "width " + width.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": '" + line + "'"));
                if (width >= StatusTable.MinWidth)
                {
                    Assert.Equal(2 + rows.Length, frame.Count);
                }
            }
        }
    }

    // Six rows under a header and a title line: all of them need 8 lines; when there are fewer, the last line is the "more" line.
    [Theory]
    [InlineData(40, 6, false)]
    [InlineData(8, 6, false)]
    [InlineData(7, 4, true)]
    [InlineData(6, 3, true)]
    [InlineData(5, 2, true)]
    [InlineData(4, 1, true)]
    [InlineData(3, 0, true)]
    public void Rows_beyond_the_height_are_cut_and_the_last_line_says_how_many_more_there_are(int height, int shown, bool more)
    {
        StatusRow[] rows = [.. Enumerable.Range(1, 6).Select(i => StatusTableRows.Running("run-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)))];

        List<string> frame = StatusTableRows.Frame(StatusTableRows.Data(rows), 100, height);

        Assert.Equal(2 + shown + (more ? 1 : 0), frame.Count);
        Assert.True(frame.Count <= height);
        Assert.Equal(shown, frame.Count(l => l.Contains("run-", StringComparison.Ordinal) && !l.StartsWith("...", StringComparison.Ordinal)));
        if (more)
        {
            Assert.Equal("... " + (rows.Length - shown).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more (status --all for the full list)", frame[^1].TrimEnd());
        }
        else
        {
            Assert.DoesNotContain(frame, l => l.StartsWith("...", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public void A_window_of_one_or_two_lines_gets_no_more_than_its_height_and_never_a_row(int height)
    {
        StatusRow[] rows = [StatusTableRows.Running("run-1"), StatusTableRows.Running("run-2")];

        List<string> frame = StatusTableRows.Frame(StatusTableRows.Data(rows), 100, height);

        Assert.Equal(height, frame.Count);
        Assert.DoesNotContain(frame, l => l.Contains("run-", StringComparison.Ordinal));
    }

    [Fact]
    public void The_frame_is_a_pure_function_the_same_inputs_give_the_same_lines_and_the_rows_are_left_as_they_are()
    {
        StatusRow[] rows = [StatusTableRows.Ended("b-run"), StatusTableRows.Running("a-run")];
        StatusTable.FrameData data = StatusTableRows.Data(rows, color: true);

        List<string> first = StatusTableRows.Frame(data, 100);
        List<string> second = StatusTableRows.Frame(data, 100);

        Assert.Equal(first, second);
        Assert.Equal(["b-run", "a-run"], [.. data.Rows.Select(r => r.Name)]);
        Assert.Contains("b-run", first[2], StringComparison.Ordinal);
        Assert.Contains("a-run", first[3], StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_counts_live_ended_and_attention_rows_and_ends_with_the_time()
    {
        StatusRow[] rows = [StatusTableRows.Running("a"), StatusTableRows.Running("b", quiet: TimeSpan.FromMinutes(41)), StatusTableRows.Ended("c", "error"), StatusTableRows.Ended("d")];

        List<string> frame = StatusTableRows.Frame(StatusTableRows.Data(rows), 100);

        Assert.Equal((StatusTableRows.Scope + "   live 2  ended 2  attention 2   12:00:00").PadRight(100), frame[0]);
    }

    [Fact]
    public void A_gate_run_shows_its_round_and_how_long_the_gate_has_run_and_a_quiet_run_its_quiet_age()
    {
        StatusRow gate = StatusTableRows.Running("gate-run", d =>
        {
            d["phase"] = "gate";
            d["updated_at"] = StatusDocs.Stamp(StatusDocs.Now - TimeSpan.FromSeconds(40));
            d["live"]!["gate_round"] = 2;
            d["live"]!["gate_max_rounds"] = 3;
        }) with
        {
            State = StatusStates.Gate,
        };
        StatusRow quiet = StatusTableRows.Running("quiet-run", quiet: TimeSpan.FromMinutes(41));

        List<string> frame = StatusTableRows.Frame(StatusTableRows.Data([gate, quiet]), 121);

        Assert.StartsWith("gate r2/3", frame[2], StringComparison.Ordinal);
        Assert.EndsWith("gate 40s", frame[2], StringComparison.Ordinal);
        Assert.EndsWith("quiet 41m !", frame[3], StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_without_a_status_file_shows_dashes_for_its_counters_and_its_detail_as_last()
    {
        StatusRow row = new("/runs/x", "no-status", StatusStates.Unreadable, Liveness.Unknown, Status: null, Detail: "status.json: torn", StartedAt: null, EndedAt: null, Attention: true, Quiet: null);

        List<string> frame = StatusTableRows.Frame(StatusTableRows.Data([row]), 121);

        Assert.Equal(Row(("unreadable", 11, false), ("no-status", 16, false), ("-", 13, false), ("-", 2, false), ("-", 13, false), ("-", 5, true), ("-", 11, true),
            ("-", 7, true), ("-", 4, true), ("-", 7, true), ("status.json~", 12, false)), frame[2]);
    }

    [Fact]
    public void Missing_tokens_or_a_zero_budget_show_a_dash()
    {
        StatusRow row = StatusTableRows.Running("no-tokens", d =>
        {
            d["live"]!["tokens"] = null;
            d["spec"]!["budget_usd"] = 0m;
        });

        List<string> frame = StatusTableRows.Frame(StatusTableRows.Data([row]), 121);

        Assert.Contains("-" + new string(' ', 4) + "$0.41" + new string(' ', 5) + "-", frame[2], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("success", "\u001b[32m")]
    [InlineData("error", "\u001b[31m")]
    [InlineData("timeout", "\u001b[31m")]
    [InlineData("failed", "\u001b[31m")]
    [InlineData("orphaned", "\u001b[31m")]
    [InlineData("lost", "\u001b[31m")]
    [InlineData("gone", "\u001b[31m")]
    [InlineData("wrapped-up", "\u001b[33m")]
    public void An_ended_rows_state_is_coloured_green_red_or_yellow_and_reset_after_it(string state, string colour)
    {
        StatusRow row = StatusTableRows.Ended("ended-run", state);

        List<string> frame = StatusTableRows.Frame(StatusTableRows.Data([row], color: true), 100);

        Assert.StartsWith(colour + state.PadRight(11) + "\u001b[0m  ended-run", frame[2], StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", frame[2][(colour.Length + 11 + 4)..], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("wrap-up", false)]
    [InlineData("ending", false)]
    [InlineData("running", true)]
    public void A_live_rows_state_is_yellow_when_it_is_wrapping_up_ending_or_quiet_and_plain_otherwise(string state, bool quietRun)
    {
        StatusRow row = StatusTableRows.Running("live-run", quiet: quietRun ? TimeSpan.FromMinutes(41) : null) with { State = state };

        List<string> frame = StatusTableRows.Frame(StatusTableRows.Data([row], color: true), 100);

        Assert.StartsWith("\u001b[33m" + state.PadRight(11) + "\u001b[0m", frame[2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_live_run_that_is_not_quiet_has_no_colour()
    {
        List<string> frame = StatusTableRows.Frame(StatusTableRows.Data([StatusTableRows.Running()], color: true), 100);

        Assert.DoesNotContain("\u001b", frame[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Without_colour_a_frame_has_no_colour_escape_at_all()
    {
        StatusRow[] rows = [StatusTableRows.Ended("a", "success"), StatusTableRows.Ended("b", "error"), StatusTableRows.Ended("c", "wrapped-up"), StatusTableRows.Running("d", quiet: TimeSpan.FromHours(1))];

        List<string> frame = StatusTableRows.Frame(StatusTableRows.Data(rows, color: false), 100);

        Assert.DoesNotContain("\u001b", string.Concat(frame), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unacknowledged_row_is_bold_from_its_start_to_its_end_and_the_colour_of_its_state_keeps_the_bold()
    {
        StatusRow ended = StatusTableRows.Ended("just-ended");
        StatusRow acknowledged = StatusTableRows.Ended("seen-before");

        List<string> frame = StatusTableRows.Frame(StatusTableRows.Data([ended, acknowledged], color: true, owner: false, ended), 100);

        Assert.StartsWith("\u001b[1m\u001b[32msuccess    \u001b[0m\u001b[1m  just-ended", frame[2], StringComparison.Ordinal);
        Assert.EndsWith("\u001b[0m", frame[2], StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[1m", frame[3], StringComparison.Ordinal);
    }

    [Fact]
    public void An_unacknowledged_row_without_colour_is_bold_and_nothing_else()
    {
        StatusRow ended = StatusTableRows.Ended("just-ended");

        List<string> frame = StatusTableRows.Frame(StatusTableRows.Data([ended], color: false, owner: false, ended), 100);

        Assert.StartsWith("\u001b[1msuccess" + new string(' ', 6) + "just-ended", frame[2], StringComparison.Ordinal);
        Assert.EndsWith("\u001b[0m", frame[2], StringComparison.Ordinal);
        Assert.Equal(2, frame[2].Count(c => c is '\u001b'));
    }

    [Fact]
    public void A_wide_row_with_budget_far_over_the_cost_and_a_huge_count_still_fits_its_cells()
    {
        StatusRow row = StatusTableRows.Running("big", d =>
        {
            d["live"]!["api_calls"] = 123456789;
            d["live"]!["cost_usd"] = 12345.678m;
            d["live"]!["elapsed_ms"] = long.MaxValue;
            d["live"]!["tokens"] = new JsonObject { ["input"] = long.MaxValue, ["cache_read"] = long.MaxValue, ["cache_write"] = 0, ["output"] = 5 };
        });

        for (int width = 54; width <= 160; width += 7)
        {
            Assert.All(StatusTableRows.Frame(StatusTableRows.Data([row]), width), line => Assert.True(StatusTableRows.Columns(line) <= width));
        }
    }
}
