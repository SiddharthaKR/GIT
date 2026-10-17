using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PrivateGit.Services;
namespace PrivateGit.Pages;
public class AccessModel(GitStore git, AccessStore access, AccountStore accounts, ILogger<AccessModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";
    [BindProperty(SupportsGet = true)] public bool Created { get; set; }
    [BindProperty] public Dictionary<string, string> Grants { get; set; } = new();
    public IReadOnlyList<AccountSummary> People { get; private set; } = [];
    public IReadOnlyList<string> Admins { get; private set; } = [];
    public Dictionary<string, Permission> Current { get; } = new(StringComparer.Ordinal);
    public string CloneUrl { get; private set; } = "";
    public bool Saved { get; private set; }
    public string? Error { get; private set; }

    private async Task<bool> Load()
    {
        try { CloneUrl = git.CloneUrl(Name); } catch (GitException) { return false; }
        if (!git.List().Any(r => r.Name == Name)) return false;
        var all = await accounts.List();
        People = all.Where(a => !a.IsAdmin).ToArray();
        Admins = all.Where(a => a.IsAdmin).Select(a => a.Name).ToArray();
        var data = access.Load();
        foreach (var person in People) Current[person.Name] = AccessStore.Check(data, person.Name, Name);
        return true;
    }
    public async Task<IActionResult> OnGetAsync()
    {
        if (!User.IsInRole("Admin")) return Forbid();
        return await Load() ? Page() : NotFound();
    }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!User.IsInRole("Admin")) return Forbid();
        if (!await Load()) return NotFound();
        Created = false;
        var people = People.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var changes = new List<(string User, Permission Level)>();
        foreach (var (user, value) in Grants)
        {
            Permission level;
            try { level = AccessStore.Parse(value); } catch (ArgumentException) { Error = "Choose No access, Read or Read & write."; return Page(); }
            if (!people.Contains(user)) { Error = "Unknown or administrator account in the request."; return Page(); }
            if (Current.TryGetValue(user, out var existing) && existing != level) changes.Add((user, level));
        }
        foreach (var change in changes)
        {
            await access.Grant(Name, change.User, change.Level);
            logger.LogInformation("Permission {Permission} on {Repository} set for {Target} by {User}", AccessStore.Format(change.Level), Name, change.User, User.Identity!.Name);
        }
        await Load();
        Saved = true;
        return Page();
    }
}
