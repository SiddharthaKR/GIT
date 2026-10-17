using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;

namespace PrivateGit.Services;

public sealed record Account(string Name, string Hash, string TotpSecret, string Role, string Stamp,
    int Failures = 0, DateTimeOffset? LockedUntil = null, long LastTotpStep = -1);

public sealed record AccountSummary(string Name, string Role)
{
    public bool IsAdmin => Role == "Admin";
}

public sealed class AccountStore : IDisposable
{
    // "User" accounts get access only to repositories an administrator grants them.
    // "Reader" is the role name used by earlier versions and is treated as "User".
    public static string NormalizeRole(string role) => role switch
    {
        "Admin" or "admin" => "Admin",
        "User" or "user" or "Reader" or "reader" => "User",
        _ => throw new ArgumentException("Role must be Admin or User."),
    };
    // Lock-free read used by local administrator commands while the dashboard runs.
    public static IReadOnlyList<AccountSummary> ReadSummaries(string directory)
    {
        var path = Path.Combine(directory, "accounts.json");
        if (!File.Exists(path)) return [];
        var accounts = JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(path)) ?? [];
        return accounts.Select(a => new AccountSummary(a.Name, a.Role == "Admin" ? "Admin" : "User")).OrderBy(a => a.Name, StringComparer.Ordinal).ToArray();
    }
    public async Task<IReadOnlyList<AccountSummary>> List()
    {
        await gate.WaitAsync();
        try { return (await Load()).Select(a => new AccountSummary(a.Name, a.Role == "Admin" ? "Admin" : "User")).OrderBy(a => a.Name, StringComparer.Ordinal).ToArray(); }
        finally { gate.Release(); }
    }
    private readonly string file;
    private readonly FileStream processLock;
    public AccountStore(string directory)
    {
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "accounts.json");
        processLock = new FileStream(Path.Combine(directory, "accounts.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        dummy = hasher.HashPassword("dummy", Guid.NewGuid().ToString());
    }
    public void Dispose() { processLock.Dispose(); gate.Dispose(); }
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly PasswordHasher<string> hasher = new(Microsoft.Extensions.Options.Options.Create(
        new PasswordHasherOptions { IterationCount = 600_000 }));
    // Unknown users still incur a password hash verification.
    private readonly string dummy;

    private async Task<List<Account>> Load() => File.Exists(file)
        ? JsonSerializer.Deserialize<List<Account>>(await File.ReadAllTextAsync(file)) ?? [] : [];
    private async Task Save(List<Account> accounts)
    {
        var temporary = file + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(accounts));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, file, true);
    }
    public async Task<Account?> Find(string name)
    {
        await gate.WaitAsync();
        try { return (await Load()).Find(a => a.Name == name); }
        finally { gate.Release(); }
    }
    public async Task<bool> Put(string name, string password, string role, bool replace)
    {
        if (!Regex.IsMatch(name, "^[a-z][a-z0-9_-]{2,31}$"))
            throw new ArgumentException("Username: 3-32 lowercase letters/digits/_/-, beginning with a letter.");
        role = NormalizeRole(role);
        if (password.Length < 16 || password.Length > 128) throw new ArgumentException("Use a unique password or passphrase of 16-128 characters.");
        await gate.WaitAsync();
        try
        {
            var all = await Load();
            if (all.Any(a => a.Name == name) != replace) return false;
            all.RemoveAll(a => a.Name == name);
            all.Add(new(name, hasher.HashPassword(name, password), Totp.NewSecret(), role, Guid.NewGuid().ToString("N")));
            await Save(all);
            return true;
        }
        finally { gate.Release(); }
    }
    public async Task Delete(string name)
    {
        await gate.WaitAsync();
        try { var all = await Load(); all.RemoveAll(a => a.Name == name); await Save(all); }
        finally { gate.Release(); }
    }
    public async Task<Account?> Authenticate(string name, string password, string code)
    {
        if (name.Length > 32 || password.Length > 128 || code.Length > 6) return null;
        await gate.WaitAsync();
        try
        {
            var all = await Load();
            var index = all.FindIndex(a => a.Name == name);
            if (index < 0) { hasher.VerifyHashedPassword("dummy", dummy, password); return null; }
            var account = all[index];
            if (account.LockedUntil > DateTimeOffset.UtcNow) return null;
            var passwordOk = hasher.VerifyHashedPassword(name, account.Hash, password) != PasswordVerificationResult.Failed;
            var step = Totp.Verify(account.TotpSecret, code, DateTimeOffset.UtcNow, account.LastTotpStep);
            if (!passwordOk || step is null)
            {
                var failures = account.LockedUntil is not null ? 1 : account.Failures + 1;
                all[index] = account with { Failures = failures, LockedUntil = failures >= 5 ? DateTimeOffset.UtcNow.AddMinutes(15) : null };
                await Save(all);
                return null;
            }
            all[index] = account with { Failures = 0, LockedUntil = null, LastTotpStep = step.Value };
            await Save(all);
            return all[index];
        }
        finally { gate.Release(); }
    }
}
