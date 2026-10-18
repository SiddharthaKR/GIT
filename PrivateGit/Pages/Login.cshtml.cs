using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using PrivateGit.Services;

namespace PrivateGit.Pages;
[EnableRateLimiting("login")]
public class LoginModel(AccountStore accounts, ILogger<LoginModel> logger) : PageModel
{
    [BindProperty, Required, StringLength(32)] public string Username { get; set; } = "";
    [BindProperty, Required, StringLength(128)] public string Password { get; set; } = "";
    [BindProperty, RegularExpression("^[0-9]{6}$")] public string? Code { get; set; }
    public bool RequireCode => accounts.RequireAuthenticator;
    public string? Error { get; set; }
    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? RedirectToPage("/Index") : Page();
    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid || (RequireCode && string.IsNullOrEmpty(Code)))
        { Error = RequireCode ? "Enter your username, password, and six-digit authenticator code." : "Enter your username and password."; return Page(); }
        var account = await accounts.Authenticate(Username, Password, Code ?? "");
        if (account is null)
        {
            logger.LogWarning("Login denied from {Address}", HttpContext.Connection.RemoteIpAddress);
            Error = RequireCode ? "Sign-in failed. Check your credentials or try later. Codes cannot be reused." : "Sign-in failed. Check your username and password, or try again later.";
            ModelState.Remove(nameof(Password)); ModelState.Remove(nameof(Code)); Password = ""; Code = "";
            return Page();
        }
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, account.Name), new Claim(ClaimTypes.Role, account.Role), new Claim("stamp", account.Stamp) }, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        logger.LogInformation("Login accepted for {User} from {Address}", account.Name, HttpContext.Connection.RemoteIpAddress);
        return RedirectToPage("/Index");
    }
}
