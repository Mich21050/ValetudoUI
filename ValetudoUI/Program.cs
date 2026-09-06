using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using ValetudoUI.Components;
using ValetudoUI.Data;
using ValetudoUI.Dev;
using ValetudoUI.Endpoints;
using ValetudoUI.Options;
using ValetudoUI.Services;
using ValetudoUI.Valetudo;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddOptions<ValetudoOptions>()
    .BindConfiguration(ValetudoOptions.Section)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<HistoryOptions>()
    .BindConfiguration(HistoryOptions.Section);

var valetudoOptions = builder.Configuration.GetSection(ValetudoOptions.Section).Get<ValetudoOptions>() ?? new ValetudoOptions();
var historyOptions = builder.Configuration.GetSection(HistoryOptions.Section).Get<HistoryOptions>() ?? new HistoryOptions();
var useFakeRobot = builder.Environment.IsDevelopment() && valetudoOptions.UseFakeRobot;

// Two named clients: one for REST calls, one with no timeout for the long-lived SSE streams.
builder.Services.AddHttpClient(ValetudoClient.RestClientName, c =>
{
    ConfigureRobotClient(c, valetudoOptions);
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient(ValetudoClient.SseClientName, c =>
{
    ConfigureRobotClient(c, valetudoOptions);
    c.Timeout = Timeout.InfiniteTimeSpan;
}).SetHandlerLifetime(Timeout.InfiniteTimeSpan);

builder.Services.AddSingleton<ValetudoClient>();
builder.Services.AddSingleton<RobotMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RobotMonitorService>());
builder.Services.AddSingleton<CleaningHistoryRecorder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CleaningHistoryRecorder>());
builder.Services.AddSingleton<HistoryQueries>();
builder.Services.AddScoped<ToastService>();

var dbPath = Path.Combine(builder.Environment.ContentRootPath, historyOptions.DbFileName);
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
});

if (useFakeRobot) builder.Services.AddSingleton<FakeRobot>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseResponseCompression();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapMapEndpoints();
app.MapHistoryEndpoints();
if (useFakeRobot) app.MapFakeRobot();

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var db = factory.CreateDbContext();
    db.Database.Migrate();
}

app.Run();

static void ConfigureRobotClient(HttpClient client, ValetudoOptions options)
{
    var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl) ? "http://ROBOT_IP" : options.BaseUrl;
    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    if (!string.IsNullOrEmpty(options.Username))
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }
}
