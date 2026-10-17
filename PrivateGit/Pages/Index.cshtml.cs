using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PrivateGit.Services;
namespace PrivateGit.Pages;
public class IndexModel(GitStore git, AccessStore access, ILogger<IndexModel> logger) : PageModel
{
    public IReadOnlyList<Repo> Repositories { get; private set; } = [];
    [BindProperty, Required, RegularExpression("^[a-z][a-z0-9_-]{0,62}$")] public string Name { get; set; } = "";
    [BindProperty(SupportsGet = true), StringLength(63)] public string? Search { get; set; }
    public string? Error { get; set; }
    public void OnGet() => Load();
    public Dictionary<string, Permission> Permissions { get; } = new(StringComparer.Ordinal);
    private void Load()
    {
        var admin = User.IsInRole("Admin");
        var data = access.Load();
        foreach (var repo in git.List())
        {
            var permission = admin ? Permission.Write : AccessStore.Check(data, User.Identity!.Name!, repo.Name);
            if (permission != Permission.None) Permissions[repo.Name] = permission;
        }
        Repositories = git.List().Where(r => Permissions.ContainsKey(r.Name))
            .Where(r => string.IsNullOrWhiteSpace(Search) || r.Name.Contains(Search[..Math.Min(Search.Length, 63)], StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    public async Task<IActionResult> OnPostAsync()
    {
        if (!User.IsInRole("Admin")) return Forbid();
        if (!ModelState.IsValid) { Error = "Use 1-63 lowercase letters, digits, underscores or hyphens; start with a letter."; Load(); return Page(); }
        try
        {
            await git.Create(Name, HttpContext.RequestAborted);
            logger.LogInformation("Repository {Repository} created by {User}", Name, User.Identity!.Name);
            // A fresh repository is visible to admins only until people are granted access.
            return RedirectToPage("/Access", new { name = Name, created = true });
        }
        catch (GitException exception) { Error = exception.Message; Load(); return Page(); }
    }
}
