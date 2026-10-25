using Serein.AspNetCore;
using Serein.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSerein(options => builder.Configuration.GetSection("Serein").Bind(options));
var app = builder.Build();

app.UseWebSockets();
app.UseHttpFlv();
app.UseWebSocketFlv();
app.MapSereinApi();
app.MapGet("/", () => Results.Ok(new
{
    name = "Serein", streams = "/api/streams",
    httpFlv = "/live/{streamId}/{channel}.flv",
    webSocketFlv = "/ws-live/{streamId}/{channel}"
}));
await app.RunAsync();
