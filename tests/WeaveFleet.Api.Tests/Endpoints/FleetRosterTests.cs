using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WeaveFleet.Api.Tests.Infrastructure;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Devices;
using WeaveFleet.Application.Machines;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Machines;

namespace WeaveFleet.Api.Tests.Endpoints;

/// <summary>
/// Your Fleet (Labs): every machine keeps the whole list. Three Fleets (hangar, atlas, workbench) reach each other by
/// name through a router, and each says it can be reached at https://{name}.test.
/// </summary>
[Collection(TwoFleetTestGroup.Name)]
public sealed class FleetRosterTests
{
    [Fact]
    public async Task Adding_atlas_through_hangar_makes_atlas_know_hangar_and_the_link_works_both_ways()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar");
        var atlas = await world.StartAsync("atlas");
        await World.AddAsync(hangar, atlas);

        await hangar.SyncAllAsync();

        var onAtlas = await atlas.ListAsync();
        onAtlas.Select(m => m.Id).ShouldBe([hangar.Id]);
        onAtlas.Single().IntroducedBy.ShouldBe(hangar.Id);
        (await hangar.ListAsync()).Single().IntroducedBy.ShouldBeNull("hangar added atlas itself");
        (await atlas.Machines.CheckAsync(hangar.Id, CancellationToken.None)).Problem.ShouldBeNull("atlas reaches hangar with the key hangar made");
        (await hangar.Machines.CheckAsync(atlas.Id, CancellationToken.None)).Problem.ShouldBeNull();
    }

    [Fact]
    public async Task Adding_workbench_through_atlas_gives_all_three_the_whole_list()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar");
        var atlas = await world.StartAsync("atlas");
        var workbench = await world.StartAsync("workbench");
        await World.AddAsync(hangar, atlas);
        await world.SettleAsync();

        await World.AddAsync(atlas, workbench);
        await world.SettleAsync();

        foreach (var machine in new[] { hangar, atlas, workbench })
        {
            var others = new[] { hangar, atlas, workbench }.Where(o => o != machine).Select(o => o.Id).Order();
            (await machine.ListAsync()).Select(m => m.Id).Order().ShouldBe(others, $"{machine.Name}'s list");
            foreach (var other in others)
                (await machine.Machines.CheckAsync(other, CancellationToken.None)).Problem.ShouldBeNull($"{machine.Name} reaches {other}");
        }

        (await hangar.ListAsync()).Single(m => m.Id == workbench.Id).IntroducedBy.ShouldBe(atlas.Id, "atlas introduced workbench to hangar");
    }

    [Fact]
    public async Task A_key_is_made_by_the_machine_it_opens_and_no_owner_token_is_handed_on()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar");
        var atlas = await world.StartAsync("atlas");
        var workbench = await world.StartAsync("workbench");
        await World.AddAsync(hangar, atlas);
        await World.AddAsync(atlas, workbench);
        await world.SettleAsync();

        // Hangar's key for workbench was made on workbench, when atlas asked for it: a machine-kind device, not the owner token.
        var hangarsKeyForWorkbench = hangar.Machines.TokenOf((await hangar.ListAsync()).Single(m => m.Id == workbench.Id));
        hangarsKeyForWorkbench.ShouldStartWith("fdt_");
        hangarsKeyForWorkbench.ShouldNotBe(workbench.OwnerToken);
        var madeOnWorkbench = (await workbench.Devices.ListAsync()).Where(d => d.Kind == DeviceKinds.Machine).ToList();
        madeOnWorkbench.ShouldContain(d => d.PairedVia == atlas.Id && d.Name.Contains("hangar"));

        foreach (var machine in new[] { hangar, atlas, workbench })
            foreach (var entry in (await machine.ListAsync()).Where(m => m.IntroducedBy is not null))
                machine.Machines.TokenOf(entry).ShouldNotBe(world.Machines[entry.Id].OwnerToken, $"{machine.Name} holds a key for {entry.Name}, not its owner token");
    }

    [Fact]
    public async Task A_machine_that_was_away_catches_up_when_it_comes_back()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar");
        var atlas = await world.StartAsync("atlas");
        var workbench = await world.StartAsync("workbench");
        await World.AddAsync(hangar, atlas);
        await world.SettleAsync();

        world.Router.Down.Add("atlas");
        await World.AddAsync(hangar, workbench);
        await world.SettleAsync();
        (await atlas.ListAsync()).Select(m => m.Id).ShouldBe([hangar.Id], "atlas hasn't heard");
        (await workbench.ListAsync()).Select(m => m.Id).ShouldBe([hangar.Id]);

        world.Router.Down.Remove("atlas");
        await hangar.Roster.SyncWithAsync(atlas.Id, CancellationToken.None);
        await world.SettleAsync();

        (await atlas.ListAsync()).Select(m => m.Id).Order().ShouldBe(new[] { hangar.Id, workbench.Id }.Order());
        (await workbench.ListAsync()).Select(m => m.Id).Order().ShouldBe(new[] { hangar.Id, atlas.Id }.Order());
        (await atlas.Machines.CheckAsync(workbench.Id, CancellationToken.None)).Problem.ShouldBeNull();
    }

    [Fact]
    public async Task A_key_nobody_took_is_removed_again()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar");
        var atlas = await world.StartAsync("atlas");
        hangar.Addresses.Clear(); // hangar can't be reached at any address atlas is told
        await World.AddAsync(hangar, atlas);

        await hangar.SyncAllAsync();

        (await atlas.ListAsync()).ShouldBeEmpty();
        (await hangar.Devices.ListAsync()).Where(d => d.Kind == DeviceKinds.Machine).ShouldBeEmpty("hangar took its own key back");
    }

    [Fact]
    public async Task An_address_that_answers_as_another_machine_never_gets_the_key()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar");
        var atlas = await world.StartAsync("atlas");
        await world.StartAsync("workbench");
        using var peer = Client(atlas, atlas.OwnerToken);
        var seen = new List<string?>();
        world.Router.Seen = request => { if (request.RequestUri!.Host == "workbench.test") seen.Add(request.Headers.Authorization?.Parameter); };

        // hangar says it is at workbench's address: workbench answers as workbench, so nothing is sent there.
        var stranger = await peer.PostAsJsonAsync("/api/machine/introductions",
            new Introduction(hangar.Id, "x", null, ["https://workbench.test"], "fdt_x.y", null));
        (await stranger.ReadSuccessAsync()).GetProperty("status").GetString().ShouldBe("unreachable");
        seen.Where(t => t is not null).ShouldBeEmpty("no token went to the wrong machine");
    }

    [Fact]
    public async Task With_Your_Fleet_off_a_machine_takes_no_introductions()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar");
        var atlas = await world.StartAsync("atlas", yourFleet: false);
        await World.AddAsync(hangar, atlas);

        await hangar.SyncAllAsync();

        (await atlas.ListAsync()).ShouldBeEmpty();
        (await atlas.Devices.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_another_machine_in_the_fleet_introduces_and_a_phone_does_not()
    {
        await using var world = new World();
        var atlas = await world.StartAsync("atlas");
        var (_, phoneToken) = await atlas.Devices.IssueAsync("Pixel 9", "android");
        using var phone = Client(atlas, phoneToken);

        (await phone.PostAsJsonAsync("/api/machine/introductions", new Introduction("m", "x", null, ["https://x.test"], "t", null)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_machine_removes_only_the_devices_it_made()
    {
        await using var world = new World();
        var atlas = await world.StartAsync("atlas");
        var (_, hangarKey) = await atlas.Devices.IssueAsync("hangar", "other", kind: DeviceKinds.Machine);
        var (_, workbenchKey) = await atlas.Devices.IssueAsync("workbench", "other", kind: DeviceKinds.Machine);
        var (phone, _) = await atlas.Devices.IssueAsync("Pixel 9", "android", pairedVia: "someone");
        using var hangar = Client(atlas, hangarKey);
        using var workbench = Client(atlas, workbenchKey);

        var made = (await (await hangar.PostAsJsonAsync("/api/machine/devices", new { name = "Chrome via hangar", platform = "other", pairedVia = "hangar-id" })).ReadSuccessAsync())
            .GetProperty("deviceId").GetString();

        (await workbench.DeleteAsync($"/api/machine/devices/{made}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "workbench didn't make it");
        (await hangar.DeleteAsync($"/api/machine/devices/{phone.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a phone paired here isn't a machine's");
        (await hangar.DeleteAsync($"/api/machine/devices/{made}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var owner = Client(atlas, atlas.OwnerToken);
        (await owner.DeleteAsync($"/api/machine/devices/{phone.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent, "the owner removes anything");
    }

    // ---- forgetting a machine and removing a phone reach every machine ----

    [Fact]
    public async Task Forgetting_atlas_on_hangar_drops_it_from_every_list_and_revokes_its_keys()
    {
        await using var world = new World();
        var (hangar, atlas, workbench) = await world.StartThreeAsync();
        var hangarKeyOnAtlas = hangar.KeyFor(atlas);
        var workbenchKeyOnAtlas = workbench.KeyFor(atlas);
        var atlasKeyOnHangar = atlas.KeyFor(hangar);
        (await Probe(atlas, hangarKeyOnAtlas)).ShouldBe(HttpStatusCode.OK);

        using var owner = Client(hangar, hangar.OwnerToken);
        (await owner.DeleteAsync($"/api/machines/{atlas.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await world.SettleAsync();

        (await hangar.ListAsync()).Select(m => m.Id).ShouldBe([workbench.Id]);
        (await workbench.ListAsync()).Select(m => m.Id).ShouldBe([hangar.Id]);
        (await atlas.ListAsync()).ShouldBeEmpty("atlas was told, and dropped the machines that forgot it");
        (await Probe(atlas, hangarKeyOnAtlas)).ShouldBe(HttpStatusCode.Unauthorized, "hangar's key on atlas");
        (await Probe(atlas, workbenchKeyOnAtlas)).ShouldBe(HttpStatusCode.Unauthorized, "workbench's key on atlas");
        (await Probe(hangar, atlasKeyOnHangar)).ShouldBe(HttpStatusCode.Unauthorized, "the key atlas used on hangar");
        (await hangar.Devices.ListAsync()).Where(d => d.MadeFor == atlas.Id).ShouldBeEmpty();
        (await workbench.Devices.ListAsync()).Where(d => d.MadeFor == atlas.Id).ShouldBeEmpty();
        (await hangar.Removals.GetAsync(atlas.Id)).ShouldNotBeNull();
        (await workbench.Removals.GetAsync(atlas.Id)).ShouldNotBeNull("workbench keeps it too");
    }

    [Fact]
    public async Task A_forgotten_machine_leaves_the_whole_Fleet_even_when_the_forgetter_cannot_reach_the_rest()
    {
        await using var world = new World();
        var (hangar, atlas, workbench) = await world.StartThreeAsync();
        var workbenchKeyOnAtlas = workbench.KeyFor(atlas);
        var atlasKeyOnWorkbench = atlas.KeyFor(workbench);
        (await Probe(workbench, atlasKeyOnWorkbench)).ShouldBe(HttpStatusCode.OK);

        // Workbench can't tell atlas it was forgotten, so atlas hears it from hangar alone.
        world.Router.Down.Add("workbench");
        using var owner = Client(hangar, hangar.OwnerToken);
        (await owner.DeleteAsync($"/api/machines/{atlas.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await atlas.ListAsync()).ShouldBeEmpty("atlas left hangar's Fleet whole: workbench is gone from its list too");
        (await Probe(atlas, workbenchKeyOnAtlas)).ShouldBe(HttpStatusCode.Unauthorized, "workbench's key on atlas");
        (await atlas.Devices.ListAsync()).Where(d => d.Kind == DeviceKinds.Machine).ShouldBeEmpty("atlas made no key that still works for a machine of that Fleet");
        (await atlas.Removals.GetAsync(workbench.Id)).ShouldBeNull("workbench was not forgotten, atlas just doesn't list it");
        (await atlas.Removals.GetAsync(hangar.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task A_forgotten_machine_keeps_a_machine_it_added_that_is_not_in_that_Fleet()
    {
        await using var world = new World();
        var (hangar, atlas, workbench) = await world.StartThreeAsync();
        var depot = await world.StartAsync("depot", yourFleet: false);

        // Atlas adds depot while hangar can't hear about it, so depot is atlas's own and not in hangar's list.
        world.Router.Down.Add("hangar");
        await World.AddAsync(atlas, depot);
        world.Router.Down.Remove("hangar");
        (await hangar.ListAsync()).Select(m => m.Id).ShouldNotContain(depot.Id);

        world.Router.Down.Add("workbench");
        using var owner = Client(hangar, hangar.OwnerToken);
        (await owner.DeleteAsync($"/api/machines/{atlas.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await atlas.ListAsync()).Select(m => m.Id).ShouldBe([depot.Id], "atlas is a machine of its own, with the machine it added itself");
    }

    [Fact]
    public async Task A_machine_that_was_off_applies_the_removal_when_it_is_back_and_does_not_bring_the_machine_back()
    {
        await using var world = new World();
        var (hangar, atlas, workbench) = await world.StartThreeAsync();

        world.Router.Down.Add("workbench");
        using var owner = Client(hangar, hangar.OwnerToken);
        (await owner.DeleteAsync($"/api/machines/{atlas.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await workbench.ListAsync()).Select(m => m.Id).Order().ShouldBe(new[] { hangar.Id, atlas.Id }.Order(), "workbench hasn't heard");

        // Workbench is back and, with a list that still has atlas, compares lists with everyone.
        world.Router.Down.Remove("workbench");
        await workbench.SyncAllAsync();
        await world.SettleAsync();

        (await workbench.ListAsync()).Select(m => m.Id).ShouldBe([hangar.Id], "workbench applied the removal");
        (await hangar.ListAsync()).Select(m => m.Id).ShouldBe([workbench.Id], "and did not bring atlas back");
        (await atlas.ListAsync()).ShouldBeEmpty();
        (await workbench.Removals.GetAsync(atlas.Id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Hangar_tells_a_machine_that_was_off_when_it_answers_again()
    {
        await using var world = new World();
        var (hangar, atlas, workbench) = await world.StartThreeAsync();

        world.Router.Down.Add("workbench");
        using var owner = Client(hangar, hangar.OwnerToken);
        (await owner.DeleteAsync($"/api/machines/{atlas.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        world.Router.Down.Remove("workbench");

        await hangar.Roster.SyncWithAsync(workbench.Id, CancellationToken.None);

        (await workbench.ListAsync()).Select(m => m.Id).ShouldBe([hangar.Id]);
    }

    [Fact]
    public async Task A_forgotten_machine_added_back_on_purpose_joins_every_machine_again()
    {
        await using var world = new World();
        var (hangar, atlas, workbench) = await world.StartThreeAsync();
        using var owner = Client(hangar, hangar.OwnerToken);
        (await owner.DeleteAsync($"/api/machines/{atlas.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await world.SettleAsync();
        (await workbench.Removals.GetAsync(atlas.Id)).ShouldNotBeNull();

        await Task.Delay(50);
        await World.AddAsync(hangar, atlas, byCode: true);
        (await hangar.Removals.GetAsync(atlas.Id)).ShouldBeNull("adding it by hand clears the removal where it is added");
        await world.SettleAsync();

        foreach (var machine in new[] { hangar, atlas, workbench })
        {
            var others = new[] { hangar, atlas, workbench }.Where(o => o != machine).Select(o => o.Id).Order();
            (await machine.ListAsync()).Select(m => m.Id).Order().ShouldBe(others, $"{machine.Name}'s list");
        }

        (await workbench.Removals.GetAsync(atlas.Id)).ShouldBeNull("the machine that added it introduced it after it was forgotten");
    }

    [Fact]
    public async Task A_list_that_is_out_of_date_cannot_introduce_a_forgotten_machine()
    {
        await using var world = new World();
        var (hangar, atlas, workbench) = await world.StartThreeAsync();
        using var owner = Client(hangar, hangar.OwnerToken);
        (await owner.DeleteAsync($"/api/machines/{atlas.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Workbench still lists atlas as of before hangar forgot it, and introduces it.
        var introduction = new Introduction(atlas.Id, "atlas", null, atlas.Addresses.ToList(), "fdt_x.y", workbench.Id, DateTimeOffset.UtcNow.AddHours(-1));
        (await hangar.Roster.AcceptAsync(introduction, CancellationToken.None)).ShouldBe(IntroductionOutcome.Refused);
        (await hangar.Roster.AcceptAsync(introduction with { Since = null }, CancellationToken.None)).ShouldBe(IntroductionOutcome.Refused);
        (await hangar.ListAsync()).Select(m => m.Id).ShouldBe([workbench.Id]);
    }

    [Fact]
    public async Task Removing_a_browser_on_hangar_takes_its_keys_on_every_machine_and_it_gets_none_back()
    {
        await using var world = new World();
        var (hangar, atlas, workbench) = await world.StartThreeAsync();
        using var ownerOnWorkbench = Client(workbench, workbench.OwnerToken);
        var keyOnHangar = await GrantAsync(ownerOnWorkbench, hangar, "abc123", "Chrome");
        var keyOnAtlas = await GrantAsync(ownerOnWorkbench, atlas, "abc123", "Chrome");
        (await Probe(hangar, keyOnHangar)).ShouldBe(HttpStatusCode.OK);
        (await Probe(atlas, keyOnAtlas)).ShouldBe(HttpStatusCode.OK);

        // On hangar the browser is the device "Chrome via workbench".
        var device = (await hangar.Devices.ListAsync()).Single(d => d.MadeFor == "screen:abc123");
        using var ownerOnHangar = Client(hangar, hangar.OwnerToken);
        (await ownerOnHangar.DeleteAsync($"/api/machine/devices/{device.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await world.SettleAsync();

        (await Probe(hangar, keyOnHangar)).ShouldBe(HttpStatusCode.Unauthorized);
        (await Probe(atlas, keyOnAtlas)).ShouldBe(HttpStatusCode.Unauthorized, "its key on atlas went too");
        (await atlas.Devices.ListAsync()).Where(d => d.MadeFor == "screen:abc123").ShouldBeEmpty();

        // Asking again gets a clear "removed", not a fresh key.
        var again = await ownerOnWorkbench.PostAsJsonAsync($"/api/machines/{atlas.Id}/device-grant", new { screenId = "abc123", name = "Chrome" });
        again.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await again.Content.ReadAsStringAsync()).ShouldContain("removed");
        (await atlas.Devices.ListAsync()).Where(d => d.MadeFor == "screen:abc123").ShouldBeEmpty("no new key was made");
        (await workbench.Machines.GetAsync(atlas.Id)).ShouldNotBeNull("the browser was removed, not the machine");
    }

    [Fact]
    public async Task Removing_a_phone_paired_with_workbench_on_atlas_unpairs_it_and_takes_its_keys_everywhere()
    {
        await using var world = new World();
        var (hangar, atlas, workbench) = await world.StartThreeAsync();
        var (phone, phoneToken) = await workbench.Devices.IssueAsync("Alex's iPhone", "ios");
        using var phoneClient = Client(workbench, phoneToken);
        var keyOnHangar = await GrantAsync(phoneClient, hangar, null, null);
        var keyOnAtlas = await GrantAsync(phoneClient, atlas, null, null);

        var onAtlas = (await atlas.Devices.ListAsync()).Single(d => d.MadeFor == phone.Id);
        using var ownerOnAtlas = Client(atlas, atlas.OwnerToken);
        (await ownerOnAtlas.DeleteAsync($"/api/machine/devices/{onAtlas.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await world.SettleAsync();

        (await Probe(workbench, phoneToken)).ShouldBe(HttpStatusCode.Unauthorized, "the phone's own key on its home");
        (await Probe(hangar, keyOnHangar)).ShouldBe(HttpStatusCode.Unauthorized);
        (await Probe(atlas, keyOnAtlas)).ShouldBe(HttpStatusCode.Unauthorized);
        (await phoneClient.PostAsJsonAsync($"/api/machines/{hangar.Id}/device-grant", new { })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "a removed phone is not handed a new key");
    }

    [Fact]
    public async Task A_machine_that_was_off_removes_the_browser_s_key_when_it_is_back()
    {
        await using var world = new World();
        var (hangar, atlas, workbench) = await world.StartThreeAsync();
        using var ownerOnWorkbench = Client(workbench, workbench.OwnerToken);
        var keyOnAtlas = await GrantAsync(ownerOnWorkbench, atlas, "abc123", "Chrome");
        await GrantAsync(ownerOnWorkbench, hangar, "abc123", "Chrome");

        world.Router.Down.Add("atlas");
        var device = (await hangar.Devices.ListAsync()).Single(d => d.MadeFor == "screen:abc123");
        using var ownerOnHangar = Client(hangar, hangar.OwnerToken);
        (await ownerOnHangar.DeleteAsync($"/api/machine/devices/{device.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await Probe(atlas, keyOnAtlas)).ShouldBe(HttpStatusCode.OK, "atlas was off, so nobody could take the key yet");

        world.Router.Down.Remove("atlas");
        await atlas.SyncAllAsync();
        await world.SettleAsync();

        (await Probe(atlas, keyOnAtlas)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---- a new server joins with a one-time code (install.sh --join) ----

    [Fact]
    public async Task A_new_server_joins_with_the_code_and_both_machines_list_each_other()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar", publicUrl: true);
        var atlas = await world.StartAsync("atlas", publicUrl: true);

        var line = await MakeJoinLineAsync(hangar);
        line.ShouldStartWith("https://hangar.test");
        line.ShouldMatch("#[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$");
        var joined = await JoinAsync(atlas, line);

        joined.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await joined.ReadSuccessAsync();
        body.GetProperty("status").GetString().ShouldBe("joined");
        body.GetProperty("fleetMachineName").GetString().ShouldBe("hangar");
        body.GetProperty("fleetKeepsList").GetBoolean().ShouldBeTrue();
        await world.SettleAsync();
        (await hangar.ListAsync()).Select(m => m.Id).ShouldBe([atlas.Id]);
        (await atlas.ListAsync()).Select(m => m.Id).ShouldBe([hangar.Id]);
        (await atlas.Machines.CheckAsync(hangar.Id, CancellationToken.None)).Problem.ShouldBeNull("atlas reaches hangar with the key hangar made for it");
        (await hangar.Machines.CheckAsync(atlas.Id, CancellationToken.None)).Problem.ShouldBeNull();
        hangar.Machines.TokenOf((await hangar.ListAsync()).Single()).ShouldNotBe(atlas.OwnerToken, "hangar holds a key atlas made for it, not atlas's owner token");
    }

    [Fact]
    public async Task The_machines_already_in_the_fleet_hear_about_the_new_server()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar", publicUrl: true);
        var workbench = await world.StartAsync("workbench");
        var atlas = await world.StartAsync("atlas", publicUrl: true);
        await World.AddAsync(hangar, workbench, byCode: true);
        await world.SettleAsync();

        (await JoinAsync(atlas, await MakeJoinLineAsync(hangar))).StatusCode.ShouldBe(HttpStatusCode.OK);
        await world.SettleAsync();

        foreach (var machine in new[] { hangar, atlas, workbench })
        {
            var others = new[] { hangar, atlas, workbench }.Where(o => o != machine).Select(o => o.Id).Order();
            (await machine.ListAsync()).Select(m => m.Id).Order().ShouldBe(others, $"{machine.Name}'s list");
        }
    }

    [Fact]
    public async Task A_join_code_works_once()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar", publicUrl: true);
        var atlas = await world.StartAsync("atlas", publicUrl: true);
        var workbench = await world.StartAsync("workbench", publicUrl: true);
        var line = await MakeJoinLineAsync(hangar);

        (await JoinAsync(atlas, line)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await JoinAsync(workbench, line);

        second.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await second.Content.ReadAsStringAsync();
        body.ShouldContain("code-refused");
        body.ShouldContain("expired or was already used");
        (await hangar.ListAsync()).Select(m => m.Id).ShouldBe([atlas.Id], "the second machine was not added");
        (await workbench.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_code_nobody_made_is_refused_and_adds_nothing()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar", publicUrl: true);
        var atlas = await world.StartAsync("atlas", publicUrl: true);

        var wrong = await JoinAsync(atlas, "https://hangar.test#ABCD-EFGH");

        wrong.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await wrong.Content.ReadAsStringAsync()).ShouldContain("code-refused");
        (await hangar.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_line_that_is_not_a_join_line_is_turned_down_before_anything_is_sent()
    {
        await using var world = new World();
        await world.StartAsync("hangar", publicUrl: true);
        var atlas = await world.StartAsync("atlas", publicUrl: true);
        var sent = 0;
        world.Router.Seen = _ => sent++;

        foreach (var line in new[] { "", "ABCD-EFGH", "https://hangar.test", "https://hangar.test#nope", "#ABCD-EFGH" })
            (await JoinAsync(atlas, line)).StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"'{line}'");

        sent.ShouldBe(0);
    }

    [Fact]
    public async Task A_new_server_the_fleet_cannot_reach_back_says_so_and_the_code_is_used_up()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar", publicUrl: true);
        var atlas = await world.StartAsync("atlas", publicUrl: true);
        // Atlas tells the Fleet it listens somewhere nothing answers.
        atlas.Factory.Services.GetRequiredService<MachineIdentityStore>().Update(identity => identity with { PublicUrl = "https://nowhere.test" });
        var line = await MakeJoinLineAsync(hangar);

        var failed = await JoinAsync(atlas, line);

        failed.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        var text = (await failed.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("message").GetString()!;
        text.ShouldContain("couldn't reach");
        text.ShouldContain("make a new one");
        (await hangar.ListAsync()).ShouldBeEmpty();
        using var scope = atlas.Factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<YourFleetFeature>().IsEnabledAsync()).ShouldBeTrue("atlas had Your Fleet on and still has");
    }

    [Fact]
    public async Task With_Your_Fleet_off_on_the_inviting_machine_only_it_lists_the_new_server_and_atlas_keeps_the_switch_off()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar", yourFleet: false, publicUrl: true);
        var atlas = await world.StartAsync("atlas", yourFleet: false, publicUrl: true);

        var joined = await JoinAsync(atlas, await MakeJoinLineAsync(hangar));

        var body = await joined.ReadSuccessAsync();
        body.GetProperty("fleetKeepsList").GetBoolean().ShouldBeFalse();
        body.GetProperty("listed").GetBoolean().ShouldBeFalse();
        (await hangar.ListAsync()).Select(m => m.Id).ShouldBe([atlas.Id]);
        (await atlas.ListAsync()).ShouldBeEmpty();
        using var scope = atlas.Factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<YourFleetFeature>().IsEnabledAsync()).ShouldBeFalse("joining a Fleet that keeps no list doesn't turn the Labs switch on");
    }

    [Fact]
    public async Task Only_the_owner_makes_a_join_code_and_a_phone_does_not()
    {
        await using var world = new World();
        var hangar = await world.StartAsync("hangar", publicUrl: true);
        var (_, phoneToken) = await hangar.Devices.IssueAsync("Pixel 9", "android");
        using var phone = Client(hangar, phoneToken);

        (await phone.PostAsync("/api/machines/join-code", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task<string> MakeJoinLineAsync(Node inviting)
    {
        using var owner = Client(inviting, inviting.OwnerToken);
        var made = await (await owner.PostAsync("/api/machines/join-code", null)).ReadSuccessAsync();
        made.GetProperty("code").GetString()!.ShouldMatch("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$");
        made.GetProperty("addresses").EnumerateArray().Select(a => a.GetString()).ShouldContain($"https://{inviting.Name}.test");
        (made.GetProperty("expiresAt").GetDateTimeOffset() - DateTimeOffset.UtcNow).ShouldBeInRange(TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
        return made.GetProperty("target").GetString()!;
    }

    /// <summary>What <c>fleet node join</c> does: asks the machine running here, with its own token, to join.</summary>
    private static async Task<HttpResponseMessage> JoinAsync(Node joining, string line)
    {
        using var owner = Client(joining, joining.OwnerToken);
        var response = await owner.PostAsJsonAsync("/api/machine/join", new { target = line });
        return response;
    }

    // ---- scaffolding ----

    private sealed class Router : HttpMessageHandler
    {
        public Dictionary<string, HttpMessageHandler> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Down { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Action<HttpRequestMessage>? Seen { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen?.Invoke(request);
            var host = request.RequestUri!.Host;
            var name = host.EndsWith(".test", StringComparison.Ordinal) ? host[..^5] : host;
            if (!Targets.TryGetValue(name, out var target) || Down.Contains(name))
                throw new HttpRequestException("connection refused");
            return await new HttpMessageInvoker(target, disposeHandler: false).SendAsync(request, cancellationToken);
        }
    }

    private sealed class Node(string name, ApiWebApplicationFactory factory, List<string> addresses)
    {
        public string Name { get; } = name;

        public ApiWebApplicationFactory Factory { get; } = factory;

        public List<string> Addresses { get; } = addresses;

        public string Id => Factory.Services.GetRequiredService<MachineIdentityStore>().Get().Id;

        public string OwnerToken => Factory.Services.GetRequiredService<ILocalTokenAuthService>().Token;

        public RemoteMachineService Machines => Factory.Services.GetRequiredService<RemoteMachineService>();

        public FleetRoster Roster => Factory.Services.GetRequiredService<FleetRoster>();

        public DeviceTokenService Devices => Factory.Services.GetRequiredService<DeviceTokenService>();

        public Task<IReadOnlyList<RemoteMachine>> ListAsync() => Machines.ListAsync();

        public Task SyncAllAsync() => Roster.SyncAllAsync(CancellationToken.None);

        public FleetRemovals Removals => Factory.Services.GetRequiredService<FleetRemovals>();

        /// <summary>The key this machine holds for <paramref name="other"/>.</summary>
        public string KeyFor(Node other) => Machines.TokenOf(Machines.ListAsync().GetAwaiter().GetResult().Single(m => m.Id == other.Id));
    }

    private sealed class FixedAddresses(List<string> addresses) : ISelfAddresses
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(addresses.ToList());
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly List<Node> _nodes = [];

        public Router Router { get; } = new();

        public Dictionary<string, Node> Machines { get; } = [];

        /// <summary>hangar, atlas and workbench, each listing the other two.</summary>
        public async Task<(Node Hangar, Node Atlas, Node Workbench)> StartThreeAsync()
        {
            var hangar = await StartAsync("hangar");
            var atlas = await StartAsync("atlas");
            var workbench = await StartAsync("workbench");
            await AddAsync(hangar, atlas, byCode: true);
            await SettleAsync();
            await AddAsync(atlas, workbench, byCode: true);
            await SettleAsync();
            return (hangar, atlas, workbench);
        }

        public async Task<Node> StartAsync(string name, bool yourFleet = true, bool publicUrl = false)
        {
            var addresses = new List<string> { $"https://{name}.test" };
            var factory = new ApiWebApplicationFactory(
                authEnabled: false,
                tokenAuthEnabled: true,
                simulateLocalhostRequest: true,
                host: "0.0.0.0",
                configureTestServices: services =>
                {
                    services.AddHttpClient(RemoteMachineService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Router);
                    services.AddHttpClient(FleetJoin.JoinHttpClientName).ConfigurePrimaryHttpMessageHandler(() => Router);
                    services.AddSingleton<ISelfAddresses>(new FixedAddresses(addresses));
                    // The tests run the sync themselves (SettleAsync, SyncAllAsync). A background pass of its own would
                    // reach out from a machine the Router marks Down (Down only blocks calls to it) and race the asserts.
                    foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null && d.ImplementationFactory.Method.ReturnType == typeof(FleetRosterSync)).ToList())
                        services.Remove(hosted);
                });
            Router.Targets[name] = factory.Server.CreateHandler();
            var node = new Node(name, factory, addresses);
            _nodes.Add(node);
            Machines[node.Id] = node;
            // The address a join line carries, and the one a joining machine gives the Fleet to reach it back at.
            factory.Services.GetRequiredService<MachineIdentityStore>().Update(identity => identity with { Name = name, PublicUrl = publicUrl ? addresses[0] : identity.PublicUrl });
            if (yourFleet)
            {
                using var scope = factory.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().SetAsync(YourFleet.PreferenceKey, "true");
            }

            return node;
        }

        /// <summary>The owner adds <paramref name="added"/> on <paramref name="through"/> with its machine token.</summary>
        public static async Task AddAsync(Node through, Node added, bool byCode = false)
        {
            // By code, the key is a machine-kind device the added machine made for it, as `fleet node pair` does; otherwise its owner token.
            var token = byCode ? (await added.Devices.IssueAsync(through.Name, "other", kind: DeviceKinds.Machine)).Token : added.OwnerToken;
            var (machine, error) = await through.Machines.AddAsync($"https://{added.Name}.test", token, CancellationToken.None);
            machine.ShouldNotBeNull(error?.Message);
        }

        /// <summary>What the background sync does, run until nothing changes.</summary>
        public async Task SettleAsync()
        {
            for (var round = 0; round < 3; round++)
                foreach (var node in _nodes)
                    await node.SyncAllAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var node in _nodes)
                await node.Factory.DisposeAsync();
        }
    }

    private static async Task<HttpStatusCode> Probe(Node node, string token)
    {
        using var client = Client(node, token);
        return (await client.GetAsync("/api/machines")).StatusCode;
    }

    /// <summary>A screen (screenId set) or a paired phone asks its home for a key on <paramref name="on"/>.</summary>
    private static async Task<string> GrantAsync(HttpClient home, Node on, string? screenId, string? name)
    {
        var response = await home.PostAsJsonAsync($"/api/machines/{on.Id}/device-grant", new { screenId, name });
        return (await response.ReadSuccessAsync()).GetProperty("token").GetString()!;
    }

    private static HttpClient Client(Node node, string token)
    {
        var client = node.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
