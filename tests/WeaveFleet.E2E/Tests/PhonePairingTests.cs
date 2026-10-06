using System.Text.Json;
using WeaveFleet.E2E.Infrastructure;

namespace WeaveFleet.E2E.Tests;

/// <summary>
/// Pairing a phone end to end: the desktop shows a code in Settings, the phone opens the QR link and connects,
/// lands signed in as a device, and loses access the moment the desktop removes it.
/// </summary>
[Trait("Category", "E2E")]
public sealed class PhonePairingTests(PhoneFleetWebApplicationFactory factory, PlaywrightFixture playwright)
    : PhoneE2ETestBase(factory, playwright), IClassFixture<PhoneFleetWebApplicationFactory>, IClassFixture<PlaywrightFixture>
{
    [Fact]
    public async Task A_phone_pairs_from_the_desktop_QR_code_and_is_removed_from_settings()
    {
        var desktop = await DesktopAsync();
        await desktop.GotoAsync("/settings");
        await desktop.GetByText("Machines", new PageGetByTextOptions { Exact = true }).First.ClickAsync();
        await desktop.GetByTestId("add-phone-start").ClickAsync();
        await desktop.GetByTestId("add-phone-url").FillAsync(ServerUrl);
        var pairing = await desktop.RunAndWaitForResponseAsync(
            () => desktop.GetByText("Show the code").ClickAsync(),
            response => response.Url.EndsWith("/api/machine/pairing", StringComparison.Ordinal));
        var url = (await pairing.JsonAsync())!.Value.GetProperty("url").GetString()!;
        await desktop.GetByTestId("add-phone-qr").WaitForAsync();
        url.ShouldContain("/pair#p=");

        var phone = await PhoneAsync();
        await phone.GotoAsync(url);
        await phone.GetByTestId("pair-machine").WaitForAsync();
        (await phone.EvaluateAsync<string>("() => location.hash")).ShouldBeEmpty("the secret leaves the address bar");
        await phone.GetByTestId("pair-device-name").FillAsync("Test Pixel");
        await phone.GetByTestId("pair-connect").ClickAsync();
        await phone.WaitForURLAsync("**/phone/setup");

        (await FetchStatusAsync(phone, "/api/sessions")).ShouldBe(200);
        (await FetchStatusAsync(phone, "/api/machine/access")).ShouldBe(403);
        var token = await phone.EvaluateAsync<string>("() => JSON.parse(localStorage.getItem('weave:device-credentials')).token");
        token.ShouldStartWith("fdt_");

        // The desktop sees it, and removes it.
        await desktop.GetByTestId("device-row").Filter(new LocatorFilterOptions { HasText = "Test Pixel" }).WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        desktop.Dialog += (_, dialog) => _ = dialog.AcceptAsync();
        await desktop.GetByTestId("device-row").Filter(new LocatorFilterOptions { HasText = "Test Pixel" }).GetByText("Remove").ClickAsync();
        await desktop.GetByTestId("device-row").Filter(new LocatorFilterOptions { HasText = "Test Pixel" }).WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });

        (await FetchStatusAsync(phone, "/api/sessions")).ShouldBe(401);
        await phone.GotoAsync("/phone");
        await phone.WaitForURLAsync("**/pair");
    }

    [Fact]
    public async Task The_typed_code_pairs_too()
    {
        using var owner = OwnerClient();
        var created = await owner.PostAsync("/api/machine/pairing", JsonContent(new { baseUrl = ServerUrl }));
        var manualCode = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("manualCode").GetString()!;

        var phone = await PhoneAsync();
        await phone.GotoAsync("/pair");
        await phone.GetByTestId("pair-code").FillAsync(manualCode.ToLowerInvariant());
        await phone.GetByTestId("pair-code-continue").ClickAsync();
        await phone.GetByTestId("pair-connect").ClickAsync();
        await phone.WaitForURLAsync("**/phone/setup");
    }

    private static Task<int> FetchStatusAsync(IPage page, string path) =>
        page.EvaluateAsync<int>("async (path) => (await fetch(path)).status", path);

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");
}
