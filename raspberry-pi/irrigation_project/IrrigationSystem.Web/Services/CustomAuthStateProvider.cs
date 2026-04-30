using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using System.Security.Claims;

namespace IrrigationSystem.Web.Services;

public class CustomAuthStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly IHttpContextAccessor HttpContextAccessor;
    private readonly ILogger<CustomAuthStateProvider> Logger;

    public CustomAuthStateProvider(
        ILoggerFactory loggerFactory,
        IHttpContextAccessor httpContextAccessor)
        : base(loggerFactory)
    {
        HttpContextAccessor = httpContextAccessor;
        Logger = loggerFactory.CreateLogger<CustomAuthStateProvider>();
    }

    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    protected override Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var httpContext = HttpContextAccessor.HttpContext;
        
        Logger.LogInformation("Getting auth state - HttpContext exists: {Exists}", httpContext != null);
        Logger.LogInformation("User authenticated: {Auth}", httpContext?.User?.Identity?.IsAuthenticated);
        Logger.LogInformation("Username: {Name}", httpContext?.User?.Identity?.Name);
        
        if (httpContext?.User?.Identity?.IsAuthenticated == true)
        {
            return Task.FromResult(new AuthenticationState(httpContext.User));
        }

        return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}