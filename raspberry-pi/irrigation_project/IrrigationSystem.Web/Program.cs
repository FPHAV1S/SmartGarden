using IrrigationSystem.Web.Services;
using Radzen;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddRadzenComponents();
builder.Services.AddControllers();
builder.Services.AddHostedService<MqttWorkerService>();

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

builder.Services.AddSingleton(sp => 
    new AdaptiveWateringService(connectionString, sp.GetRequiredService<ILogger<AdaptiveWateringService>>(), sp.GetRequiredService<SensorDataService>()));

builder.Services.AddSingleton(sp => 
    new AuthService(connectionString, sp.GetRequiredService<ILogger<AuthService>>()));

builder.Services.AddHostedService<AdaptiveBackgroundService>();
builder.Services.AddHostedService<AutoWateringService>();
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

    if (!isAuthenticated && !path.StartsWith("/loginpage") && !path.Contains("_framework") &&
        !path.Contains("_blazor") && !path.Contains("css") && !path.Contains("_content") &&
        !path.StartsWith("/api"))
    {
        context.Response.Redirect("/loginpage");
        return;
    }

    await next();
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.MapControllers();

app.Run();
