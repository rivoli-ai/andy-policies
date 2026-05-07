// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.ValueObjects;
using Andy.Policies.Infrastructure.Services;
using Andy.Policies.Tests.Unit.Fixtures;
using FluentAssertions;
using Xunit;

namespace Andy.Policies.Tests.Unit.Services;

/// <summary>
/// Pure validation paths on <see cref="BundleService.CreateAsync"/> —
/// the slug regex + the rationale-required guard. These reject before
/// the service touches the catalog or the audit chain, so a stub
/// builder that throws on Build proves the precondition fired first.
/// P8.2 (#82).
/// </summary>
public class BundleServiceValidationTests
{
    private sealed class ThrowingBuilder : IBundleSnapshotBuilder
    {
        public Task<BundleSnapshot> BuildAsync(
            DateTimeOffset capturedAt,
            bool includeOverrides = true,
            CancellationToken ct = default)
            => throw new InvalidOperationException(
                "BundleSnapshotBuilder must not be invoked when validation fails.");
    }

    private sealed class ThrowingAudit : IAuditChain
    {
        public Task<AuditEventDto> AppendAsync(AuditAppendRequest request, CancellationToken ct)
            => throw new InvalidOperationException(
                "Audit chain must not be invoked when validation fails.");
        public Task<ChainVerificationResult> VerifyChainAsync(
            long? fromSeq, long? toSeq, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static BundleService NewService() => new(
        InMemoryDbFixture.Create(),
        new ThrowingBuilder(),
        new ThrowingAudit(),
        TimeProvider.System);

    [Theory]
    [InlineData("Bad-Capital")]       // uppercase
    [InlineData("-leading-dash")]     // first char must be [a-z0-9]
    [InlineData("under_score")]       // underscore not in [a-z0-9-]
    [InlineData("dot.notation")]      // dot not in [a-z0-9-]
    [InlineData("with space")]        // space not allowed
    [InlineData("")]                  // empty
    public async Task Create_RejectsInvalidSlug_WithValidationException(string name)
    {
        var svc = NewService();
        var request = new CreateBundleRequest(name, "desc", "rationale");

        var act = async () => await svc.CreateAsync(request, "actor", CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(ex => ex.Message.Contains("slug", StringComparison.OrdinalIgnoreCase),
                $"name '{name}' must be rejected with a slug-shape error");
    }

    [Fact]
    public async Task Create_RejectsEmptyRationale_WithValidationException()
    {
        var svc = NewService();
        var request = new CreateBundleRequest("valid-slug", "desc", "   ");

        var act = async () => await svc.CreateAsync(request, "actor", CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>()
            .Where(ex => ex.Message.Contains("Rationale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Create_RejectsEmptyActorSubjectId_WithArgumentException()
    {
        var svc = NewService();
        var request = new CreateBundleRequest("valid", "desc", "rationale");

        var act = async () => await svc.CreateAsync(request, string.Empty, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
