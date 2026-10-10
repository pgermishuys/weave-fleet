using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using WeaveFleet.Api.Hubs;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Api.Tests.Hubs;

/// <summary>
/// The hub names each domain event for the client from a switch that falls back to the C# type name, so an event
/// missing from it reaches the browser as "ModsChanged" and is dropped. Every event the base type lists must be in it.
/// </summary>
public sealed class DomainEventHubNamesTests
{
    public static TheoryData<Type, string> Events
    {
        get
        {
            var data = new TheoryData<Type, string>();
            foreach (var attribute in typeof(DomainEvent).GetCustomAttributes<JsonDerivedTypeAttribute>())
                data.Add(attribute.DerivedType, (string)attribute.TypeDiscriminator!);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Events))]
    public void The_hub_sends_each_domain_event_under_its_discriminator(Type eventType, string discriminator)
    {
        var resolve = typeof(SessionEventsHub).GetMethod("ResolveDomainEventType", BindingFlags.NonPublic | BindingFlags.Static)!;
        var instance = RuntimeHelpers.GetUninitializedObject(eventType);

        resolve.Invoke(null, [instance]).ShouldBe(discriminator);
    }
}
