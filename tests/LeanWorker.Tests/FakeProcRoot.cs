using System.Globalization;

namespace LeanWorker.Tests;

/// <summary>
/// A directory laid out like /proc for the status code: a boot id and one stat file per process
/// the test adds. The real /proc is never read.
/// </summary>
internal sealed class FakeProcRoot : IDisposable
{
    public const string DefaultBoot = "boot-A";

    public FakeProcRoot(string? bootId = DefaultBoot)
    {
        Root = Directory.CreateTempSubdirectory("lw-proc").FullName;
        Fs = new ProcFs(Root);
        if (bootId is null)
        {
            return;
        }

        _ = Directory.CreateDirectory(Path.Combine(Root, "sys", "kernel", "random"));
        File.WriteAllText(Path.Combine(Root, "sys", "kernel", "random", "boot_id"), bootId + "\n");
    }

    public string Root { get; }

    public ProcFs Fs { get; }

    /// <summary>
    /// A stat line as the kernel writes it: 22 fields up to the start time, then a few more. The
    /// command name sits in parentheses; a test may give one with spaces and parentheses of its own.
    /// </summary>
    public static string Line(int pid, string comm, char state, int pgrp, int session, long startTicks)
    {
        CultureInfo ic = CultureInfo.InvariantCulture;
        string[] rest =
        [
            state.ToString(), "1", pgrp.ToString(ic), session.ToString(ic), "0", "-1", "4194560", "100", "0", "0", "0",
            "1", "2", "0", "0", "20", "0", "1", "0", startTicks.ToString(ic), "123456", "789",
        ];
        return pid.ToString(ic) + " (" + comm + ") " + string.Join(' ', rest) + "\n";
    }

    /// <summary>
    /// A live process (state S) whose group and session default to its own pid, as a setsid leader's do.
    /// </summary>
    public void Add(int pid, long startTicks, char state = 'S', int? pgrp = null, int? session = null, string comm = "worker") =>
        AddRaw(pid, Line(pid, comm, state, pgrp ?? pid, session ?? pid, startTicks));

    /// <summary>
    /// A process whose stat file holds exactly <paramref name="content"/>.
    /// </summary>
    public void AddRaw(int pid, string content)
    {
        string dir = Path.Combine(Root, pid.ToString(CultureInfo.InvariantCulture));
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stat"), content);
    }

    /// <summary>
    /// A process whose stat entry cannot be read as a file (a directory stands in for a permission error).
    /// </summary>
    public void AddUnreadable(int pid) =>
        Directory.CreateDirectory(Path.Combine(Root, pid.ToString(CultureInfo.InvariantCulture), "stat"));

    public void Remove(int pid) =>
        Directory.Delete(Path.Combine(Root, pid.ToString(CultureInfo.InvariantCulture)), recursive: true);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory must not fail the test that already finished.
        }
    }
}
