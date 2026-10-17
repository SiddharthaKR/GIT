using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PrivateGit.Services;
namespace PrivateGit.Pages;
public class CompareModel(GitStore git) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? Before { get; set; }
    [BindProperty(SupportsGet = true)] public string? After { get; set; }
    public IReadOnlyList<Branch> Branches { get; private set; } = [];
    public IReadOnlyList<Tag> Tags { get; private set; } = [];
    public ChangeView? Comparison { get; private set; }
    public string? Error { get; private set; }
    public async Task<IActionResult> OnGetAsync()
    {
        try
        {
            Branches = await git.Branches(Name, HttpContext.RequestAborted); Tags = await git.Tags(Name, HttpContext.RequestAborted);
            if (Before is null || After is null) return Page();
            var before = await git.Resolve(Name, Before, HttpContext.RequestAborted);
            var after = await git.Resolve(Name, After, HttpContext.RequestAborted);
            Comparison = new(Name, before.Commit, after.Commit, await git.Changes(Name, before.Commit, after.Commit, HttpContext.RequestAborted));
            return Page();
        }
        catch (GitException exception) { Error = exception.Message; Response.StatusCode = 400; return Page(); }
    }
}
