using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using IrrigationSystem.Web.Services;

namespace IrrigationSystem.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LoginController : ControllerBase
{
    private readonly AuthService AuthService;
    private readonly ILogger<LoginController> Logger;

    public LoginController(AuthService authService, ILogger<LoginController> logger)
    {
        AuthService = authService;
        Logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        Logger.LogInformation("Login attempt for user: {Username}", request.Username);
        
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        
        var isValid = await AuthService.ValidateLoginAsync(request.Username, request.Password, ipAddress);

        Logger.LogInformation("Validation result: {IsValid}", isValid);

        if (isValid)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, request.Username),
                new Claim(ClaimTypes.NameIdentifier, request.Username)
            };

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var authProperties = new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
                AllowRefresh = true
            };

            Logger.LogInformation("Signing in user: {Username}", request.Username);

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                authProperties);

            Logger.LogInformation("Sign in complete for user: {Username}", request.Username);

            return Ok(new { success = true });
        }

        var remaining = await AuthService.GetRemainingAttemptsAsync(ipAddress);
        Logger.LogInformation("Login failed, remaining attempts: {Remaining}", remaining);
        
        return Ok(new { success = false, remainingAttempts = remaining });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        Logger.LogInformation("Logout called");
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { success = true });
    }

    [HttpGet("remaining")]
    public async Task<IActionResult> GetRemainingAttempts()
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var remaining = await AuthService.GetRemainingAttemptsAsync(ipAddress);
        return Ok(new { remainingAttempts = remaining });
    }
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}