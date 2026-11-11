using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Notlar.Core;
using Notlar.Mcp;

if (ContextCommand.TryRun(args))
    return;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Claude Code'un o anki çalışma dizininden appsettings yüklemesini/izlemesini önler.
    ContentRootPath = AppContext.BaseDirectory,
});

// stdout MCP protokolüne ayrılmıştır; tüm loglar stderr'e.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new McpAuthor(McpAuthorParser.Parse(args)));
builder.Services.AddSingleton<IChangeSignal>(sp =>
{
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Notlar.ChangeSignal");
    return ChangeSignals.CreateOrNull(AppPaths.ChangeEventName,
        ex => logger.LogWarning(ex, "Değişiklik sinyali oluşturulamadı; canlı güncellemeler devre dışı."));
});
builder.Services.AddSingleton(sp => new NoteStore(
    AppPaths.DbPath,
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IChangeSignal>()));
builder.Services.AddSingleton(sp => new TaskStore(
    AppPaths.DbPath,
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IChangeSignal>()));
builder.Services.AddSingleton(sp => new MemoryStore(
    AppPaths.DbPath,
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IChangeSignal>()));
builder.Services.AddSingleton(sp => new SkillRegistry(
    AppPaths.DbPath,
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IChangeSignal>()));
builder.Services.AddSingleton(sp => new SkillTranslations(
    AppPaths.DbPath,
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IChangeSignal>()));
builder.Services.AddSingleton(sp => new PlanStore(
    AppPaths.DbPath,
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IChangeSignal>()));
builder.Services.AddSingleton(sp => new AppFlags(AppPaths.DbPath));
builder.Services.AddSingleton(sp => new ProjectStore(
    AppPaths.DbPath,
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IChangeSignal>()));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
