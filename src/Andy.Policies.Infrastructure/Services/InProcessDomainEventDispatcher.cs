// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// Resolves all <see cref="IDomainEventHandler{TEvent}"/> registrations from
/// DI and invokes each in registration order. A handler that throws is
/// logged and skipped; the source transaction has already committed, so
/// no rollback is possible — this is the deliberate fire-and-forget contract
/// from P2.2 (#12).
/// </summary>
public sealed class InProcessDomainEventDispatcher : IDomainEventDispatcher
{
    private readonly IServiceProvider _services;
    private readonly ILogger<InProcessDomainEventDispatcher> _logger;

    public InProcessDomainEventDispatcher(
        IServiceProvider services,
        ILogger<InProcessDomainEventDispatcher> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task DispatchAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var handlers = _services.GetServices<IDomainEventHandler<TEvent>>();
        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(domainEvent, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Domain-event handler {Handler} threw while processing {EventType}; continuing.",
                    handler.GetType().FullName,
                    typeof(TEvent).Name);
            }
        }
    }
}
