using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace PrivateGit.Services;

// SSH gate. sshd runs this for every connection to the git account through
// "ForceCommand /opt/privategit/PrivateGit git-serve --config /etc/privategit/environment"
// with "ExposeAuthInfo yes". The gate identifies the person from the public key that
// actually authenticated (SSH_USER_AUTH), finds the matching root-managed line in the
// authorized_keys file (marked "privategit:<user>"), checks the per-repository
// permission, and only then starts git upload-pack (read) or git receive-pack (write).
// Nothing else can run: no shell, no forwarding, no arbitrary commands or paths.
public static class GitServe
{
    private static readonly Regex Command = new(
        "^(?<verb>git-upload-pack|git-receive-pack|git upload-pack|git receive-pack|git-upload-archive|git upload-archive) '(?<path>[^'\\\\]{1,512})'$",
        RegexOptions.CultureInvariant);
    private static readonly Regex KeyMarker = new("(?<=^|\\s)privategit:(?<user>[a-z][a-z0-9_-]{2,31})(?=\\s|$)", RegexOptions.CultureInvariant);

    public sealed record Settings(string RepositoryDirectory, string AccessFile, string AuthorizedKeysFile, string GitExecutable,
        string HooksDirectory = "/opt/privategit/hooks", bool AllowBranchDelete = true,
        bool MainBackup = true, string BackupDirectory = "/srv/privategit/backups",
        string NasBackupDirectory = "", string NasBackupRepositories = "*");
    public sealed record Request(string Service, string Repository, bool Write);

    public static async Task<int> Run(string[] args)
    {
        string user = "unknown", detail = "";
        try
        {
            var settings = LoadSettings(args);
            var original = Environment.GetEnvironmentVariable("SSH_ORIGINAL_COMMAND");
            var authFile = Environment.GetEnvironmentVariable("SSH_USER_AUTH");
            var identified = IdentifyUser(authFile, settings.AuthorizedKeysFile);
            if (identified is null)
                return Deny("This SSH key is not linked to a PrivateGit user. Ask the administrator to run add-client.sh USER key.pub.", user, "unlinked-key");
            user = identified;
            if (string.IsNullOrEmpty(original))
                return Deny($"Hi {user}. Interactive shells are disabled; use git clone/pull/push.", user, "no-command", 1, quiet: true);
            var request = ParseCommand(original, settings.RepositoryDirectory);
            if (request is null) return Deny("Only git clone, fetch, pull and push are allowed.", user, "bad-command");
            detail = $"repo={request.Repository} op={(request.Write ? "push" : "fetch")}";
            var repositoryPath = Path.Combine(Path.GetFullPath(settings.RepositoryDirectory), request.Repository + ".git");
            var access = new AccessStore(settings.AccessFile);
            var accessData = access.Load();
            var permission = AccessStore.Check(accessData, user, request.Repository);
            var needed = request.Write ? Permission.Write : Permission.Read;
            // Same message for missing and forbidden repositories: do not reveal names.
            if (permission < needed || !Directory.Exists(repositoryPath) || !File.Exists(Path.Combine(repositoryPath, "HEAD"))
                || (File.GetAttributes(repositoryPath) & FileAttributes.ReparsePoint) != 0)
                return Deny(permission >= Permission.Read && request.Write
                    ? $"{user} has read-only access to {request.Repository}. Ask the administrator for write access."
                    : $"Repository '{request.Repository}' not found or {user} has no access.", user, "denied " + detail);
            Audit($"allow user={user} {detail}");
            return await Execute(settings, request.Service, repositoryPath, user, accessData.Admins.Contains(user));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            return Deny("The Git server is misconfigured. Ask the administrator to check the server log.", user, "error " + exception.GetType().Name + " " + detail);
        }
    }

    public static Settings LoadSettings(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string? config = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length) config = args[++i];
            else throw new ArgumentException("Usage: git-serve --config /etc/privategit/environment");
        }
        if (config is not null)
            foreach (var raw in File.ReadAllLines(config))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var split = line.IndexOf('=');
                if (split > 0) values[line[..split].Trim()] = line[(split + 1)..].Trim();
            }
        string Get(string key, string fallback) => values.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;
        var repositories = Get("PrivateGit__RepositoryDirectory", "/srv/privategit/repositories");
        return new(repositories,
            Get("PrivateGit__AccessFile", "/srv/privategit/access/access.json"),
            Get("PrivateGit__AuthorizedKeysFile", "/etc/privategit/authorized_keys"),
            Get("PrivateGit__GitExecutable", "/usr/bin/git"),
            Get("PrivateGit__HooksDirectory", "/opt/privategit/hooks"),
            !string.Equals(Get("PrivateGit__AllowBranchDelete", "true"), "false", StringComparison.OrdinalIgnoreCase),
            !string.Equals(Get("PrivateGit__MainBackup", "true"), "false", StringComparison.OrdinalIgnoreCase),
            Get("PrivateGit__BackupDirectory", "/srv/privategit/backups"),
            values.TryGetValue("PrivateGit__NasBackupDirectory", out var nas) ? nas : "",
            Get("PrivateGit__NasBackupRepositories", "*"));
    }

    // SSH_USER_AUTH lists the methods that succeeded, e.g. "publickey ssh-ed25519 AAAA...".
    public static string? IdentifyUser(string? authFile, string authorizedKeysFile)
    {
        if (string.IsNullOrEmpty(authFile) || !File.Exists(authFile)) return null;
        var keys = File.ReadAllLines(authFile)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 3 && p[0] == "publickey")
            .Select(p => (Type: p[1], Blob: p[2])).ToArray();
        if (keys.Length != 1) return null;
        string? found = null;
        foreach (var raw in File.ReadLines(authorizedKeysFile))
        {
            if (raw.TrimStart().StartsWith('#')) continue;
            var line = " " + raw;
            var position = line.IndexOf(" " + keys[0].Type + " " + keys[0].Blob, StringComparison.Ordinal);
            if (position < 0) continue;
            var after = line[(position + keys[0].Type.Length + keys[0].Blob.Length + 2)..];
            if (after.Length > 0 && after[0] != ' ') continue; // a longer blob that merely starts the same
            var markers = KeyMarker.Matches(after);
            if (markers.Count == 0) continue; // older unlinked line: never grants anything
            if (markers.Count != 1) return null;
            var marker = markers[0];
            // The same key linked to two people is ambiguous: refuse rather than guess.
            if (found is not null && found != marker.Groups["user"].Value) return null;
            found = marker.Groups["user"].Value;
        }
        return found;
    }

    public static Request? ParseCommand(string command, string repositoryDirectory)
    {
        var match = Command.Match(command);
        if (!match.Success) return null;
        var verb = match.Groups["verb"].Value.Replace(' ', '-');
        if (verb == "git-upload-archive") return null;
        var path = match.Groups["path"].Value;
        var root = Path.GetFullPath(repositoryDirectory).TrimEnd('/') + "/";
        if (path.StartsWith(root, StringComparison.Ordinal)) path = path[root.Length..];
        else if (path.StartsWith("~/", StringComparison.Ordinal)) path = path[2..];
        else if (path.StartsWith('/')) path = path[1..];
        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
        if (!AccessStore.RepositoryPattern.IsMatch(path)) return null;
        return new(verb, path, verb == "git-receive-pack");
    }

    public static bool NasSelected(string list, string repository) =>
        list.Trim() == "*" || list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(repository, StringComparer.Ordinal);

    // Branch deletion is allowed only when the root-owned pre-receive hook that protects the
    // main branch and tags is present (fail safe: otherwise deletes stay refused).
    public static bool BranchDeleteEnabled(Settings settings)
    {
        var hook = Path.Combine(settings.HooksDirectory, "pre-receive");
        return settings.AllowBranchDelete && File.Exists(hook) && !OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(hook) & UnixFileMode.UserExecute) != 0;
    }

    private static async Task<int> Execute(Settings settings, string service, string repository, string user, bool isAdmin)
    {
        var start = new ProcessStartInfo(settings.GitExecutable) { UseShellExecute = false };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.Ordinal) && k != "GIT_PROTOCOL").ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        // Repositories are created (owned) by the dashboard account, but SSH Git runs as the
        // git account. Git refuses repositories owned by another user ("dubious ownership")
        // unless marked safe; mark exactly this already-authorized repository, nothing else.
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("safe.directory=" + repository);
        if (service == "git-receive-pack" && Directory.Exists(settings.HooksDirectory))
        {
            // Only the central, root-owned hooks run on push (never per-repository hooks).
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("core.hooksPath=" + settings.HooksDirectory);
            if (BranchDeleteEnabled(settings))
            {
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add("receive.denyDeletes=false");
            }
            start.Environment["PRIVATEGIT_USER"] = user;
            start.Environment["PRIVATEGIT_ADMIN"] = isAdmin ? "1" : "0";
            // Hidden backups of the main branch, written by the post-receive hook.
            var name = Path.GetFileName(repository)[..^4];
            if (settings.MainBackup && Directory.Exists(settings.BackupDirectory))
                start.Environment["PRIVATEGIT_BACKUP_DIR"] = settings.BackupDirectory;
            // Optional NAS copy (no existence check here: a sleeping NAS must never delay the gate).
            if (settings.MainBackup && settings.NasBackupDirectory.StartsWith('/') && NasSelected(settings.NasBackupRepositories, name))
                start.Environment["PRIVATEGIT_NAS_DIR"] = settings.NasBackupDirectory;
        }
        start.ArgumentList.Add(service == "git-upload-pack" ? "upload-pack" : "receive-pack");
        start.ArgumentList.Add(repository);
        // stdin/stdout/stderr are inherited, so pack data streams directly between sshd and git.
        using var process = Process.Start(start) ?? throw new IOException("Git could not start.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static int Deny(string message, string user, string reason, int code = 1, bool quiet = false)
    {
        Console.Error.WriteLine("PrivateGit: " + message);
        if (!quiet || reason != "no-command") Audit($"deny user={user} {reason}");
        return code;
    }

    // Best-effort audit to the local syslog/journal (journalctl -t privategit-ssh).
    private static void Audit(string message)
    {
        try
        {
            if (OperatingSystem.IsWindows() || !File.Exists("/dev/log")) return;
            var client = Environment.GetEnvironmentVariable("SSH_CONNECTION")?.Split(' ').FirstOrDefault() ?? "-";
            var safe = Regex.Replace(message + " from=" + client, "[^\\x20-\\x7e]", "?");
            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint("/dev/log"));
            // authpriv.info = 10 * 8 + 6
            socket.Send(Encoding.UTF8.GetBytes($"<86>privategit-ssh[{Environment.ProcessId}]: {safe}"));
        }
        catch (Exception exception) when (exception is SocketException or IOException or UnauthorizedAccessException) { }
    }
}
