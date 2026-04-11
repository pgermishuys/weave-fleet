using Duende.IdentityServer;
using WeaveFleet.IdP;
using WeaveFleet.IdP.Pages.Login;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

builder.Services.AddIdentityServer(options =>
    {
        options.Events.RaiseErrorEvents = true;
        options.Events.RaiseInformationEvents = true;
        options.Events.RaiseFailureEvents = true;
        options.Events.RaiseSuccessEvents = true;
        options.EmitStaticAudienceClaim = true;
        options.UserInteraction.LoginUrl = "/Login";
        options.UserInteraction.LogoutUrl = "/Logout";
    })
    .AddInMemoryIdentityResources(Config.IdentityResources)
    .AddInMemoryApiScopes(Config.ApiScopes)
    .AddInMemoryClients(Config.GetClients([]))
    .AddTestUsers(Config.TestUsers)
    .AddRedirectUriValidator<PermissiveRedirectUriValidator>();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.UseIdentityServer();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
