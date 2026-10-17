using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PrivateGit.Services;
namespace PrivateGit.Pages;
public class VersionsModel(GitStore git, IConfiguration configuration, ILogger<VersionsModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";
    [BindProperty] public string DeleteBranch { get; set; } = "";
    [BindProperty] public string DeleteCommit { get; set; } = "";
    public IReadOnlyList<Branch> Branches { get; private set; } = [];
    public IReadOnlyList<Tag> Tags { get; private set; } = [];
    public string MainBranch { get; private set; } = "main";
    public bool CanDelete { get; private set; }
    public IReadOnlyDictionary<string, string> Owners { get; private set; } = new Dictionary<string, string>();
    public bool MayDelete(string branch) => CanDelete && GitStore.MayDelete(branch, MainBranch, Owners.GetValueOrDefault(branch), User.Identity!.Name!, User.IsInRole("Admin"));
    public string? Message { get; private set; }
    public string? Error { get; private set; }
    private bool DeletionAllowed => !string.Equals(configuration["PrivateGit:AllowBranchDelete"], "false", StringComparison.OrdinalIgnoreCase);
    private async Task Load()
    {
        Branches = await git.Branches(Name, HttpContext.RequestAborted); Tags = await git.Tags(Name, HttpContext.RequestAborted);
        MainBranch = await git.DefaultBranch(Name, HttpContext.RequestAborted);
        Owners = await git.BranchOwners(Name, HttpContext.RequestAborted);
        CanDelete = DeletionAllowed && RepositoryAccessFilter.Of(HttpContext) == Permission.Write;
    }
    public async Task<IActionResult> OnGetAsync()
    {
        try { await Load(); return Page(); }
        catch (GitException) { return NotFound(); }
    }
    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!DeletionAllowed || RepositoryAccessFilter.Of(HttpContext) != Permission.Write) return Forbid();
        try
        {
            await git.DeleteBranch(Name, DeleteBranch, DeleteCommit, User.Identity!.Name!, User.IsInRole("Admin"), HttpContext.RequestAborted);
            logger.LogInformation("Branch {Branch} in {Repository} deleted by {User} (was {Commit})", DeleteBranch, Name, User.Identity!.Name, DeleteCommit);
            Message = $"Branch {DeleteBranch} deleted. Its last commit was {DeleteCommit[..8]}.";
        }
        catch (GitException exception) { Error = exception.Message; Response.StatusCode = 400; }
        try { await Load(); } catch (GitException) { return NotFound(); }
        return Page();
    }
}
