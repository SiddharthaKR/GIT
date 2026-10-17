using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PrivateGit.Services;
namespace PrivateGit.Pages;
public class CommitModel(GitStore git) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Id { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? Parent { get; set; }
    public CommitDetails? Details { get; private set; }
    public ChangeSet Changes { get; private set; } = new([], 0, null);
    public string? Error { get; private set; }
    public async Task<IActionResult> OnGetAsync()
    {
        try
        {
            var resolved = await git.Resolve(Name, Id, HttpContext.RequestAborted);
            if (resolved.Label != resolved.Commit) return NotFound();
            Details = await git.Details(Name, resolved.Commit, HttpContext.RequestAborted);
            if (Parent is not null && !Details.Parents.Contains(Parent)) return BadRequest();
            Parent ??= Details.Parents.FirstOrDefault();
            Changes = await git.Changes(Name, Parent, resolved.Commit, HttpContext.RequestAborted);
            return Page();
        }
        catch (GitException exception) { Error = exception.Message; Response.StatusCode = 400; return Page(); }
    }
}
