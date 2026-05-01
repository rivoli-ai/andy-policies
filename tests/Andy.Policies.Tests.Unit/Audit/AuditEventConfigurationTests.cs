// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Domain.Entities;
using Andy.Policies.Tests.Unit.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Andy.Policies.Tests.Unit.Audit;

/// <summary>
/// P6.1 (#41) — verifies the EF mapping for <see cref="AuditEvent"/>
/// pins the table name, the unique <c>seq</c> index, and the three
/// query indexes (entity, actor, timestamp). These properties are
/// what P6.5 / P6.6 query patterns rely on; a regression here
/// would surface as silent table-scan performance loss in a
/// production read.
/// </summary>
public class AuditEventConfigurationTests
{
    [Fact]
    public void AuditEvent_MapsToTable_audit_events()
    {
        using var db = InMemoryDbFixture.Create();
        var entityType = db.Model.FindEntityType(typeof(AuditEvent));

        entityType.Should().NotBeNull();
        entityType!.GetTableName().Should().Be("audit_events");
    }

    [Fact]
    public void Seq_HasUniqueIndex()
    {
        using var db = InMemoryDbFixture.Create();
        var entityType = db.Model.FindEntityType(typeof(AuditEvent));

        var seqIndex = entityType!.GetIndexes()
            .FirstOrDefault(i => i.Properties.Count == 1
                                  && i.Properties[0].Name == nameof(AuditEvent.Seq));
        seqIndex.Should().NotBeNull("P6.2's verifier walks rows ordered by seq; the index must be present");
        seqIndex!.IsUnique.Should().BeTrue();
    }

    [Fact]
    public void EntityTypeAndId_HaveCompositeIndex()
    {
        using var db = InMemoryDbFixture.Create();
        var entityType = db.Model.FindEntityType(typeof(AuditEvent));

        var compositeIndex = entityType!.GetIndexes()
            .FirstOrDefault(i => i.Properties.Count == 2
                                  && i.Properties[0].Name == nameof(AuditEvent.EntityType)
                                  && i.Properties[1].Name == nameof(AuditEvent.EntityId));
        compositeIndex.Should().NotBeNull("entity-scoped queries (P6.5 GET /api/audit?entityId=…) hit this index");
    }

    [Fact]
    public void ActorSubjectId_HasIndex()
    {
        using var db = InMemoryDbFixture.Create();
        var entityType = db.Model.FindEntityType(typeof(AuditEvent));

        var actorIndex = entityType!.GetIndexes()
            .FirstOrDefault(i => i.Properties.Count == 1
                                  && i.Properties[0].Name == nameof(AuditEvent.ActorSubjectId));
        actorIndex.Should().NotBeNull("actor-scoped queries (compliance review) hit this index");
    }

    [Fact]
    public void Timestamp_HasIndex()
    {
        using var db = InMemoryDbFixture.Create();
        var entityType = db.Model.FindEntityType(typeof(AuditEvent));

        var tsIndex = entityType!.GetIndexes()
            .FirstOrDefault(i => i.Properties.Count == 1
                                  && i.Properties[0].Name == nameof(AuditEvent.Timestamp));
        tsIndex.Should().NotBeNull("time-range queries (P6.6 GET /api/audit?since=…) hit this index");
    }

    [Fact]
    public void RequiredColumns_AreNonNullable()
    {
        using var db = InMemoryDbFixture.Create();
        var entityType = db.Model.FindEntityType(typeof(AuditEvent));

        foreach (var name in new[]
        {
            nameof(AuditEvent.ActorSubjectId),
            nameof(AuditEvent.Action),
            nameof(AuditEvent.EntityType),
            nameof(AuditEvent.EntityId),
            nameof(AuditEvent.PrevHash),
            nameof(AuditEvent.Hash),
            nameof(AuditEvent.FieldDiffJson),
            nameof(AuditEvent.ActorRoles),
        })
        {
            var prop = entityType!.FindProperty(name);
            prop.Should().NotBeNull();
            prop!.IsNullable.Should().BeFalse($"{name} is required");
        }
    }

    [Fact]
    public void RationaleColumn_IsNullable()
    {
        using var db = InMemoryDbFixture.Create();
        var entityType = db.Model.FindEntityType(typeof(AuditEvent));
        var prop = entityType!.FindProperty(nameof(AuditEvent.Rationale));

        prop.Should().NotBeNull();
        prop!.IsNullable.Should().BeTrue();
    }

    [Fact]
    public void DbContext_ExposesAuditEventsDbSet()
    {
        using var db = InMemoryDbFixture.Create();
        // The compile would have caught a missing DbSet; this is the
        // runtime sentinel that the property is wired and queryable.
        db.AuditEvents.Should().NotBeNull();
    }
}
