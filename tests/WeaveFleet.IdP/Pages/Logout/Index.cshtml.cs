using Duende.IdentityServer.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WeaveFleet.IdP.Pages.Logout;

[IgnoreAntiforgeryToken]
public sealed class IndexModel(IIdentityServerInteractionService interaction) : PageModel
{
    [BindProperty]
    public string? LogoutId { get; set; }

    public bool ShowSignoutPrompt { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? logoutId)
    {
        LogoutId = logoutId;
        var context = await interaction.GetLogoutContextAsync(logoutId);

        // If the request was authenticated, ask the user to confirm sign-out
        ShowSignoutPrompt = User.Identity?.IsAuthenticated == true;

        if (!ShowSignoutPrompt)
            return await SignOutAsync(context?.PostLogoutRedirectUri);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var context = await interaction.GetLogoutContextAsync(LogoutId);
        return await SignOutAsync(context?.PostLogoutRedirectUri);
    }

    private async Task<IActionResult> SignOutAsync(string? postLogoutRedirectUri)
    {
        if (User.Identity?.IsAuthenticated == true)
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        if (!string.IsNullOrEmpty(postLogoutRedirectUri))
            return Redirect(postLogoutRedirectUri);

        return Page();
    }
}
