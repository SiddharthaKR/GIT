using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PrivateGit.Services;

// Every page whose route contains {name} is a repository page. Users without at least
// read permission get 404 (not 403) so repository names are not disclosed.
public sealed class RepositoryAccessFilter(AccessStore access) : IAsyncPageFilter
{
    public const string ItemKey = "PrivateGit.Permission";
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;
    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (context.RouteData.Values.TryGetValue("name", out var value) && value is string repository)
        {
            var user = context.HttpContext.User;
            var permission = user.Identity?.IsAuthenticated != true ? Permission.None
                : user.IsInRole("Admin") ? Permission.Write
                : access.Check(user.Identity!.Name!, repository);
            if (permission == Permission.None) { context.Result = new NotFoundResult(); return; }
            context.HttpContext.Items[ItemKey] = permission;
        }
        await next();
    }
    public static Permission Of(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is Permission permission ? permission : Permission.None;
}
