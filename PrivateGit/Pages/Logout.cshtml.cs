using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
namespace PrivateGit.Pages;
public class LogoutModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Index");
    public async Task<IActionResult> OnPostAsync() { await HttpContext.SignOutAsync(); return RedirectToPage("/Login"); }
}
