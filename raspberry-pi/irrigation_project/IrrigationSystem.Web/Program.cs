using IrrigationSystem.Web.Services;
using IrrigationSystem.Web.Models;
using Radzen;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;

const string LoginPath = "/loginpage";
const string OpenAiClientName = "OpenAI";

var builder = WebApplication.CreateBuilder(args);
var openAiBaseUri = new Uri("https://api.openai.com/v1/");

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddRadzenComponents();
builder.Services.AddControllers();
builder.Services.AddHttpClient(WeatherForecastService.ClientName, client =>
{
    client.BaseAddress = new Uri("https://api.open-meteo.com/v1/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<WeatherForecastService>();
builder.Services.AddHostedService<MqttWorkerService>();
builder.Services.Configure<AiIrrigationOptions>(
    builder.Configuration.GetSection(AiIrrigationOptions.SectionName));
builder.Services.AddHttpClient(OpenAiClientName, client =>
{
    client.BaseAddress = openAiBaseUri;
    client.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHttpClient<IAiIrrigationService, OpenAiIrrigationService>(client =>
{
    client.BaseAddress = openAiBaseUri;
    client.Timeout = TimeSpan.FromSeconds(120);
});

builder.Services.AddScoped(sp =>
{
    var navManager = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(navManager.BaseUri) };
});

builder.Services.AddHttpContextAccessor();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/loginpage";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.None;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });

builder.Services.AddAuthorization();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
builder.Services.AddSingleton(sp => 
    new SensorDataService(connectionString, sp.GetRequiredService<ILogger<SensorDataService>>()));

builder.Services.AddSingleton<MqttService>();
builder.Services.AddSingleton<IValveCommandService>(sp => sp.GetRequiredService<MqttService>());
builder.Services.AddSingleton<IrrigationDecisionService>();
builder.Services.AddSingleton<SensorReadingDecisionQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SensorReadingDecisionQueue>());
builder.Services.AddSingleton<OpenAiApiKeyProvider>();
builder.Services.AddSingleton<AiApiHealthService>();

builder.Services.AddSingleton(sp => 
    new AdaptiveWateringService(connectionString, sp.GetRequiredService<ILogger<AdaptiveWateringService>>(), sp.GetRequiredService<SensorDataService>()));

builder.Services.AddSingleton(sp => 
    new AuthService(connectionString, sp.GetRequiredService<ILogger<AuthService>>()));

builder.Services.AddHostedService<AdaptiveBackgroundService>();
builder.Services.AddHostedService<AutoWateringService>();
builder.Services.AddHostedService<AiApiStartupCheckService>();
builder.Services.AddSingleton<DemoModeService>();
builder.Services.AddHostedService<DemoModeService>(sp => sp.GetRequiredService<DemoModeService>());

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MinRequestBodyDataRate = null;
});

builder.WebHost.UseUrls("http://0.0.0.0:5000");

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLower() ?? "";
    var isAuthenticated = context.User?.Identity?.IsAuthenticated ?? false;

    if (!isAuthenticated && !AllowsAnonymousAccess(path))
    {
        context.Response.Redirect(LoginPath);
        return;
    }

    await next();
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.MapControllers();

app.Run();

bool AllowsAnonymousAccess(string path)
{
    return path.StartsWith(LoginPath, StringComparison.Ordinal) ||
        path.StartsWith("/api", StringComparison.Ordinal) ||
        path.Contains("_framework", StringComparison.Ordinal) ||
        path.Contains("_blazor", StringComparison.Ordinal) ||
        path.Contains("css", StringComparison.Ordinal) ||
        path.Contains("_content", StringComparison.Ordinal);
}
