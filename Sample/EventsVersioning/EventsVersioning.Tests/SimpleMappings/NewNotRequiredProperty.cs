﻿using System.Text.Json;
using FluentAssertions;
using V1 = ECommerce.V1;

namespace EventsVersioning.Tests.SimpleMappings;

public class NewNotRequiredProperty
{
    public record ShoppingCartOpened(
        Guid ShoppingCartId,
        Guid ClientId,
        // Adding new not required property as nullable
        DateTime? InitializedAt
    );

    [Fact]
    public void Should_BeForwardCompatible()
    {
        // Given
        var oldEvent = new V1.ShoppingCartOpened(Guid.NewGuid(), Guid.NewGuid());
        var json = JsonSerializer.Serialize(oldEvent);

        // When
        var @event = JsonSerializer.Deserialize<ShoppingCartOpened>(json);

        @event.Should().NotBeNull();
        @event!.ShoppingCartId.Should().Be(oldEvent.ShoppingCartId);
        @event.ClientId.Should().Be(oldEvent.ClientId);
        @event.InitializedAt.Should().BeNull();
    }

    [Fact]
    public void Should_BeBackwardCompatible()
    {
        // Given
        var @event = new ShoppingCartOpened(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow);
        var json = JsonSerializer.Serialize(@event);

        // When
        var oldEvent = JsonSerializer.Deserialize<V1.ShoppingCartOpened>(json);

        oldEvent.Should().NotBeNull();
        oldEvent!.ShoppingCartId.Should().Be(@event.ShoppingCartId);
        oldEvent.ClientId.Should().Be(@event.ClientId);
    }
}
