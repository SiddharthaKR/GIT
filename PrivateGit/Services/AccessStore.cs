using System.Text.Json;
using System.Text.RegularExpressions;

namespace PrivateGit.Services;

public enum Permission { None = 0, Read = 1, Write = 2 }

// Per-repository, per-user permissions shared by the web dashboard (gitdash) and the
// SSH gate (git account). The file contains no secrets: account names, repository
// names and permission levels only. It is group-readable by gitrepos and writable
// only by the dashboard service account.
public sealed class AccessData
{
    public SortedSet<string> Admins { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, SortedDictionary<string, string>> Repositories { get; set; } = new(StringComparer.Ordinal);
}

public sealed class AccessStore
{
    public static readonly Regex UserPattern = new("^[a-z][a-z0-9_-]{2,31}$", RegexOptions.CultureInvariant);
    public static readonly Regex RepositoryPattern = new("^[a-z][a-z0-9_-]{0,62}$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    public string File { get; }

    public AccessStore(string file)
    {
        File = Path.GetFullPath(file);
        var directory = Path.GetDirectoryName(File)!;
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
    }

    public static Permission Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "read" or "r" => Permission.Read,
        "write" or "rw" or "w" => Permission.Write,
        "none" or "" or null => Permission.None,
        _ => throw new ArgumentException("Permission must be none, read or write."),
    };
    public static string Format(Permission permission) => permission.ToString().ToLowerInvariant();

    // Readers never take a lock: writers replace the file atomically.
    public AccessData Load()
    {
        if (!System.IO.File.Exists(File)) return new();
        using var stream = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Access file is unexpectedly large.");
        var data = JsonSerializer.Deserialize<AccessData>(stream, Json) ?? new();
        // Normalise comparers after deserialization and drop anything malformed.
        var clean = new AccessData();
        foreach (var admin in data.Admins ?? []) if (UserPattern.IsMatch(admin)) clean.Admins.Add(admin);
        foreach (var (repository, grants) in data.Repositories ?? new())
        {
            if (!RepositoryPattern.IsMatch(repository) || grants is null) continue;
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (user, level) in grants)
            {
                if (!UserPattern.IsMatch(user)) continue;
                Permission parsed;
                try { parsed = Parse(level); } catch (ArgumentException) { continue; }
                if (parsed != Permission.None) map[user] = Format(parsed);
            }
            clean.Repositories[repository] = map;
        }
        return clean;
    }

    public static Permission Check(AccessData data, string user, string repository)
    {
        if (data.Admins.Contains(user)) return Permission.Write;
        return data.Repositories.TryGetValue(repository, out var grants) && grants.TryGetValue(user, out var level)
            ? Parse(level) : Permission.None;
    }
    public Permission Check(string user, string repository) => Check(Load(), user, repository);

    public async Task Update(Action<AccessData> change)
    {
        await gate.WaitAsync();
        try
        {
            // Cross-process lock so the CLI can grant access while the dashboard runs.
            var directory = Path.GetDirectoryName(File)!;
            using var processLock = await AcquireLock(Path.Combine(directory, "access.lock"));
            var data = Load();
            change(data);
            var temporary = Path.Combine(directory, ".access-" + Guid.NewGuid().ToString("N") + ".tmp");
            await System.IO.File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(data, Json));
            if (!OperatingSystem.IsWindows())
                System.IO.File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            System.IO.File.Move(temporary, File, true);
        }
        finally { gate.Release(); }
    }
    private static async Task<FileStream> AcquireLock(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { await Task.Delay(50); }
        }
    }

    public Task Grant(string repository, string user, Permission permission)
    {
        if (!RepositoryPattern.IsMatch(repository)) throw new ArgumentException("Invalid repository name.");
        if (!UserPattern.IsMatch(user)) throw new ArgumentException("Invalid username.");
        return Update(data =>
        {
            if (!data.Repositories.TryGetValue(repository, out var grants))
                data.Repositories[repository] = grants = new(StringComparer.Ordinal);
            if (permission == Permission.None) grants.Remove(user); else grants[user] = Format(permission);
        });
    }
    public Task RemoveUser(string user) => Update(data =>
    {
        data.Admins.Remove(user);
        foreach (var grants in data.Repositories.Values) grants.Remove(user);
    });
    public Task SyncAdmins(IEnumerable<string> admins) => Update(data =>
    {
        data.Admins.Clear();
        foreach (var admin in admins) if (UserPattern.IsMatch(admin)) data.Admins.Add(admin);
    });
    // Admins are mirrored from the account store so the SSH gate (which cannot read
    // account secrets) knows who has implicit access. Only writes when something changed.
    public async Task EnsureAdmins(IEnumerable<string> admins)
    {
        var wanted = new SortedSet<string>(admins.Where(a => UserPattern.IsMatch(a)), StringComparer.Ordinal);
        if (!Load().Admins.SetEquals(wanted) || !System.IO.File.Exists(File)) await SyncAdmins(wanted);
    }
}
