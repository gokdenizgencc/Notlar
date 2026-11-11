using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Notlar.Core;

var builder = Host.CreateApplicationBuilder(args);

// stdout MCP protokolüne ayrılmıştır; tüm loglar stderr'e.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IChangeSignal>(_ => new NamedEventChangeSignal(AppPaths.ChangeEventName));
builder.Services.AddSingleton(sp => new NoteStore(
    AppPaths.DbPath,
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IChangeSignal>()));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
