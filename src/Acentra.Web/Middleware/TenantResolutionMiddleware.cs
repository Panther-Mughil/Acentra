namespace Acentra.Web.Middleware;

public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        return next(context);
    }
}
