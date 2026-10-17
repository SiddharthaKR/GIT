namespace PrivateGit.Services;

public static class AccountCommands
{
    public static readonly string[] Names = ["user-add", "user-reset", "user-delete", "user-list"];

    public static async Task Run(AccountStore store, AccessStore access, string[] args)
    {
        if (args[0] == "user-list")
        {
            foreach (var summary in await store.List()) Console.WriteLine($"{summary.Name,-32} {summary.Role}");
            return;
        }
        if (args.Length < 2) throw new ArgumentException("Usage: user-add NAME Admin|User; user-reset NAME Admin|User; user-delete NAME; user-list");
        if (args[0] == "user-delete")
        {
            await store.Delete(args[1]);
            await access.RemoveUser(args[1]);
            Console.WriteLine("Account removed and its repository permissions revoked; active sessions end on their next request.");
            Console.WriteLine("Also remove this person's SSH keys: sudo bash /opt/privategit/add-client.sh --revoke " + args[1]);
            return;
        }
        if (args.Length != 3) throw new ArgumentException("Specify Admin or User.");
        var role = AccountStore.NormalizeRole(args[2]);
        Console.Write("New password (16+ characters): ");
        var password = ReadPassword();
        Console.Write("Repeat password: ");
        if (password != ReadPassword()) throw new ArgumentException("Passwords differ.");
        if (!await store.Put(args[1], password, role, args[0] == "user-reset"))
            throw new InvalidOperationException("Account already exists (add) or does not exist (reset).");
        await access.EnsureAdmins((await store.List()).Where(a => a.IsAdmin).Select(a => a.Name));
        var account = (await store.Find(args[1]))!;
        Console.WriteLine("Enroll in an offline-capable authenticator: SHA1 / 6 digits / 30 seconds.");
        Console.WriteLine($"Secret (sensitive; shown only for enrollment): {account.TotpSecret}");
        Console.WriteLine($"otpauth://totp/PrivateGit:{Uri.EscapeDataString(account.Name)}?secret={account.TotpSecret}&issuer=PrivateGit&digits=6&period=30");
        Console.WriteLine("Reset rotates password + TOTP and revokes existing sessions. Do not save this output in logs.");
        if (role == "User")
            Console.WriteLine($"This user sees no repositories until granted: PrivateGit access-grant REPO {account.Name} read|write (or use the dashboard Access tab).");
    }
    private static string ReadPassword()
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Use an interactive terminal; passwords are not accepted through command arguments or redirected input.");
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return text.ToString(); }
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (!char.IsControl(key.KeyChar) && text.Length < 128) text.Append(key.KeyChar);
        }
    }
}

// Permission administration from the server console. Runs while the dashboard is
// running (it does not open the account store, only reads account names).
public static class AccessCommands
{
    public static readonly string[] Names = ["access-grant", "access-revoke", "access-list"];

    public static async Task<int> Run(string dataDirectory, AccessStore access, string repositoryDirectory, string[] args)
    {
        var accounts = AccountStore.ReadSummaries(dataDirectory);
        switch (args[0])
        {
            case "access-grant" when args.Length == 4:
            case "access-revoke" when args.Length == 3:
            {
                var repository = args[1]; var user = args[2];
                var permission = args[0] == "access-revoke" ? Permission.None : AccessStore.Parse(args[3]);
                if (!Directory.Exists(Path.Combine(repositoryDirectory, repository + ".git")))
                    throw new ArgumentException($"Repository '{repository}' does not exist.");
                var account = accounts.FirstOrDefault(a => a.Name == user) ?? throw new ArgumentException($"Account '{user}' does not exist.");
                if (account.IsAdmin) Console.WriteLine($"Note: {user} is an Admin and already has full access to every repository.");
                await access.Grant(repository, user, permission);
                Console.WriteLine($"{user} → {repository}: {AccessStore.Format(permission)}. Effective immediately for web and SSH.");
                return 0;
            }
            case "access-list" when args.Length <= 2:
            {
                var data = access.Load();
                Console.WriteLine("Admins (full access): " + (data.Admins.Count == 0 ? "-" : string.Join(", ", data.Admins)));
                var repositories = Directory.Exists(repositoryDirectory)
                    ? Directory.EnumerateDirectories(repositoryDirectory, "*.git").Select(p => Path.GetFileName(p)[..^4])
                        .Where(n => AccessStore.RepositoryPattern.IsMatch(n)).Order(StringComparer.Ordinal).ToArray() : [];
                foreach (var repository in repositories.Where(r => args.Length == 1 || r == args[1]))
                {
                    var grants = data.Repositories.TryGetValue(repository, out var g) ? g : new(StringComparer.Ordinal);
                    Console.WriteLine($"{repository}: " + (grants.Count == 0 ? "(admins only)" : string.Join(", ", grants.Select(p => $"{p.Key}={p.Value}"))));
                }
                return 0;
            }
            default:
                Console.Error.WriteLine("Usage:\n  access-grant REPO USER read|write\n  access-revoke REPO USER\n  access-list [REPO]");
                return 2;
        }
    }
}
