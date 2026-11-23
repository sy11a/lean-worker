// The per-user index of runs (docs/research/live-status/README.md, section 4.1): one pointer file
// per run round, $XDG_STATE_HOME/lean-worker/runs/<stamp>-<launcher-pid>-r<round>.json (schema
// lean-worker.pointer/1), written once by the launcher right after the first status.json and never
// rewritten; `status` finds the runs of a session through it, whichever runs root they live under.
// LEAN_WORKER_STATE_DIR moves the index (to <that>/runs), for tests and tools.
//
// A pointer is as untrusted as a status file (any process of the user can write the folder): the
// reader takes it through StatusFile and checks every field it uses. Deleting (reaping) is limited
// to regular files directly in the index folder whose name has the pointer form.

using System.Globalization;
using System.Text.Json.Nodes;

namespace LeanWorker;

internal static class StatusIndex
{
    public const string Schema = "lean-worker.pointer/1";
    public const string StateDirVariable = "LEAN_WORKER_STATE_DIR";

    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// The index folder for an environment: &lt;LEAN_WORKER_STATE_DIR&gt;/runs, else
    /// &lt;XDG_STATE_HOME&gt;/lean-worker/runs, else ~/.local/state/lean-worker/runs. A value that is
    /// not an absolute path is passed over (as the XDG rules say for XDG_STATE_HOME); null when no
    /// value gives one.
    /// </summary>
    public static string? Folder(Func<string, string?> env)
    {
        if (Absolute(env(StateDirVariable)) is { } stateDir)
        {
            return Path.Combine(stateDir, "runs");
        }

        if (Absolute(env("XDG_STATE_HOME")) is { } xdg)
        {
            return Path.Combine(xdg, "lean-worker", "runs");
        }

        return Absolute(env("HOME")) is { } home ? Path.Combine(home, ".local", "state", "lean-worker", "runs") : null;
    }

    private static string? Absolute(string? path) => path is { Length: > 0 } && Path.IsPathFullyQualified(path) ? path : null;

    /// <summary>
    /// The pointer's file name: the run's start stamp (local time, as in its run dir's name), the
    /// launcher's pid and the round.
    /// </summary>
    public static string FileName(DateTimeOffset started, int launcherPid, int round) =>
        string.Create(CultureInfo.InvariantCulture, $"{started:yyyyMMdd-HHmmss}-{launcherPid}-r{round}.json");

    /// <summary>
    /// The most names <see cref="Write"/> tries: the plain one, then a suffix -2 to -99.
    /// </summary>
    private const int NameTries = 99;

    /// <summary>
    /// Whether <paramref name="name"/> has the pointer form: 8 digits, a dash, 6 digits, a dash, a
    /// pid of 1 to 10 digits, "-r", a round of 1 to 6 digits, optionally a dash and one or two
    /// digits (the suffix of a name that was taken), and ".json".
    /// </summary>
    public static bool IsPointerName(string name)
    {
        if (!name.EndsWith(".json", StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = name[..^".json".Length].Split('-');
        return parts.Length is 4 or 5
               && Digits(parts[0], 8, 8) && Digits(parts[1], 6, 6) && Digits(parts[2], 1, 10)
               && parts[3].Length > 1 && parts[3][0] is 'r' && Digits(parts[3][1..], 1, 6)
               && (parts.Length is 4 || Digits(parts[4], 1, 2));
    }

    private static bool Digits(string text, int min, int max) => text.Length >= min && text.Length <= max && text.All(c => char.IsAsciiDigit(c));

    /// <summary>
    /// The pointer of one run round.
    /// </summary>
    public static JsonObject Pointer(DateTimeOffset createdAt, string runDir, string chainId, string? parentRun,
        IEnumerable<string> ownerKeys, JsonObject launcher)
    {
        return new JsonObject
        {
            ["schema"] = Schema,
            ["created_at"] = RunStatus.Stamp(createdAt),
            ["run_dir"] = runDir,
            ["chain_id"] = chainId,
            ["parent_run"] = parentRun,
            ["owner_keys"] = RunOwner.Array(ownerKeys),
            ["launcher"] = launcher,
        };
    }

    /// <summary>
    /// The prefix of the claim file that guards a pointer name while it is published; a claim never
    /// has the pointer form, so no listing takes it for a pointer.
    /// </summary>
    private const string ClaimPrefix = ".claim-";

    /// <summary>
    /// Writes a pointer once: the folder is made with mode 0700 when it is missing, and the bytes go
    /// to a temp sibling made with mode 0600. A name is published only under its claim, an empty
    /// file ".claim-&lt;name&gt;" made with an exclusive create (O_CREAT|O_EXCL, which the kernel makes
    /// atomic for threads and processes alike): the claim's holder checks that the name is free and
    /// renames the temp onto it, then deletes the claim. A reader sees either no file of that name
    /// or the whole pointer. File.Move without overwrite alone is not enough: on Unix it checks the
    /// name and then renames, and rename(2) replaces a file that appeared in between. When the name
    /// or its claim is taken (one process that launched two runs of one round in the same second),
    /// the next suffix -2 to -99 is tried; when all are taken, an <see cref="IOException"/> is
    /// thrown. Returns the name written.
    /// </summary>
    public static string Write(string folder, string fileName, JsonObject pointer)
    {
        string stem = fileName.EndsWith(".json", StringComparison.Ordinal) ? fileName[..^".json".Length] : fileName;
        string temp = WriteTemp(folder, pointer);
        try
        {
            for (int k = 1; k <= NameTries; k++)
            {
                string name = k is 1 ? fileName : string.Create(CultureInfo.InvariantCulture, $"{stem}-{k}.json");
                if (Publish(folder, name, temp))
                {
                    return name;
                }
            }

            throw new IOException(string.Create(CultureInfo.InvariantCulture,
                $"The pointer name {fileName} and its suffixes -2 to -{NameTries} are all taken in {folder}."));
        }
        finally
        {
            Delete(temp);
        }
    }

    /// <summary>
    /// The pointer's bytes in a new temp sibling (".tmp-" and a random name, mode 0600); the folder
    /// is made first when it is missing. Returns the temp's path.
    /// </summary>
    private static string WriteTemp(string folder, JsonObject pointer)
    {
        byte[] bytes = Json.Utf8.GetBytes(pointer.ToJsonString(Json.Indented));
        string temp = Path.Combine(folder, ".tmp-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            _ = Directory.CreateDirectory(folder);
        }
        else
        {
            _ = Directory.CreateDirectory(folder, DirectoryMode);
        }

        try
        {
            using FileStream fs = new(temp, CreateNewOptions());
            fs.Write(bytes);
        }
        catch
        {
            Delete(temp);
            throw;
        }

        return temp;
    }

    /// <summary>
    /// Publishes <paramref name="temp"/> as <paramref name="name"/> under the name's claim. False
    /// when the claim is held by another writer or the name exists; the claim is always released.
    /// </summary>
    private static bool Publish(string folder, string name, string temp)
    {
        string path = Path.Combine(folder, name);
        string claim = Path.Combine(folder, ClaimPrefix + name);
        try
        {
            new FileStream(claim, CreateNewOptions()).Dispose();
        }
        catch (IOException) when (File.Exists(claim) || File.Exists(path))
        {
            // Held by another writer, or released by it since: a holder lets go only after it
            // published the name or found it taken.
            return false;
        }

        try
        {
            // Under the claim no other writer publishes this name, so the check and the rename that
            // follows it cannot be overtaken.
            if (File.Exists(path))
            {
                return false;
            }

            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            // Taken after all, by a writer that does not claim (an older launcher).
            return false;
        }
        finally
        {
            Delete(claim);
        }
    }

    private static FileStreamOptions CreateNewOptions()
    {
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode600;
        }

        return options;
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The pointer files in the index folder, by name, as far as the listing got within the
    /// budget: only names of the pointer form, never a path below the folder. The listing itself
    /// runs on the thread with the deadline (.NET's enumeration stats an entry that is a link to
    /// tell whether its target is a directory), and Complete is false when it did not finish. A
    /// folder that is missing or is itself a symbolic link lists nothing.
    /// </summary>
    public static (List<string> Names, bool Complete) List(string folder, ReadBudget budget) => budget.List(add => Collect(folder, add));

    private static void Collect(string folder, Action<string> add)
    {
        if (StatusFile.IsLink(folder) || !Directory.Exists(folder))
        {
            return;
        }

        foreach (string name in Directory.EnumerateFiles(folder).Select(path => Path.GetFileName(path)).Where(name => IsPointerName(name)))
        {
            add(name);
        }
    }

    /// <summary>
    /// Deletes the pointer <paramref name="name"/> in <paramref name="folder"/> when it is still a
    /// regular file of the pointer form (no link, no directory, not empty). A pointer already gone
    /// counts as deleted; any other failure, or a delete that does not answer within the budget, is
    /// left alone. Nothing here throws.
    /// </summary>
    public static void Reap(string folder, string name, ReadBudget budget)
    {
        if (!IsPointerName(name) || Path.GetFileName(name) != name)
        {
            return;
        }

        string path = Path.Combine(folder, name);
        try
        {
            _ = budget.Run(() => DeleteIfPointer(folder, path), out bool _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Another reader got there first, or the file cannot be deleted: it stays for the next one.
        }
    }

    private static bool DeleteIfPointer(string folder, string path)
    {
        FileInfo info = new(path);
        if (info.LinkTarget is not null || !info.Exists || info.Attributes.HasFlag(FileAttributes.Directory) || info.Length is 0)
        {
            return false;
        }

        // Checked again right before the delete: a folder swapped for a link since it was listed
        // would have the delete land in the link's target.
        if (StatusFile.IsLink(folder))
        {
            return false;
        }

        info.Delete();
        return true;
    }
}
