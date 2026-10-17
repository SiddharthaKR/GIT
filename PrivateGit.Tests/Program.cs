using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using PrivateGit.Services;

// Dependency-free security/integration test runner. Never creates development accounts.
var temporary = Path.Combine(Path.GetTempPath(), "privategit-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passed++;
}
async Task ExpectArgument(Func<Task> action, string name)
{
    try { await action(); throw new Exception("FAIL: " + name); } catch (ArgumentException) { Check(true, name); }
}
async Task ExpectGitError(Func<Task> action, string name)
{
    try { await action(); throw new Exception("FAIL: " + name); } catch (GitException) { Check(true, name); }
}
var source = Path.GetFullPath("PrivateGit");
var assembly = Path.Combine(source, "bin", "Release", "net10.0", "PrivateGit.dll");
var dotnet = Environment.ProcessPath!;
var data = Path.Combine(temporary, "state");
var repositories = Path.Combine(temporary, "repositories");
var password = "Test-only-long-passphrase!983";
string adminSecret, readerSecret;
Process? server = null;
try
{
    var rfcSecret = Totp.Encode(Encoding.ASCII.GetBytes("12345678901234567890"));
    Check(Totp.Code(rfcSecret, 1) == "287082", "RFC 6238 / RFC 4226 known vector");
    foreach (var sample in new[] { (1111111109L, "081804"), (1111111111L, "050471"), (1234567890L, "005924"), (2000000000L, "279037"), (20000000000L, "353130") })
        Check(Totp.Code(rfcSecret, sample.Item1 / 30) == sample.Item2, "RFC TOTP vector " + sample.Item1);
    Check(Totp.Decode(rfcSecret).SequenceEqual(Encoding.ASCII.GetBytes("12345678901234567890")), "Base32 round trip");
    var now = DateTimeOffset.UtcNow;
    Check(Totp.Verify(rfcSecret, Totp.Code(rfcSecret, now.ToUnixTimeSeconds() / 30), now, now.ToUnixTimeSeconds() / 30) is null, "Reject replayed TOTP");
    Check(Totp.Verify(rfcSecret, "bad", now, -1) is null, "Reject malformed TOTP");
    using (var accounts = new AccountStore(data))
    {
        Check(await accounts.Put("admin", password, "Admin", false), "Create admin with hashed password");
        Check(await accounts.Put("reader", password, "Reader", false), "Create reader");
        Check(!await accounts.Put("admin", password, "Admin", false), "Refuse duplicate account");
        adminSecret = (await accounts.Find("admin"))!.TotpSecret;
        readerSecret = (await accounts.Find("reader"))!.TotpSecret;
        await accounts.Put("locked", password, "Reader", false);
        var locked = (await accounts.Find("locked"))!;
        for (var i = 0; i < 5; i++) await accounts.Authenticate("locked", "incorrect", "000000");
        Check((await accounts.Find("locked"))!.LockedUntil > DateTimeOffset.UtcNow, "Persistent account lockout after five failures");
        Check(await accounts.Authenticate("locked", password, Totp.Code(locked.TotpSecret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30)) is null, "Correct credentials cannot bypass lockout");
        Check(!(await File.ReadAllTextAsync(Path.Combine(data, "accounts.json"))).Contains(password), "No plaintext password in account storage");
        var oldStamp = (await accounts.Find("locked"))!.Stamp;
        await accounts.Put("locked", password, "Reader", true);
        Check((await accounts.Find("locked"))!.Stamp != oldStamp, "Account reset rotates session stamp");
    }
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["PrivateGit:RepositoryDirectory"] = repositories, ["PrivateGit:CloneBaseUrl"] = "git@test:/srv/repos" }).Build();
    var git = new GitStore(configuration);
    await git.Create("example");
    Check(git.List().Single().Name == "example", "Create/list bare repository");
    Check((await git.Branches("example")).Count == 0, "Empty remote has no fabricated branch");
    Check(File.ReadAllText(Path.Combine(repositories, "example.git", "HEAD")).Contains("refs/heads/main"), "Remote HEAD points to main");
    await ExpectGitError(() => git.Create("../escape"), "Reject repository traversal");
    await ExpectGitError(() => git.Create("-c-malicious"), "Reject option-like repository name");
    await ExpectGitError(() => git.Create("example"), "Reject duplicate repository");
    await ExpectGitError(() => git.Tree("example", "--help"), "Reject object option injection");
    var working = Path.Combine(temporary, "working");
    async Task Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await stdout;
        if (process.ExitCode != 0) throw new Exception(await stderr);
    }
    await Git("clone", Path.Combine(repositories, "example.git"), working);
    await Git("-C", working, "config", "user.name", "Test Author");
    await Git("-C", working, "config", "user.email", "test@example.invalid");
    await File.WriteAllTextAsync(Path.Combine(working, "README.md"), "<script>alert('xss')</script>\n# Test repository");
    await File.WriteAllBytesAsync(Path.Combine(working, "binary.bin"), [0, 1, 2, 3]);
    await File.WriteAllTextAsync(Path.Combine(working, "large.txt"), new string('a', 300_000));
    Directory.CreateDirectory(Path.Combine(working, "folder"));
    await File.WriteAllTextAsync(Path.Combine(working, "folder", "sample.cs"), "class Example { }");
    await Git("-C", working, "add", ".");
    await Git("-C", working, "commit", "-m", "First test commit");
    await Git("-C", working, "push", "origin", "HEAD:main");
    var branch = (await git.Branches("example")).Single();
    var tree = await git.Tree("example", branch.Commit);
    Check(tree.Count == 4, "Read pushed Git tree");
    Check((await git.History("example", branch.Commit)).Single().Subject == "First test commit", "Read commit history");
    Check((await git.Blob("example", tree.Single(e => e.Name == "README.md").Object)).Contains("<script>"), "Read text blob without rendering it");
    await ExpectGitError(() => git.Blob("example", tree.Single(e => e.Name == "binary.bin").Object), "Reject binary preview");
    await ExpectGitError(() => git.Blob("example", tree.Single(e => e.Name == "large.txt").Object), "Reject oversized preview");
    var initial = await git.Details("example", branch.Commit);
    Check(initial.Parents.Count == 0 && initial.Commit.CommittedDate.Length > 0, "Initial commit metadata and committer timestamp");
    var initialChanges = await git.Changes("example", null, branch.Commit);
    Check(initialChanges.Warning is null && initialChanges.TotalFiles == 4 && initialChanges.Files.All(f => f.Status == "A"), "Initial commit diff compares against empty tree");
    Check(initialChanges.Files.Single(f => f.Path == "large.txt").Truncated, "Oversized individual diff is explicitly truncated");
    await Git("-C", working, "tag", "v0.1.0", branch.Commit);
    await Git("-C", working, "tag", "-a", "v0.1.1", "-m", "<svg/onload=alert(1)> version notes", branch.Commit);
    await Git("-C", working, "tag", "main", branch.Commit);
    await File.WriteAllTextAsync(Path.Combine(working, "README.md"), "<script>alert('xss')</script>\n# Updated repository\n<img src=x onerror=alert(1)>\n");
    await File.WriteAllBytesAsync(Path.Combine(working, "binary.bin"), [0, 9, 8, 7, 6]);
    File.Delete(Path.Combine(working, "large.txt"));
    await Git("-C", working, "mv", "folder/sample.cs", "folder/renamed.cs");
    await File.WriteAllTextAsync(Path.Combine(working, "[a] spaced.txt"), "literal path, not a wildcard\n");
    await Git("-C", working, "add", ".");
    await Git("-C", working, "commit", "-m", "Update content and rename file", "-m", "Detailed explanation of the code change.");
    await Git("-C", working, "push", "origin", "HEAD:main");
    var second = (await git.Branches("example")).Single().Commit;
    var changes = await git.Changes("example", branch.Commit, second);
    Check(changes.Warning is null && changes.TotalFiles == 6, "Diff contains all modified, added and removed files");
    var readmeChange = changes.Files.Single(f => f.Path == "README.md");
    Check(readmeChange.Added == 2 && readmeChange.Removed == 1, "Exact added and removed line totals");
    Check(readmeChange.Lines.Any(l => l.Kind == "deletion" && l.OldLine == 2 && l.NewLine is null && l.Text == "-# Test repository"), "Removed line has old line number");
    Check(readmeChange.Lines.Any(l => l.Kind == "addition" && l.NewLine == 2 && l.OldLine is null && l.Text == "+# Updated repository"), "Added line has new line number");
    Check(changes.Files.Single(f => f.Path == "binary.bin").Added is null, "Binary changes are identified without fabricated line counts");
    Check(changes.Files.Single(f => f.Path == "large.txt").Status == "D", "Deleted files are listed");
    Check(changes.Files.Single(f => f.Path == "folder/sample.cs").Status == "D" && changes.Files.Single(f => f.Path == "folder/renamed.cs").Status == "A", "Renames are explicitly represented as delete and add");
    Check(changes.Files.Single(f => f.Path == "[a] spaced.txt").Added == 1, "Spaces and wildcard-like file names remain intact");
    Check((await git.Changes("example", second, second)).TotalFiles == 0, "Identical snapshot comparison is empty");
    Check((await git.Details("example", second)).Message.Contains("Detailed explanation"), "Full multiline commit message");
    await Git("-C", working, "tag", "-a", "v1.0.0", "-m", "Version 1.0", second);
    await Git("-C", working, "checkout", "-b", "feature", branch.Commit);
    await File.WriteAllTextAsync(Path.Combine(working, "feature.txt"), "Feature branch\n");
    await Git("-C", working, "add", ".");
    await Git("-C", working, "commit", "-m", "Add feature");
    await Git("-C", working, "checkout", "main");
    await Git("-C", working, "merge", "--no-ff", "feature", "-m", "Merge feature");
    await Git("-C", working, "push", "origin", "refs/heads/main:refs/heads/main", "refs/heads/feature:refs/heads/feature", "--tags");
    var merged = (await git.Branches("example")).Single(b => b.Name == "main").Commit;
    var mergeDetails = await git.Details("example", merged);
    Check(mergeDetails.Parents.Count == 2 && mergeDetails.Parents[0] == second, "Merge commit exposes all parents in order");
    Check((await git.Changes("example", mergeDetails.Parents[0], merged)).Files.Single().Path == "feature.txt", "Merge first-parent diff");
    Check((await git.Changes("example", mergeDetails.Parents[1], merged)).TotalFiles == 6, "Merge alternate-parent diff");
    var tags = await git.Tags("example");
    Check(tags.Count == 4 && tags.Single(t => t.Name == "v0.1.0").Commit == branch.Commit && !tags.Single(t => t.Name == "v0.1.0").Annotated, "Lightweight version tags");
    Check(tags.Single(t => t.Name == "v1.0.0").Commit == second && tags.Single(t => t.Name == "v1.0.0").Annotated, "Annotated tags peel to their commit");
    Check((await git.Resolve("example", "tag:main")).Commit == branch.Commit && (await git.Resolve("example", "branch:main")).Commit == merged, "Branch/tag name collision resolves explicitly");
    Check((await git.Resolve("example", second)).Commit == second, "Resolve full historical commit ID");
    await ExpectGitError(() => git.Resolve("example", "main~1"), "Reject revision expressions");
    await ExpectGitError(() => git.Changes("example", "--output=outside", second), "Reject diff option injection");
    Check((await git.PagedHistory("example", merged, "README.md", 1)).Commits.Count == 2, "Per-file history excludes unrelated commits");
    Check((await git.PagedHistory("example", merged, "[a] spaced.txt", 1)).Commits.Count == 1, "History pathspec treats wildcard syntax literally");
    await ExpectGitError(() => git.PagedHistory("example", merged, "../outside", 1), "History rejects path traversal");
    for (var i = 1; i <= 21; i++) await Git("-C", working, "commit", "--allow-empty", "-m", "History page " + i);
    await Git("-C", working, "push", "origin", "refs/heads/main:refs/heads/main");
    var latest = (await git.Branches("example")).Single(b => b.Name == "main").Commit;
    var firstHistoryPage = await git.PagedHistory("example", latest, null, 1);
    var secondHistoryPage = await git.PagedHistory("example", latest, null, 2);
    Check(firstHistoryPage.Commits.Count == 20 && firstHistoryPage.HasMore && secondHistoryPage.Commits.Count == 5 && !secondHistoryPage.HasMore, "Commit history pagination includes older commits");
    Check(!firstHistoryPage.Commits.Select(c => c.Id).Intersect(secondHistoryPage.Commits.Select(c => c.Id)).Any(), "Pinned history pages do not duplicate commits");
    await git.Create("many-files");
    var manyWorking = Path.Combine(temporary, "many-working");
    await Git("clone", Path.Combine(repositories, "many-files.git"), manyWorking);
    for (var i = 0; i < 201; i++) await File.WriteAllTextAsync(Path.Combine(manyWorking, $"file-{i:D3}.txt"), "bounded preview\n");
    await Git("-C", manyWorking, "add", ".");
    await Git("-C", manyWorking, "-c", "user.name=Test Author", "-c", "user.email=test@example.invalid", "commit", "-m", "Many files");
    await Git("-C", manyWorking, "push", "origin", "HEAD:main");
    var manyCommit = (await git.Branches("many-files")).Single().Commit;
    var manyChanges = await git.Changes("many-files", null, manyCommit);
    Check(manyChanges.TotalFiles == 201 && manyChanges.Files.Count == 200 && manyChanges.Warning is not null, "Large change set is explicitly capped at 200 files");
    var noNewline = DiffParser.Parse("a/file b/file\n@@ -1 +1 @@\n-old\n\\ No newline at end of file\n+new\n\\ No newline at end of file\n");
    Check(noNewline.Count(l => l.Kind == "metadata") == 2 && noNewline.Single(l => l.Kind == "addition").NewLine == 1, "No-newline markers do not corrupt diff line numbers");
    // Access store and SSH gate logic (the end-to-end sshd test is scripts/test-ssh-gate.sh).
    var accessFile = Path.Combine(temporary, "acl", "access.json");
    var acl = new AccessStore(accessFile);
    Check(acl.Check("alice", "example") == Permission.None, "No grant means no access");
    await acl.Grant("example", "alice", Permission.Write); await acl.Grant("example", "bob", Permission.Read);
    Check(acl.Check("alice", "example") == Permission.Write && acl.Check("bob", "example") == Permission.Read && acl.Check("bob", "many-files") == Permission.None, "Per-user per-repository grants");
    await acl.EnsureAdmins(["root-admin"]);
    Check(acl.Check("root-admin", "anything") == Permission.Write, "Admins have implicit write access");
    await acl.RemoveUser("alice");
    Check(acl.Check("alice", "example") == Permission.None && acl.Check("bob", "example") == Permission.Read, "Removing a user revokes only their grants");
    await acl.Grant("example", "bob", Permission.None);
    Check(acl.Check("bob", "example") == Permission.None, "Revoking sets access to none");
    await ExpectArgument(() => acl.Grant("../x", "bob", Permission.Read), "Grant rejects invalid repository name");
    await ExpectArgument(() => acl.Grant("example", "Bob;rm", Permission.Read), "Grant rejects invalid user name");
    await File.WriteAllTextAsync(accessFile, "{\"Admins\":[\"../evil\"],\"Repositories\":{\"example\":{\"carol\":\"owner\",\"dave\":\"read\"}}}");
    Check(acl.Check("carol", "example") == Permission.None && acl.Check("dave", "example") == Permission.Read && acl.Load().Admins.Count == 0, "Malformed access entries are ignored, valid ones kept");
    if (!OperatingSystem.IsWindows())
    {
        await acl.Grant("example", "dave", Permission.Write);
        Check((File.GetUnixFileMode(accessFile) & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.GroupWrite)) == 0, "Access file is not world-readable or group-writable");
    }
    var gateRoot = "/srv/privategit/repositories";
    Check(GitServe.ParseCommand("git-upload-pack '/srv/privategit/repositories/example.git'", gateRoot) == new GitServe.Request("git-upload-pack", "example", false), "Gate parses absolute clone path");
    Check(GitServe.ParseCommand("git-receive-pack 'example.git'", gateRoot) == new GitServe.Request("git-receive-pack", "example", true), "Gate parses short push path");
    Check(GitServe.ParseCommand("git upload-pack '/example'", gateRoot)?.Repository == "example" && GitServe.ParseCommand("git-upload-pack '~/example.git/'", gateRoot)?.Repository == "example", "Gate accepts equivalent path forms");
    foreach (var bad in new[] { "bash", "git-upload-pack '../etc.git'", "git-upload-pack '/srv/privategit/repositories/../x.git'", "git-upload-pack 'a/b.git'",
        "git-upload-pack 'example.git'; rm -rf /", "git-upload-pack 'example.git' extra", "git-upload-archive 'example.git'", "git-upload-pack '/etc/passwd'",
        "git-upload-pack '-c x.git'", "git-upload-pack 'Example.git'", "scp -t /tmp", "git-upload-pack \"example.git\"" })
        Check(GitServe.ParseCommand(bad, gateRoot) is null, "Gate rejects command: " + bad);
    Check(GitServe.NasSelected("*", "proj") && GitServe.NasSelected("fpga-rtl, proj", "proj") && !GitServe.NasSelected("fpga-rtl", "proj") && !GitServe.NasSelected("", "proj"), "NAS copy selection list");
    var gateConfig = Path.Combine(temporary, "gate.env");
    await File.WriteAllLinesAsync(gateConfig, ["PrivateGit__MainBackup=false", "PrivateGit__NasBackupDirectory=/mnt/nas", "PrivateGit__NasBackupRepositories=a,b", "PrivateGit__AllowBranchDelete=false"]);
    var gateSettings = GitServe.LoadSettings(["git-serve", "--config", gateConfig]);
    Check(!gateSettings.MainBackup && gateSettings.NasBackupDirectory == "/mnt/nas" && gateSettings.NasBackupRepositories == "a,b" && !gateSettings.AllowBranchDelete && gateSettings.BackupDirectory == "/srv/privategit/backups", "Gate reads backup and deletion settings");
    var defaults = GitServe.LoadSettings(["git-serve"]);
    Check(defaults.MainBackup && defaults.AllowBranchDelete && defaults.NasBackupDirectory == "", "Backup on, NAS off by default");
    var keyPath = Path.Combine(temporary, "authorized_keys");
    var authPath = Path.Combine(temporary, "ssh-auth");
    await File.WriteAllLinesAsync(keyPath, [
        "# managed by add-client.sh",
        "from=\"172.20.1.0/24\",restrict ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAliceKey privategit:alice alice@126",
        "from=\"172.20.1.0/24\",restrict ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIBobKey privategit:bob",
        "from=\"172.20.1.0/24\",restrict ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOldKey legacy-client",
        "restrict ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDupKey privategit:alice",
        "restrict ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDupKey privategit:bob",
        "restrict ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAITwoKey privategit:alice privategit:bob"]);
    async Task<string?> Who(string authLine) { await File.WriteAllTextAsync(authPath, authLine + "\n"); return GitServe.IdentifyUser(authPath, keyPath); }
    Check(await Who("publickey ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAliceKey") == "alice" && await Who("publickey ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIBobKey") == "bob", "Gate maps the authenticating key to its user");
    Check(await Who("publickey ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOldKey") is null, "Gate refuses keys not linked to a user");
    Check(await Who("publickey ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDupKey") is null && await Who("publickey ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAITwoKey") is null, "Gate refuses ambiguous key ownership");
    Check(await Who("publickey ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAlice") is null && await Who("publickey ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIUnknown") is null, "Gate refuses unknown or prefix-matching keys");
    Check(await Who("publickey ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAliceKey\npublickey ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIBobKey") is null && GitServe.IdentifyUser(null, keyPath) is null, "Gate refuses missing or multi-key authentication info");
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    var start = new ProcessStartInfo(dotnet) { WorkingDirectory = source, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(assembly);
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
    start.Environment["PrivateGit__DataDirectory"] = data;
    start.Environment["PrivateGit__RepositoryDirectory"] = repositories;
    server = Process.Start(start)!;
    var serverLog = server.StandardOutput.ReadToEndAsync(); var serverError = server.StandardError.ReadToEndAsync();
    HttpClient NewClient() => new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(20) };
    using var client = NewClient();
    for (var attempt = 0; ; attempt++)
    {
        try { using var ready = await client.GetAsync("/Login"); if (ready.IsSuccessStatusCode) break; }
        catch (HttpRequestException) { }
        if (attempt > 80 || server.HasExited) throw new Exception("Server did not start: " + (server.HasExited ? await serverError : "timeout"));
        await Task.Delay(150);
    }
    async Task<string> Token(HttpClient browser, string path)
    {
        var html = await browser.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
        if (!match.Success) throw new Exception("Antiforgery token absent");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
    Check((await client.GetAsync("/")).StatusCode == HttpStatusCode.Redirect, "Anonymous dashboard request requires login");
    Check((await client.GetAsync("/repositories/example")).StatusCode == HttpStatusCode.Redirect, "Anonymous repository access denied");
    Check((await client.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode == HttpStatusCode.BadRequest, "Login CSRF rejected");
    var loginToken = await Token(client, "/Login");
    var login = await client.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
    { ["Username"] = "admin", ["Password"] = password, ["Code"] = Totp.Code(adminSecret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30), ["__RequestVerificationToken"] = loginToken }));
    Check(login.StatusCode == HttpStatusCode.Redirect, "Password and TOTP sign-in");
    Check(login.Headers.GetValues("Set-Cookie").Any(v => v.Contains("httponly", StringComparison.OrdinalIgnoreCase) && v.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase)), "HTTP-only SameSite session cookie");
    var home = await client.GetAsync("/"); var homeHtml = await home.Content.ReadAsStringAsync();
    Check(home.IsSuccessStatusCode && homeHtml.Contains("example"), "Authenticated repository dashboard");
    Check(home.Headers.GetValues("Content-Security-Policy").Single().Contains("frame-ancestors 'none'"), "Strict CSP header");
    Check(home.Headers.CacheControl?.NoStore == true, "Sensitive pages are not cached");
    Check((await client.GetAsync("/repositories/example?PathName=..%2Fsecret")).StatusCode == HttpStatusCode.BadRequest, "Reject browser path traversal");
    Check((await client.GetAsync("/repositories/example?Branch=--all")).StatusCode == HttpStatusCode.NotFound, "Reject injected branch selector");
    var fileHtml = await client.GetStringAsync("/repositories/example?PathName=README.md");
    Check(fileHtml.Contains("&lt;script&gt;") && !fileHtml.Contains("<script>"), "Repository HTML is escaped against XSS");
    Check((await client.GetStringAsync("/repositories/example?PathName=folder%2Frenamed.cs")).Contains("class Example"), "Browse nested file");
    Check((await client.GetAsync($"/repositories/example?Revision={second}&PageNumber=0")).StatusCode == HttpStatusCode.BadRequest, "Reject invalid history pagination");
    var historicHtml = await client.GetStringAsync($"/repositories/example?Revision={branch.Commit}&PathName=README.md");
    Check(historicHtml.Contains("# Test repository") && !historicHtml.Contains("# Updated repository"), "Browse file at historical commit");
    Check((await client.GetStringAsync("/repositories/example?Revision=tag%3Av0.1.0&PathName=README.md")).Contains("# Test repository"), "Browse file at version tag");
    Check((await client.GetStringAsync($"/repositories/example?Revision={latest}&PageNumber=2")).Contains("First test commit"), "Older commit history page is reachable through HTTP");
    var fileHistoryHtml = await client.GetStringAsync("/repositories/example?PathName=README.md");
    Check(fileHistoryHtml.Contains("History for README.md") && fileHistoryHtml.Contains("Update content and rename file") && !fileHistoryHtml.Contains("History page 21"), "File page shows exact-path history and dates");
    var versionsHtml = await client.GetStringAsync("/repositories/example/versions");
    Check(versionsHtml.Contains("v1.0.0") && versionsHtml.Contains("feature") && versionsHtml.Contains("Annotated"), "Versions UI lists branch and tag links");
    Check(versionsHtml.Contains("&lt;svg/onload") && !versionsHtml.Contains("<svg/onload"), "Tag messages are escaped");
    var commitHtml = await client.GetStringAsync($"/repositories/example/commits/{second}");
    Check(commitHtml.Contains("class=\"addition\"") && commitHtml.Contains("class=\"deletion\""), "Commit UI renders green/red diff rows");
    Check(commitHtml.Contains("Detailed explanation") && commitHtml.Contains("Committed by") && commitHtml.Contains("Before</a>") && commitHtml.Contains("After</a>"), "Commit UI includes full metadata and before/after links");
    Check(commitHtml.Contains("&lt;img src=x onerror=alert(1)&gt;") && !commitHtml.Contains("<img src=x"), "Diff code is escaped against XSS");
    Check((await client.GetStringAsync($"/repositories/example/commits/{branch.Commit}")).Contains("Initial commit"), "Root commit diff page");
    Check((await client.GetStringAsync($"/repositories/example/commits/{merged}?Parent={mergeDetails.Parents[1]}")).Contains("Merge commit"), "Merge UI permits selected actual parent");
    Check((await client.GetAsync($"/repositories/example/commits/{second}?Parent={latest}")).StatusCode == HttpStatusCode.BadRequest, "Commit UI rejects unrelated parent override");
    var comparisonHtml = await client.GetStringAsync("/repositories/example/compare?Before=tag%3Av0.1.0&After=tag%3Av1.0.0");
    Check(comparisonHtml.Contains("Updated repository") && comparisonHtml.Contains("6 changed files"), "Compare UI shows changes between version tags");
    Check((await client.GetStringAsync($"/repositories/example/compare?Before={second}&After={second}")).Contains("No file changes"), "Compare UI handles equal snapshots");
    var manyHtml = await client.GetStringAsync($"/repositories/many-files/commits/{manyCommit}");
    Check(manyHtml.Contains("201 changed files") && manyHtml.Contains("first 200 file names") && !manyHtml.Contains("class=\"added-total\""), "Large change UI warns instead of inventing line totals");
    Check((await client.GetAsync("/repositories/example/compare?Before=--output%3Doutside&After=branch%3Amain")).StatusCode == HttpStatusCode.BadRequest, "Compare rejects injected revision options");
    using (var anonymous = NewClient())
    {
        Check((await anonymous.GetAsync($"/repositories/example/commits/{second}")).StatusCode == HttpStatusCode.Redirect, "Commit diffs require authentication");
        Check((await anonymous.GetAsync("/repositories/example/versions")).StatusCode == HttpStatusCode.Redirect, "Version listing requires authentication");
        Check((await anonymous.GetAsync("/repositories/example/compare")).StatusCode == HttpStatusCode.Redirect, "Comparison requires authentication");
    }
    Check((await client.PostAsync("/", new FormUrlEncodedContent(new Dictionary<string, string> { ["Name"] = "missingcsrf" }))).StatusCode == HttpStatusCode.BadRequest, "Repository creation CSRF rejected");
    var create = await client.PostAsync("/", new FormUrlEncodedContent(new Dictionary<string, string> { ["Name"] = "created", ["__RequestVerificationToken"] = await Token(client, "/") }));
    Check(create.StatusCode == HttpStatusCode.Redirect && Directory.Exists(Path.Combine(repositories, "created.git")), "Admin creates bare remote from dashboard");
    Check(create.Headers.Location!.ToString().Contains("/repositories/created/access"), "New repository leads the admin to its permissions");
    Check((await client.GetStringAsync("/repositories/created/access?created=true")).Contains("Only administrators can see it"), "New repository starts admin-only");
    var invalid = await client.PostAsync("/", new FormUrlEncodedContent(new Dictionary<string, string> { ["Name"] = "../escape", ["__RequestVerificationToken"] = await Token(client, "/") }));
    Check(invalid.IsSuccessStatusCode && !Directory.Exists(Path.Combine(temporary, "escape.git")), "Form rejects traversal without touching filesystem");
    using var reader = NewClient();
    var readerLogin = await reader.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
    { ["Username"] = "reader", ["Password"] = password, ["Code"] = Totp.Code(readerSecret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30), ["__RequestVerificationToken"] = await Token(reader, "/Login") }));
    Check(readerLogin.StatusCode == HttpStatusCode.Redirect, "Reader can sign in");
    // Per-repository permissions: a user without a grant sees nothing.
    var ungrantedHome = await reader.GetStringAsync("/");
    Check(!ungrantedHome.Contains("/repositories/example") && ungrantedHome.Contains("No repositories shared with you"), "Ungranted user sees no repositories");
    Check((await reader.GetAsync("/repositories/example")).StatusCode == HttpStatusCode.NotFound, "Ungranted user gets 404 for a repository");
    Check((await reader.GetAsync($"/repositories/example/commits/{second}")).StatusCode == HttpStatusCode.NotFound, "Ungranted user cannot read commit diffs");
    Check((await reader.GetAsync("/repositories/example/versions")).StatusCode == HttpStatusCode.NotFound && (await reader.GetAsync("/repositories/example/compare")).StatusCode == HttpStatusCode.NotFound, "Ungranted user cannot list versions or compare");
    Check((await reader.GetAsync("/repositories/example/access")).StatusCode == HttpStatusCode.NotFound, "Ungranted user cannot discover the access page");
    // Administrator grants read access through the dashboard (CSRF-protected form).
    Check((await client.PostAsync("/repositories/example/access", new FormUrlEncodedContent(new Dictionary<string, string> { ["Grants[reader]"] = "read" }))).StatusCode == HttpStatusCode.BadRequest, "Access form CSRF rejected");
    var grant = await client.PostAsync("/repositories/example/access", new FormUrlEncodedContent(new Dictionary<string, string> { ["Grants[reader]"] = "read", ["__RequestVerificationToken"] = await Token(client, "/repositories/example/access") }));
    Check(grant.IsSuccessStatusCode && (await grant.Content.ReadAsStringAsync()).Contains("Permissions saved"), "Admin grants read access from the dashboard");
    Check(new AccessStore(Path.Combine(data, "access.json")).Check("reader", "example") == Permission.Read, "Dashboard grant is stored for the SSH gate");
    Check((await client.PostAsync("/repositories/example/access", new FormUrlEncodedContent(new Dictionary<string, string> { ["Grants[admin]"] = "none", ["__RequestVerificationToken"] = await Token(client, "/repositories/example/access") }))).Content.ReadAsStringAsync().Result.Contains("Unknown or administrator account"), "Access form rejects administrator or unknown targets");
    var grantedHome = await reader.GetStringAsync("/");
    Check(grantedHome.Contains("/repositories/example") && grantedHome.Contains("Read only") && !grantedHome.Contains("/repositories/many-files"), "Granted user sees only granted repositories");
    Check((await reader.GetAsync($"/repositories/example/commits/{second}")).IsSuccessStatusCode, "Read grant allows commit diffs");
    Check((await reader.GetStringAsync("/repositories/example")).Contains("Read only"), "Repository page shows the user's permission");
    Check((await reader.GetAsync("/repositories/many-files")).StatusCode == HttpStatusCode.NotFound, "Grant on one repository does not leak another");
    var readerAccess = await reader.GetAsync("/repositories/example/access");
    Check(readerAccess.StatusCode == HttpStatusCode.Redirect && readerAccess.Headers.Location!.ToString().Contains("Denied"), "Non-admin cannot open permission management");
    var readerGrant = await reader.PostAsync("/repositories/example/access", new FormUrlEncodedContent(new Dictionary<string, string> { ["Grants[reader]"] = "write", ["__RequestVerificationToken"] = await Token(reader, "/") }));
    Check(readerGrant.StatusCode == HttpStatusCode.Redirect && new AccessStore(Path.Combine(data, "access.json")).Check("reader", "example") == Permission.Read, "Non-admin cannot raise their own permission");
    var readerHome = await reader.GetStringAsync("/");
    Check(!readerHome.Contains("Create a repository"), "Reader creation controls hidden");
    // Branch deletion from the dashboard: write users only, never the main branch.
    await Git("-C", working, "push", "origin", "refs/heads/main:refs/heads/topic-a", "refs/heads/main:refs/heads/topic-b", "refs/heads/main:refs/heads/legacy");
    // Owners as recorded by the SSH post-receive hook; "legacy" has none (made before tracking).
    await Git("--git-dir", Path.Combine(repositories, "example.git"), "config", "branch.topic-a.privategitowner", "reader");
    await Git("--git-dir", Path.Combine(repositories, "example.git"), "config", "branch.topic-b.privategitowner", "admin");
    var topicA = (await git.Branches("example")).Single(b => b.Name == "topic-a").Commit;
    var readerVersions = await reader.GetStringAsync("/repositories/example/versions");
    Check(readerVersions.Contains("topic-a") && !readerVersions.Contains("Delete branch"), "Read users see branches but no delete control");
    var readerDelete = await reader.PostAsync("/repositories/example/versions?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string> { ["DeleteBranch"] = "topic-a", ["DeleteCommit"] = topicA, ["__RequestVerificationToken"] = await Token(reader, "/") }));
    Check(readerDelete.StatusCode == HttpStatusCode.Redirect && (await git.Branches("example")).Any(b => b.Name == "topic-a"), "Read user cannot delete a branch");
    await new AccessStore(Path.Combine(data, "access.json")).Grant("example", "reader", Permission.Write);
    var writerVersions = await reader.GetStringAsync("/repositories/example/versions");
    Check(writerVersions.Contains("Delete branch topic-a") && !writerVersions.Contains("Delete branch main") && writerVersions.Contains("Protected"), "Write user sees delete for own branch, main shown as protected");
    Check(writerVersions.Contains("created by admin") && !writerVersions.Contains("Delete branch topic-b") && !writerVersions.Contains("Delete branch legacy"), "No delete control for other people's or unowned branches");
    var topicB = (await git.Branches("example")).Single(b => b.Name == "topic-b").Commit;
    var othersBranch = await reader.PostAsync("/repositories/example/versions?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string> { ["DeleteBranch"] = "topic-b", ["DeleteCommit"] = topicB, ["__RequestVerificationToken"] = await Token(reader, "/repositories/example/versions") }));
    Check(othersBranch.StatusCode == HttpStatusCode.BadRequest && (await othersBranch.Content.ReadAsStringAsync()).Contains("created by admin") && (await git.Branches("example")).Any(b => b.Name == "topic-b"), "Write user cannot delete someone else's branch");
    var legacyCommit = (await git.Branches("example")).Single(b => b.Name == "legacy").Commit;
    var legacyDelete = await reader.PostAsync("/repositories/example/versions?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string> { ["DeleteBranch"] = "legacy", ["DeleteCommit"] = legacyCommit, ["__RequestVerificationToken"] = await Token(reader, "/repositories/example/versions") }));
    Check(legacyDelete.StatusCode == HttpStatusCode.BadRequest && (await git.Branches("example")).Any(b => b.Name == "legacy"), "Branches without a recorded creator: users cannot delete them");
    Check((await reader.PostAsync("/repositories/example/versions?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string> { ["DeleteBranch"] = "topic-a", ["DeleteCommit"] = topicA }))).StatusCode == HttpStatusCode.BadRequest, "Branch deletion requires CSRF token");
    var stale = await reader.PostAsync("/repositories/example/versions?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string> { ["DeleteBranch"] = "topic-a", ["DeleteCommit"] = second, ["__RequestVerificationToken"] = await Token(reader, "/repositories/example/versions") }));
    Check(stale.StatusCode == HttpStatusCode.BadRequest && (await stale.Content.ReadAsStringAsync()).Contains("changed since you opened") && (await git.Branches("example")).Any(b => b.Name == "topic-a"), "Deletion refused if the branch moved since the page was opened");
    var mainCommit = (await git.Branches("example")).Single(b => b.Name == "main").Commit;
    var deleteMain = await reader.PostAsync("/repositories/example/versions?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string> { ["DeleteBranch"] = "main", ["DeleteCommit"] = mainCommit, ["__RequestVerificationToken"] = await Token(reader, "/repositories/example/versions") }));
    Check(deleteMain.StatusCode == HttpStatusCode.BadRequest && (await deleteMain.Content.ReadAsStringAsync()).Contains("cannot be deleted") && (await git.Branches("example")).Any(b => b.Name == "main"), "Main branch cannot be deleted");
    var deleteOk = await reader.PostAsync("/repositories/example/versions?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string> { ["DeleteBranch"] = "topic-a", ["DeleteCommit"] = topicA, ["__RequestVerificationToken"] = await Token(reader, "/repositories/example/versions") }));
    Check(deleteOk.IsSuccessStatusCode && (await deleteOk.Content.ReadAsStringAsync()).Contains("Branch topic-a deleted") && !(await git.Branches("example")).Any(b => b.Name == "topic-a") && (await git.Branches("example")).Any(b => b.Name == "topic-b"), "Creator deletes exactly their own branch");
    Check(!(await git.BranchOwners("example")).ContainsKey("topic-a") && (await git.BranchOwners("example"))["topic-b"] == "admin", "Owner record removed with the branch, others kept");
    var adminDelete = await client.PostAsync("/repositories/example/versions?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string> { ["DeleteBranch"] = "legacy", ["DeleteCommit"] = legacyCommit, ["__RequestVerificationToken"] = await Token(client, "/repositories/example/versions") }));
    Check(adminDelete.IsSuccessStatusCode && !(await git.Branches("example")).Any(b => b.Name == "legacy"), "Administrator can delete any branch, including unowned ones");
    var injected = await reader.PostAsync("/repositories/example/versions?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string> { ["DeleteBranch"] = "../../HEAD", ["DeleteCommit"] = mainCommit, ["__RequestVerificationToken"] = await Token(reader, "/repositories/example/versions") }));
    Check(injected.StatusCode == HttpStatusCode.BadRequest && File.Exists(Path.Combine(repositories, "example.git", "HEAD")), "Only existing branch names can be deleted (no ref path injection)");
    await new AccessStore(Path.Combine(data, "access.json")).Grant("example", "reader", Permission.Read);
    // Logout form provides a valid token on every authenticated page.
    var forbidden = await reader.PostAsync("/", new FormUrlEncodedContent(new Dictionary<string, string> { ["Name"] = "forbidden", ["__RequestVerificationToken"] = await Token(reader, "/") }));
    Check(forbidden.StatusCode == HttpStatusCode.Redirect && forbidden.Headers.Location!.ToString().Contains("Denied") && !Directory.Exists(Path.Combine(repositories, "forbidden.git")), "Server enforces reader authorization");
    Check((await client.PostAsync("/Logout", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode == HttpStatusCode.BadRequest, "Logout CSRF rejected");
    await client.PostAsync("/Logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = await Token(client, "/") }));
    Check((await client.GetAsync("/")).StatusCode == HttpStatusCode.Redirect, "Logout clears authentication");
    server.Kill(true); await server.WaitForExitAsync();
    await serverLog; await serverError;
    using (var accounts = new AccountStore(data)) await accounts.Delete("reader");
    server.Dispose();
    server = Process.Start(start)!;
    serverLog = server.StandardOutput.ReadToEndAsync(); serverError = server.StandardError.ReadToEndAsync();
    for (var attempt = 0; ; attempt++)
    {
        try { using var ready = await client.GetAsync("/Login"); if (ready.IsSuccessStatusCode) break; } catch (HttpRequestException) { }
        if (attempt > 80 || server.HasExited) throw new Exception("Restart failed.");
        await Task.Delay(150);
    }
    Check((await reader.GetAsync("/")).StatusCode == HttpStatusCode.Redirect, "Deleting account revokes an existing cookie after restart");
    server.Kill(true); await server.WaitForExitAsync(); await serverLog; await serverError; server.Dispose();

    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
    start.Environment["AllowedHosts"] = "git.internal";
    start.Environment["PrivateGit__CloneBaseUrl"] = "git@git.internal:/srv/privategit/repositories";
    start.Environment["PrivateGit__BehindProxy"] = "true";
    server = Process.Start(start)!;
    serverLog = server.StandardOutput.ReadToEndAsync(); serverError = server.StandardError.ReadToEndAsync();
    using var production = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri("http://127.0.0.1:5080") };
    production.DefaultRequestHeaders.Host = "git.internal";
    for (var attempt = 0; ; attempt++)
    {
        try { using var ready = await production.GetAsync("/Login"); if (ready.StatusCode == HttpStatusCode.BadRequest) break; } catch (HttpRequestException) { }
        if (attempt > 80 || server.HasExited) throw new Exception("Production start failed.");
        await Task.Delay(150);
    }
    Check((await production.GetAsync("/Login")).StatusCode == HttpStatusCode.BadRequest, "Production refuses plaintext requests");
    production.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
    var secureLogin = await production.GetAsync("/Login");
    Check(secureLogin.IsSuccessStatusCode, "Production accepts HTTPS scheme from trusted loopback proxy");
    Check(secureLogin.Headers.Contains("Strict-Transport-Security"), "Production HSTS works behind reverse proxy");
    Check(secureLogin.Headers.GetValues("Set-Cookie").Any(v => v.Contains("secure", StringComparison.OrdinalIgnoreCase)), "Production antiforgery cookie requires HTTPS");
    production.DefaultRequestHeaders.Host = "attacker.invalid";
    Check((await production.GetAsync("/Login")).StatusCode == HttpStatusCode.BadRequest, "Production rejects unapproved host header");
    server.Kill(true); await server.WaitForExitAsync(); await serverLog; await serverError;
    Console.WriteLine($"All {passed} checks passed.");
}
finally
{
    if (server is not null) { if (!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); } server.Dispose(); }
    // Clean only the generated test directory; Git may mark object files read-only.
    foreach (var file in Directory.EnumerateFiles(temporary, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(temporary, true);
}
