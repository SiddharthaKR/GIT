using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using PrivateGit.Services;
using System.Threading.RateLimiting;

// SSH gate: must run before anything touches the dashboard's private data directory,
// because sshd starts it as the unprivileged git account.
if (args.Length > 0 && args[0] == "git-serve")
{
    Environment.ExitCode = await GitServe.Run(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment()) builder.WebHost.UseUrls("http://127.0.0.1:5080");
var config = builder.Configuration.GetSection("PrivateGit");
var data = Path.GetFullPath(config["DataDirectory"] ?? "App_Data");
Directory.CreateDirectory(data);
var accessFile = Path.GetFullPath(config["AccessFile"] ?? Path.Combine(data, "access.json"));
var access = new AccessStore(accessFile);
// Permission and listing commands do not open the (single-process) account store,
// so they work while the dashboard service is running.
if (args.Length > 0 && (AccessCommands.Names.Contains(args[0]) || args[0] == "user-list"))
{
    var repositoryDirectory = Path.GetFullPath(config["RepositoryDirectory"] ?? "App_Data/repositories");
    if (args[0] == "user-list")
    {
        foreach (var summary in AccountStore.ReadSummaries(data)) Console.WriteLine($"{summary.Name,-32} {summary.Role}");
        return;
    }
    try { Environment.ExitCode = await AccessCommands.Run(data, access, repositoryDirectory, args); }
    catch (ArgumentException exception) { Console.Error.WriteLine(exception.Message); Environment.ExitCode = 2; }
    return;
}
builder.Services.AddSingleton(new AccountStore(data, config.GetValue("RequireAuthenticator", false)));
builder.Services.AddSingleton(access);
builder.Services.AddSingleton<GitStore>();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys")))
    .SetApplicationName("PrivateGit");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/Login";
    options.AccessDeniedPath = "/Denied";
    options.Cookie.Name = "PrivateGit.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(4);
    options.SlidingExpiration = false;
    options.Events.OnValidatePrincipal = async context =>
    {
        var accounts = context.HttpContext.RequestServices.GetRequiredService<AccountStore>();
        var account = await accounts.Find(context.Principal?.Identity?.Name ?? "");
        if (account is null || account.Stamp != context.Principal?.FindFirst("stamp")?.Value)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync();
        }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRazorPages(options => options.Conventions.AuthorizeFolder("/")
    .AllowAnonymousToPage("/Login").AllowAnonymousToPage("/Error"))
    .AddMvcOptions(options => options.Filters.Add<RepositoryAccessFilter>());
builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
    ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Defaults trust only loopback. Never clear KnownProxies/KnownIPNetworks.
    options.ForwardLimit = 1;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
        RateLimitPartition.GetConcurrencyLimiter("git-work", _ => new ConcurrencyLimiterOptions
        { PermitLimit = 16, QueueLimit = 0 }));
});
var app = builder.Build();
// File-backed accounts intentionally support exactly one application/CLI process.
var accounts = app.Services.GetRequiredService<AccountStore>();

// Commands are local administrator operations. No public bootstrap/registration endpoint.
if (args.Length > 0 && AccountCommands.Names.Contains(args[0]))
{
    try { await AccountCommands.Run(accounts, access, args); }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException)
    {
        Console.Error.WriteLine("Error: " + error.Message);
        Environment.ExitCode = 1;
    }
    return;
}
// Mirror administrator names for the SSH gate, which cannot read account secrets.
await access.EnsureAdmins((await accounts.List()).Where(a => a.IsAdmin).Select(a => a.Name));
if (config.GetValue<bool>("BehindProxy")) app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    var hosts = builder.Configuration["AllowedHosts"];
    if (string.IsNullOrWhiteSpace(hosts) || hosts.Contains('*') || hosts.Contains("localhost"))
        throw new InvalidOperationException("Set AllowedHosts to the exact internal DNS hostname in production.");
    if (config["CloneBaseUrl"]?.Contains("CHANGE-ME") != false)
        throw new InvalidOperationException("Set PrivateGit__CloneBaseUrl before production startup.");
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.Use(async (context, next) =>
{
    // Also enforce allowed hosts on direct Kestrel requests.
    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    if (!app.Environment.IsDevelopment() && !context.Request.IsHttps)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("HTTPS is required.");
        return;
    }
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.Run();
