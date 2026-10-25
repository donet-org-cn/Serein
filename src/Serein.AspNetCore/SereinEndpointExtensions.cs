using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serein.Hosting;
using System.Reflection;

namespace Serein.AspNetCore;

public static class SereinEndpointExtensions
{
    private static readonly string PackageVersion = typeof(SereinEndpointExtensions).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
    /// <summary>Maps read-only source status endpoints. The returned group supports RequireAuthorization.</summary>
    public static RouteGroupBuilder MapSereinApi(this IEndpointRouteBuilder endpoints, string prefix = "/api")
    {
        var group = endpoints.MapGroup(prefix);
        group.MapGet("/status", () => Results.Ok(new { name = "Serein", version = PackageVersion, runtime = ".NET 10" }));
        group.MapGet("/streams", (SereinMediaService media) => Results.Ok(media.GetSources()));
        return group;
    }
}
