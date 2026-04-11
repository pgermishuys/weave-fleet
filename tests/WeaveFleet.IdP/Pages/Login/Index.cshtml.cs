using Duende.IdentityServer;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Test;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WeaveFleet.IdP.Pages.Login;

[IgnoreAntiforgeryToken]
public sealed class IndexModel(
    IIdentityServerInteractionService interaction,
    TestUserStore users) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet(string? returnUrl)
    {
        Input = new InputModel { ReturnUrl = returnUrl ?? string.Empty };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var context = await interaction.GetAuthorizationContextAsync(Input.ReturnUrl);

        if (!ModelState.IsValid)
            return Page();

        if (users.ValidateCredentials(Input.Username, Input.Password))
        {
            var user = users.FindByUsername(Input.Username)!;

            var idpUser = new IdentityServerUser(user.SubjectId)
            {
                DisplayName = user.Username,
                AdditionalClaims = user.Claims
            };

            await HttpContext.SignInAsync(idpUser);

            if (context is not null)
                return Redirect(Input.ReturnUrl);

            if (Url.IsLocalUrl(Input.ReturnUrl))
                return Redirect(Input.ReturnUrl);

            return Redirect("~/");
        }

        ErrorMessage = "Invalid username or password.";
        return Page();
    }

    public sealed class InputModel
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string ReturnUrl { get; set; } = string.Empty;
    }
}
