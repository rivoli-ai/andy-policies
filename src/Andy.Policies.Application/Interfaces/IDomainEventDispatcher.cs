// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// In-process publisher for domain events. Subscribers register an
/// <see cref="IDomainEventHandler{TEvent}"/> in DI and are dispatched
/// after the originating transaction commits. Errors raised by handlers
/// are logged but never roll back the source transaction — this is the
/// fire-and-forget contract the lifecycle service relies on.
/// </summary>
public interface IDomainEventDispatcher
{
    Task DispatchAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
        where TEvent : notnull;
}

/// <summary>
/// Marker interface for in-process event handlers. Resolve via DI
/// (<c>IEnumerable&lt;IDomainEventHandler&lt;TEvent&gt;&gt;</c>).
/// </summary>
public interface IDomainEventHandler<in TEvent>
    where TEvent : notnull
{
    Task HandleAsync(TEvent domainEvent, CancellationToken ct);
}
