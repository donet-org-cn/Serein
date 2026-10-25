using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serein.Hosting;

namespace Serein.AspNetCore;

public static class SereinEndpointExtensions
{
    /// <summary>Maps read-only source status endpoints. The returned group supports RequireAuthorization.</summary>
    public static RouteGroupBuilder MapSereinApi(this IEndpointRouteBuilder endpoints, string prefix = "/api")
    {
        var group = endpoints.MapGroup(prefix);
        group.MapGet("/status", () => Results.Ok(new { name = "Serein", version = "0.1.0-preview.1", runtime = ".NET 10" }));
        group.MapGet("/streams", (SereinMediaService media) => Results.Ok(media.GetSources()));
        return group;
    }
}
