using TecLink.Relay;

var builder = WebApplication.CreateBuilder(args);
// Hosts like Railway give the port in PORT; locally it listens on 8080.
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.Logging.ClearProviders(); // nothing about anybody is ever logged
var app = builder.Build();
RelayServer.Map(app);
app.Run();
