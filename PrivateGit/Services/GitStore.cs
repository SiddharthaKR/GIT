using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PrivateGit.Services;

public sealed record Repo(string Name);
public sealed record Branch(string Name, string Commit);
public sealed record TreeEntry(string Mode, string Type, string Object, string Name);
public sealed record Commit(string Id, string Author, string Date, string Subject, string Committer = "", string CommittedDate = "");
public sealed record Tag(string Name, string Commit, string Date, string Subject, bool Annotated);
public sealed record Revision(string Label, string Commit);
public sealed record HistoryPage(IReadOnlyList<Commit> Commits, bool HasMore);
public sealed record CommitDetails(Commit Commit, IReadOnlyList<string> Parents, string Message);
public sealed class GitException(string message) : Exception(message);

public sealed class GitStore
{
    private readonly string root;
    private readonly string executable;
    private readonly string cloneBase;
    private readonly SemaphoreSlim slots = new(4, 4);
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9_-]{0,62}$", RegexOptions.CultureInvariant);
    private static readonly Regex ObjectPattern = new("^[a-f0-9]{40}([a-f0-9]{24})?$", RegexOptions.CultureInvariant);
    public GitStore(IConfiguration configuration)
    {
        root = Path.GetFullPath(configuration["PrivateGit:RepositoryDirectory"] ?? "App_Data/repositories");
        executable = configuration["PrivateGit:GitExecutable"] ?? "git";
        cloneBase = configuration["PrivateGit:CloneBaseUrl"] ?? "git@CHANGE-ME:/srv/privategit/repositories";
        Directory.CreateDirectory(root);
        RejectLink(root);
    }
    public string CloneUrl(string name) { _ = RepositoryPath(name); return $"{cloneBase.TrimEnd('/')}/{name}.git"; }
    public string RepositoryPath(string name)
    {
        if (!NamePattern.IsMatch(name)) throw new GitException("Invalid repository name.");
        var path = Path.Combine(root, name + ".git");
        RejectLink(path);
        return path;
    }
    private static void RejectLink(string path)
    {
        if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new GitException("Symbolic repository paths are not supported.");
    }
    public IReadOnlyList<Repo> List() => Directory.EnumerateDirectories(root, "*.git")
        .Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
        .Select(p => Path.GetFileName(p)[..^4]).Where(n => NamePattern.IsMatch(n))
        .Order(StringComparer.Ordinal).Take(500).Select(n => new Repo(n)).ToArray();

    public async Task Create(string name, CancellationToken cancellation = default)
    {
        var destination = RepositoryPath(name);
        if (Directory.Exists(destination)) throw new GitException("A repository with this name already exists.");
        var staging = Path.Combine(root, ".creating-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Run(null, ["init", "--bare", "--shared=group", "--initial-branch=main", staging], cancellation);
            await Run(staging, ["config", "receive.denyNonFastForwards", "true"], cancellation);
            await Run(staging, ["config", "receive.denyDeletes", "true"], cancellation);
            Directory.Move(staging, destination);
        }
        catch (IOException) { throw new GitException("Repository creation conflicted with another operation."); }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private string Existing(string name)
    {
        var path = RepositoryPath(name);
        if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, "HEAD"))) throw new GitException("Repository not found.");
        return path;
    }
    private static void ValidateObject(string id)
    {
        if (!ObjectPattern.IsMatch(id)) throw new GitException("Invalid Git object identifier.");
    }
    public async Task<IReadOnlyList<Branch>> Branches(string name, CancellationToken cancellation = default)
    {
        var output = await Run(Existing(name), ["for-each-ref", "--count=200", "--format=%(objectname) %(refname:strip=2)", "refs/heads/"], cancellation);
        return Encoding.UTF8.GetString(output).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split(' ', 2)).Where(p => p.Length == 2 && ObjectPattern.IsMatch(p[0]))
            .Select(p => new Branch(p[1], p[0])).ToArray();
    }
    public async Task<string> DefaultBranch(string name, CancellationToken cancellation = default)
    {
        try
        {
            var head = Encoding.UTF8.GetString(await Run(Existing(name), ["symbolic-ref", "-q", "HEAD"], cancellation)).Trim();
            return head.StartsWith("refs/heads/", StringComparison.Ordinal) ? head[11..] : "main";
        }
        catch (GitException) { return "main"; }
    }
    // Branch creators recorded by the SSH post-receive hook (branch.<name>.privategitowner).
    public async Task<IReadOnlyDictionary<string, string>> BranchOwners(string name, CancellationToken cancellation = default)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        byte[] output;
        try { output = await Run(Existing(name), ["config", "--local", "-z", "--get-regexp", "^branch\\..*\\.privategitowner$"], cancellation); }
        catch (GitException) { return owners; } // exit code 1 = no owners recorded yet
        foreach (var entry in Encoding.UTF8.GetString(output).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = entry.IndexOf('\n');
            if (split < 0) continue;
            var key = entry[..split]; var value = entry[(split + 1)..];
            const string prefix = "branch.", suffix = ".privategitowner";
            if (key.Length > prefix.Length + suffix.Length && key.StartsWith(prefix, StringComparison.Ordinal) && key.EndsWith(suffix, StringComparison.Ordinal)
                && AccessStore.UserPattern.IsMatch(value))
                owners[key[prefix.Length..^suffix.Length]] = value;
        }
        return owners;
    }
    public static bool MayDelete(string branch, string mainBranch, string? owner, string user, bool isAdmin) =>
        branch != mainBranch && branch != "main" && (isAdmin || (owner is not null && owner == user));

    // Deletes a branch: never the default/main branch, and only by its creator or an admin.
    // The name must match an existing branch exactly; deletion is conditional on the commit
    // the user saw.
    public async Task DeleteBranch(string name, string branch, string expectedCommit, string user, bool isAdmin, CancellationToken cancellation = default)
    {
        ValidateObject(expectedCommit);
        var target = (await Branches(name, cancellation)).FirstOrDefault(b => b.Name == branch)
            ?? throw new GitException("Branch not found. It may already have been deleted.");
        var protectedBranch = await DefaultBranch(name, cancellation);
        if (branch == protectedBranch || branch == "main")
            throw new GitException($"The main branch ({branch}) cannot be deleted.");
        var owner = (await BranchOwners(name, cancellation)).GetValueOrDefault(branch);
        if (!MayDelete(branch, protectedBranch, owner, user, isAdmin))
            throw new GitException($"Branch {branch} was created by {owner ?? "an unknown user (before ownership tracking)"}. Only its creator or an administrator can delete it.");
        if (target.Commit != expectedCommit)
            throw new GitException("The branch changed since you opened this page. Reload and try again.");
        await Run(Existing(name), ["update-ref", "-d", "refs/heads/" + target.Name, target.Commit], cancellation);
        try { await Run(Existing(name), ["config", "--local", "--unset", "branch." + target.Name + ".privategitowner"], cancellation); }
        catch (GitException) { } // no owner was recorded
    }
    public async Task<IReadOnlyList<TreeEntry>> Tree(string name, string id, CancellationToken cancellation = default)
    {
        ValidateObject(id);
        var output = await Run(Existing(name), ["ls-tree", "-z", id], cancellation);
        return Encoding.UTF8.GetString(output).Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            var parts = line.Split('\t', 2); var metadata = parts[0].Split(' ');
            return new TreeEntry(metadata[0], metadata[1], metadata[2], parts[1]);
        }).OrderBy(e => e.Type == "tree" ? 0 : 1).ThenBy(e => e.Name, StringComparer.Ordinal).ToArray();
    }
    public async Task<IReadOnlyList<Commit>> History(string name, string id, CancellationToken cancellation = default)
    {
        return (await PagedHistory(name, id, null, 1, cancellation)).Commits;
    }
    public async Task<IReadOnlyList<Tag>> Tags(string name, CancellationToken cancellation = default)
    {
        var bytes = await Run(Existing(name), ["for-each-ref", "--count=200", "--sort=-creatordate",
            "--format=%(refname:strip=2)%00%(objectname)%00%(*objectname)%00%(objecttype)%00%(*objecttype)%00%(creatordate:iso-strict)%00%(subject)%00", "refs/tags/"], cancellation);
        var fields = Encoding.UTF8.GetString(bytes).Split('\0'); var tags = new List<Tag>();
        for (var i = 0; i + 6 < fields.Length; i += 7)
        {
            var target = fields[i + 3] == "commit" ? fields[i + 1] : fields[i + 4] == "commit" ? fields[i + 2] : "";
            if (ObjectPattern.IsMatch(target)) tags.Add(new(fields[i].TrimStart('\r', '\n'), target, fields[i + 5], fields[i + 6], fields[i + 3] == "tag"));
        }
        return tags;
    }
    public async Task<Revision> Resolve(string name, string? revision, CancellationToken cancellation = default)
    {
        if (revision?.Length > 1100) throw new GitException("Invalid revision.");
        if (revision is null || revision.StartsWith("branch:", StringComparison.Ordinal))
        {
            var branches = await Branches(name, cancellation);
            var branch = revision is null ? branches.FirstOrDefault(b => b.Name == "main") ?? branches.FirstOrDefault()
                : branches.FirstOrDefault(b => b.Name == revision[7..]);
            if (branch is not null) return new("branch:" + branch.Name, branch.Commit);
        }
        else if (revision.StartsWith("tag:", StringComparison.Ordinal))
        {
            var tag = (await Tags(name, cancellation)).FirstOrDefault(t => t.Name == revision[4..]);
            if (tag is not null) return new("tag:" + tag.Name, tag.Commit);
        }
        else if (ObjectPattern.IsMatch(revision))
        {
            var type = Encoding.ASCII.GetString(await Run(Existing(name), ["cat-file", "-t", revision], cancellation)).Trim();
            if (type == "commit") return new(revision, revision);
        }
        throw new GitException("Revision not found. Select a branch, a commit-targeted tag, or a full commit ID.");
    }
    public static bool ValidPath(string? path) => string.IsNullOrEmpty(path) ||
        (path.Length <= 2048 && !path.Contains('\\') && !path.Any(char.IsControl) &&
         path.Split('/').Length <= 24 && !path.Split('/').Any(s => s is "" or "." or ".."));

    public async Task<HistoryPage> PagedHistory(string name, string id, string? path, int page, CancellationToken cancellation = default)
    {
        ValidateObject(id);
        if (!ValidPath(path) || page is < 1 or > 1000) throw new GitException("Invalid history path or page.");
        var arguments = new List<string> { "log", "--no-show-signature", "--no-notes", "--no-decorate", "--max-count=21",
            "--skip=" + ((page - 1) * 20).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--format=%H%x00%an%x00%aI%x00%s%x00%cn%x00%cI%x00", id, "--" };
        if (!string.IsNullOrEmpty(path)) arguments.Add(":(literal)" + path);
        var fields = Encoding.UTF8.GetString(await Run(Existing(name), arguments.ToArray(), cancellation)).Split('\0');
        var result = new List<Commit>();
        for (var i = 0; i + 5 < fields.Length; i += 6)
        {
            var commit = fields[i].Trim();
            if (!ObjectPattern.IsMatch(commit)) throw new GitException("Malformed commit metadata.");
            result.Add(new(commit, fields[i + 1], fields[i + 2], fields[i + 3], fields[i + 4], fields[i + 5]));
        }
        return new(result.Take(20).ToArray(), result.Count > 20);
    }
    public async Task<CommitDetails> Details(string name, string id, CancellationToken cancellation = default)
    {
        ValidateObject(id);
        var fields = Encoding.UTF8.GetString(await Run(Existing(name), ["show", "--no-show-signature", "--no-notes", "-s",
            "--format=%H%x00%an%x00%aI%x00%s%x00%cn%x00%cI%x00%P%x00%B%x00", id, "--"], cancellation)).Split('\0');
        if (fields.Length < 9 || !ObjectPattern.IsMatch(fields[0].Trim())) throw new GitException("Malformed commit metadata.");
        var parents = fields[6].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var parent in parents) ValidateObject(parent);
        return new(new(fields[0].Trim(), fields[1], fields[2], fields[3], fields[4], fields[5]), parents, fields[7].TrimEnd());
    }
    public async Task<ChangeSet> Changes(string name, string? before, string after, CancellationToken cancellation = default)
    {
        ValidateObject(after); if (before is not null) ValidateObject(before);
        var repository = Existing(name);
        string[] Arguments(string format)
        {
            var arguments = new List<string> { "diff-tree", "--root", "-r", "--no-commit-id", "--no-renames", "--no-ext-diff", "--no-textconv",
                "--no-color", "--no-abbrev", "--no-relative", "--ignore-submodules=none", "--diff-algorithm=myers", "--src-prefix=a/", "--dst-prefix=b/" };
            arguments.Add(format);
            if (format != "--patch") arguments.Add("-z"); else arguments.Add("--unified=3");
            if (before is not null) arguments.Add(before);
            arguments.Add(after); arguments.Add("--");
            return arguments.ToArray();
        }
        var raw = Encoding.UTF8.GetString(await Run(repository, Arguments("--raw"), cancellation)).Split('\0');
        var files = new List<ChangedFile>();
        for (var i = 0; i + 1 < raw.Length; i += 2)
        {
            var metadata = raw[i].Split(' ');
            if (metadata.Length != 5 || !metadata[0].StartsWith(':')) throw new GitException("Malformed change metadata.");
            files.Add(new(raw[i + 1], metadata[4], metadata[0][1..], metadata[1], null, null, []));
        }
        if (files.Count > 200) return new(files.Take(200).ToArray(), files.Count, "More than 200 files changed. Showing the first 200 file names; clone locally to inspect the complete diff.");
        if (files.Count == 0) return new([], 0, null);
        var statistics = Encoding.UTF8.GetString(await Run(repository, Arguments("--numstat"), cancellation)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (statistics.Length != files.Count) throw new GitException("Change statistics did not match the file list.");
        for (var i = 0; i < files.Count; i++)
        {
            var stat = statistics[i].Split('\t', 3);
            if (stat.Length != 3 || stat[2] != files[i].Path) throw new GitException("Change statistics did not match the file list.");
            files[i] = files[i] with { Added = int.TryParse(stat[0], out var added) ? added : null, Removed = int.TryParse(stat[1], out var removed) ? removed : null };
        }
        try
        {
            var patch = Encoding.UTF8.GetString(await Run(repository, Arguments("--patch"), cancellation));
            var sections = Regex.Split(patch, "(?m)^diff --git ").Skip(1).ToArray();
            if (sections.Length != files.Count) return new(files, files.Count, "Patch layout could not be matched safely. Clone locally to inspect this diff.");
            for (var i = 0; i < files.Count; i++) files[i] = files[i] with { Lines = DiffParser.Parse(sections[i]), Truncated = sections[i].Length > DiffParser.MaxCharacters || sections[i].Count(c => c == '\n') > DiffParser.MaxLines };
            return new(files, files.Count, null);
        }
        catch (GitException exception) { return new(files, files.Count, "Patch preview unavailable: " + exception.Message); }
    }
    public async Task<string> Blob(string name, string id, CancellationToken cancellation = default)
    {
        ValidateObject(id); var path = Existing(name);
        var type = Encoding.ASCII.GetString(await Run(path, ["cat-file", "-t", id], cancellation)).Trim();
        if (type != "blob") throw new GitException("This object is not a file.");
        var size = long.Parse(Encoding.ASCII.GetString(await Run(path, ["cat-file", "-s", id], cancellation)).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        if (size > 256 * 1024) throw new GitException("Preview limited to 256 KiB. Clone the repository to open this file.");
        var bytes = await Run(path, ["cat-file", "blob", id], cancellation);
        if (bytes.Contains((byte)0)) throw new GitException("Binary preview is disabled. Clone the repository to open this file.");
        return Encoding.UTF8.GetString(bytes);
    }
    private async Task<byte[]> Run(string? repository, string[] arguments, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        await slots.WaitAsync(deadline.Token);
        using var process = new Process();
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true };
            foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
            start.ArgumentList.Add("--no-pager");
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.hooksPath=" + Path.Combine(root, ".disabled-hooks"));
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.fsmonitor=false");
            if (repository is not null) { start.ArgumentList.Add("--git-dir"); start.ArgumentList.Add(repository); }
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            process.StartInfo = start;
            if (!process.Start()) throw new GitException("Git could not start.");
            var stdout = ReadLimited(process.StandardOutput.BaseStream, 2 * 1024 * 1024, deadline.Token);
            var stderr = ReadLimited(process.StandardError.BaseStream, 16 * 1024, deadline.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
            if (process.ExitCode != 0) throw new GitException("Git could not complete this operation. Check the repository and server logs.");
            return await stdout;
        }
        catch (OperationCanceledException) { throw new GitException("Git operation timed out or was cancelled."); }
        finally
        {
            try { if (process.Id != 0 && !process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            slots.Release();
        }
    }
    private static async Task<byte[]> ReadLimited(Stream source, int maximum, CancellationToken cancellation)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, cancellation)) != 0)
        {
            if (output.Length + count > maximum) throw new GitException("Git output exceeds the preview limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
