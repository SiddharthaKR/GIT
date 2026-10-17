using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PrivateGit.Services;
namespace PrivateGit.Pages;
public class RepositoryModel(GitStore git) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Name { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? Branch { get; set; }
    [BindProperty(SupportsGet = true)] public string? Revision { get; set; }
    [BindProperty(SupportsGet = true)] public string? PathName { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public string CloneUrl { get; private set; } = "";
    public Permission Access => RepositoryAccessFilter.Of(HttpContext);
    public string Snapshot { get; private set; } = "";
    public IReadOnlyList<Branch> Branches { get; private set; } = [];
    public IReadOnlyList<Tag> Tags { get; private set; } = [];
    public bool IsEmpty { get; private set; }
    public bool HasMore { get; private set; }
    public IReadOnlyList<TreeEntry> Entries { get; private set; } = [];
    public IReadOnlyList<Commit> Commits { get; private set; } = [];
    public string? FileText { get; private set; }
    public string? Error { get; private set; }
    public string Parent => PathName?.Contains('/') == true ? PathName[..PathName.LastIndexOf('/')] : "";
    public string Child(string name) => string.IsNullOrEmpty(PathName) ? name : PathName + "/" + name;
    public async Task<IActionResult> OnGetAsync()
    {
        try
        {
            CloneUrl = git.CloneUrl(Name);
            if (!GitStore.ValidPath(PathName) || PageNumber is < 1 or > 1000) return BadRequest();
            Branches = await git.Branches(Name, HttpContext.RequestAborted);
            Tags = await git.Tags(Name, HttpContext.RequestAborted);
            if (Branches.Count == 0 && Tags.Count == 0 && Revision is null && Branch is null) { IsEmpty = true; return Page(); }
            var requested = Revision ?? (Branch is not null ? "branch:" + Branch : Branches.Count == 0 && Tags.Count > 0 ? "tag:" + Tags[0].Name : null);
            PrivateGit.Services.Revision selected;
            try { selected = await git.Resolve(Name, requested, HttpContext.RequestAborted); }
            catch (GitException) { return NotFound(); }
            Revision = selected.Label;
            Snapshot = selected.Commit;
            var history = await git.PagedHistory(Name, Snapshot, PathName, PageNumber, HttpContext.RequestAborted);
            Commits = history.Commits; HasMore = history.HasMore;
            var segments = string.IsNullOrEmpty(PathName) ? [] : PathName.Split('/');
            var tree = selected.Commit;
            for (var segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
            {
                var segment = segments[segmentIndex];
                var entries = await git.Tree(Name, tree, HttpContext.RequestAborted);
                var entry = entries.SingleOrDefault(e => e.Name == segment);
                if (entry is null) return NotFound();
                if (entry.Type == "blob")
                {
                    if (segmentIndex != segments.Length - 1) return NotFound();
                    // Symlink objects are shown as text; never followed on the server.
                    FileText = await git.Blob(Name, entry.Object, HttpContext.RequestAborted);
                    return Page();
                }
                if (entry.Type != "tree") { Error = "Submodule content must be opened in a local clone."; return Page(); }
                tree = entry.Object;
            }
            Entries = await git.Tree(Name, tree, HttpContext.RequestAborted);
            return Page();
        }
        catch (GitException exception) { Error = exception.Message; return Page(); }
    }
}
