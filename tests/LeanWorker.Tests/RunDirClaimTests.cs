using System.Globalization;
using Xunit;

namespace LeanWorker.Tests;

/// <summary>
/// The unique run dir (docs/research/live-status/README.md, section 5, "Same name, same second, one run dir"; slice
/// 6): a free name keeps today's name, a taken one gets the launcher's pid, a name taken twice is a launch error, and
/// launchers that claim one name at the same moment never share a directory.
/// </summary>
public sealed class RunDirClaimTests : IDisposable
{
    private const string Base = "20261122-115500-job";

    private readonly TempDirs _dirs = new();

    public void Dispose() => _dirs.Dispose();

    private string Runs() => Path.Combine(_dirs.Create("lw-claim"), "runs");

    [Fact]
    public void A_free_name_is_kept_as_it_always_was_and_the_dir_is_made()
    {
        string runs = Runs();

        string dir = Launcher.ClaimRunDir(runs, Base, 4000);

        Assert.Equal(Path.Combine(runs, Base), dir);
        Assert.True(Directory.Exists(dir));
        Assert.Equal(["task.md"], [.. Directory.GetFiles(dir).Select(f => Path.GetFileName(f))]);
    }

    [Fact]
    public void A_different_name_in_the_same_second_is_free_and_unchanged()
    {
        string runs = Runs();

        string first = Launcher.ClaimRunDir(runs, Base, 4000);
        string other = Launcher.ClaimRunDir(runs, "20261122-115500-other", 4000);

        Assert.Equal(Path.Combine(runs, Base), first);
        Assert.Equal(Path.Combine(runs, "20261122-115500-other"), other);
    }

    [Fact]
    public void A_taken_name_gets_the_launcher_pid_as_a_suffix_and_the_older_run_is_untouched()
    {
        string runs = Runs();
        string existing = Path.Combine(runs, Base);
        _ = Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "task.md"), "older task");
        File.WriteAllText(Path.Combine(existing, "status.json"), "{}");

        string dir = Launcher.ClaimRunDir(runs, Base, 4000);

        Assert.Equal(Path.Combine(runs, Base + "-4000"), dir);
        Assert.True(Directory.Exists(dir));
        Assert.Equal("older task", File.ReadAllText(Path.Combine(existing, "task.md")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(existing, "status.json")));
    }

    [Fact]
    public void A_file_or_a_link_of_that_name_counts_as_taken()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string runs = Runs();
        _ = Directory.CreateDirectory(runs);
        File.WriteAllText(Path.Combine(runs, Base), "a file");
        const string linkName = "20261122-115500-linked";
        _ = File.CreateSymbolicLink(Path.Combine(runs, linkName), Path.Combine(_dirs.Create("lw-claim-gone"), "dangling"));

        string viaFile = Launcher.ClaimRunDir(runs, Base, 4001);
        string viaLink = Launcher.ClaimRunDir(runs, linkName, 4002);

        Assert.Equal(Path.Combine(runs, Base + "-4001"), viaFile);
        Assert.Equal(Path.Combine(runs, linkName + "-4002"), viaLink);
    }

    [Fact]
    public void A_name_that_is_taken_with_its_pid_suffix_too_is_a_launch_error_and_nothing_is_made()
    {
        string runs = Runs();
        _ = Directory.CreateDirectory(Path.Combine(runs, Base));
        _ = Directory.CreateDirectory(Path.Combine(runs, Base + "-4000"));

        LaunchException ex = Assert.Throws<LaunchException>(() => Launcher.ClaimRunDir(runs, Base, 4000));

        Assert.Contains(Base, ex.Message, StringComparison.Ordinal);
        Assert.Contains(Base + "-4000", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, Directory.GetDirectories(runs).Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(runs, Base)));
    }

    [Fact]
    public void The_same_launcher_claiming_the_same_name_a_third_time_is_a_launch_error()
    {
        string runs = Runs();
        string first = Launcher.ClaimRunDir(runs, Base, 4000);
        string second = Launcher.ClaimRunDir(runs, Base, 4000);

        _ = Assert.Throws<LaunchException>(() => Launcher.ClaimRunDir(runs, Base, 4000));

        Assert.Equal(Path.Combine(runs, Base), first);
        Assert.Equal(Path.Combine(runs, Base + "-4000"), second);
    }

    [Fact]
    public void The_suffixed_name_keeps_the_stamp_and_the_run_name_parseable()
    {
        string runs = Runs();
        _ = Launcher.ClaimRunDir(runs, Base, 4000);

        string id = Path.GetFileName(Launcher.ClaimRunDir(runs, Base, 4000));

        Assert.Equal("20261122-115500", id[..15]);
        Assert.Equal('-', id[15]);
        Assert.Equal("job-4000", StatusReader.StampName(id));
        Assert.Equal(id, Runtimes.RunId(Path.Combine(runs, id)));
        Assert.NotNull(OwnerKeys.RunId(id));
    }

    [Fact]
    public async Task Many_launchers_claiming_one_name_at_once_each_get_a_directory_of_their_ownAsync()
    {
        const int launchers = 16;
        for (int round = 0; round < 15; round++)
        {
            string runs = Runs();
            using Barrier barrier = new(launchers);
            Task<string>[] claims =
            [
                .. Enumerable.Range(0, launchers).Select(async i => await Task.Run(() =>
                {
                    _ = barrier.SignalAndWait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                    return Launcher.ClaimRunDir(runs, Base, 5000 + i);
                }, TestContext.Current.CancellationToken)),
            ];

            string[] dirs = await Task.WhenAll(claims);

            Assert.Equal(launchers, dirs.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(launchers, Directory.GetDirectories(runs).Length);
            _ = Assert.Single(dirs, d => string.Equals(Path.GetFileName(d), Base, StringComparison.Ordinal));
            for (int i = 0; i < launchers; i++)
            {
                string name = Path.GetFileName(dirs[i]);
                string own = Base + "-" + (5000 + i).ToString(CultureInfo.InvariantCulture);
                Assert.True(string.Equals(name, Base, StringComparison.Ordinal) || string.Equals(name, own, StringComparison.Ordinal), name);
                Assert.True(File.Exists(Path.Combine(dirs[i], "task.md")));
            }
        }
    }
}
