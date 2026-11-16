using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Pluperfect.Jwt;

namespace Nexus.Service;

/// <summary>Marks routes that check who calls them on their own, so a page on any origin may call them.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AnyOriginAttribute : Attribute;

public static class InstallGate
{
    public const string NotSetUp = "Nexus is not set up yet. Open /install on its client to set it up.";

    /// <summary>The routes that answer before the instance is set up.</summary>
    public static bool IsOpen(PathString path) => path.StartsWithSegments("/install") || path.StartsWithSegments("/health");

    /// <summary>Answers 503 to every route but the open ones until the instance is set up.</summary>
    public static IApplicationBuilder UseInstallGate(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (IsOpen(http.Request.Path) || await http.RequestServices.GetRequiredService<Installation>().IsInstalledAsync(http.RequestAborted))
        {
            await next(http);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = new ProblemDetails { Status = StatusCodes.Status503ServiceUnavailable, Title = "Not set up", Detail = NotSetUp },
        });
    });
}

/// <summary>
/// CORS for the client's origin alone, read from the instance on each request. Before the instance is set up, and on
/// the installer's routes, any origin's page may call: those routes check the caller themselves.
/// </summary>
public sealed class InstanceCorsPolicy(Installation installation, Instance instance) : ICorsPolicyProvider
{
    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return null;
        }

        var open = InstallGate.IsOpen(context.Request.Path) || !await installation.IsInstalledAsync(context.RequestAborted);
        var allowed = open ? origin : instance.ClientOrigin;
        return allowed is null ? null : new CorsPolicyBuilder().WithOrigins(allowed).AllowAnyHeader().AllowAnyMethod().AllowCredentials().Build();
    }
}

/// <summary>Pluperfect.Jwt's origin check against the client's origin, read from the instance on each request.</summary>
public sealed class ClientOriginFilter(Instance instance) : IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<AnyOriginAttribute>().Any())
        {
            return next();
        }

        if (instance.ClientOrigin is { } origin)
        {
            return new OriginFilter(new AllowedOriginRegistry([origin])).OnActionExecutionAsync(context, next);
        }

        // With no client yet, no page is one the service trusts.
        var request = context.HttpContext.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && (request.Headers.Origin.Count > 0 || request.Headers.Referer.Count > 0))
        {
            context.Result = new ObjectResult(new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = "Request origin is not allowed." })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
            return Task.CompletedTask;
        }

        return next();
    }
}
