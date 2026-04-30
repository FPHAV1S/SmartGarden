using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;
using IrrigationSystem.Web.Services;

namespace IrrigationSystem.Web.Pages;

public class LoginPageModel : PageModel
{
    private readonly AuthService AuthService;
    private readonly ILogger<LoginPageModel> Logger;

    [BindProperty]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public string ErrorMessage { get; set; } = string.Empty;
    public bool IsBlocked { get; set; }
    public int RemainingAttempts { get; set; } = 3;

    public LoginPageModel(AuthService authService, ILogger<LoginPageModel> logger)
    {
        AuthService = authService;
        Logger = logger;
    }

    public async Task OnGetAsync()
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        RemainingAttempts = await AuthService.GetRemainingAttemptsAsync(ipAddress);
        IsBlocked = RemainingAttempts == 0;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Please enter both username and password";
            return Page();
        }

        var isValid = await AuthService.ValidateLoginAsync(Username, Password, ipAddress);

        if (isValid)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, Username)
            };

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var authProperties = new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                authProperties);

            Logger.LogInformation("User {Username} logged in successfully", Username);
            
            return Redirect("/");
        }

        RemainingAttempts = await AuthService.GetRemainingAttemptsAsync(ipAddress);
        
        if (RemainingAttempts == 0)
        {
            IsBlocked = true;
        }
        else
        {
            ErrorMessage = "Invalid username or password";
        }

        return Page();
    }
}