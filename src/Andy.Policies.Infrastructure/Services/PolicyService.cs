// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using System.Text.RegularExpressions;
using Andy.Policies.Application.Dtos;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Application.Queries;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Domain.Validation;
using Andy.Policies.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Services;

public sealed partial class PolicyService : IPolicyService
{
    /// <summary>
    /// Canonical slug pattern for <see cref="Policy.Name"/>. Matches ADR 0001 §1:
    /// lowercase, starts with a letter or digit, 1-63 chars, only letters/digits/hyphens.
    /// </summary>
    public const string NameRegexPattern = "^[a-z0-9][a-z0-9-]{0,62}$";

    [GeneratedRegex(NameRegexPattern, RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    /// <summary>
    /// ADR 0001 §5 cap for the opaque rules DSL. Prevents DoS via oversized JSON blobs.
    /// </summary>
    public const int MaxRulesJsonBytes = 64 * 1024;

    /// <summary>Hard cap on page size returned by <see cref="ListPoliciesAsync"/>.</summary>
    public const int MaxPageSize = 500;

    private readonly AppDbContext _db;
    private readonly IAuditWriter? _audit;

    /// <param name="audit">Optional audit writer. Production DI wires
    /// <c>NoopAuditWriter</c> (P3.2) and the real hash-chained writer
    /// (P6.2). Tests that construct directly may pass <c>null</c> to
    /// skip audit emission — the 57 pre-existing direct-instantiation
    /// tests rely on that. New audit-aware tests inject a spy.</param>
    public PolicyService(AppDbContext db, IAuditWriter? audit = null)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<IReadOnlyList<PolicyDto>> ListPoliciesAsync(ListPoliciesQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var take = Math.Clamp(query.Take, 1, MaxPageSize);
        var skip = Math.Max(0, query.Skip);

        // Parse optional enum filters up-front so we fail fast on bad input.
        EnforcementLevel? enforcementFilter = ParseEnumOrThrow<EnforcementLevel>(query.Enforcement, "enforcement");
        Severity? severityFilter = ParseEnumOrThrow<Severity>(query.Severity, "severity");

        var policies = await _db.Policies
            .AsNoTracking()
            .Include(p => p.Versions)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        IEnumerable<Policy> filtered = policies;
        if (!string.IsNullOrEmpty(query.NamePrefix))
        {
            filtered = filtered.Where(p => p.Name.StartsWith(query.NamePrefix, StringComparison.Ordinal));
        }

        if (query.Scope is not null || enforcementFilter is not null || severityFilter is not null)
        {
            filtered = filtered.Where(p =>
            {
                var active = ResolveActive(p.Versions);
                if (active is null) return false;
                if (query.Scope is not null && !active.Scopes.Contains(query.Scope)) return false;
                if (enforcementFilter is not null && active.Enforcement != enforcementFilter.Value) return false;
                if (severityFilter is not null && active.Severity != severityFilter.Value) return false;
                return true;
            });
        }

        return filtered.Skip(skip).Take(take).Select(ToPolicyDto).ToList();
    }

    public async Task<PolicyDto?> GetPolicyAsync(Guid policyId, CancellationToken ct = default)
    {
        var policy = await _db.Policies
            .AsNoTracking()
            .Include(p => p.Versions)
            .FirstOrDefaultAsync(p => p.Id == policyId, ct);
        return policy is null ? null : ToPolicyDto(policy);
    }

    public async Task<PolicyDto?> GetPolicyByNameAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var policy = await _db.Policies
            .AsNoTracking()
            .Include(p => p.Versions)
            .FirstOrDefaultAsync(p => p.Name == name, ct);
        return policy is null ? null : ToPolicyDto(policy);
    }

    public async Task<IReadOnlyList<PolicyVersionDto>> ListVersionsAsync(Guid policyId, CancellationToken ct = default)
    {
        var versions = await _db.PolicyVersions
            .AsNoTracking()
            .Where(v => v.PolicyId == policyId)
            .OrderByDescending(v => v.Version)
            .ToListAsync(ct);
        return versions.Select(ToVersionDto).ToList();
    }

    public async Task<PolicyVersionDto?> GetVersionAsync(Guid policyId, Guid versionId, CancellationToken ct = default)
    {
        var version = await _db.PolicyVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.PolicyId == policyId && v.Id == versionId, ct);
        return version is null ? null : ToVersionDto(version);
    }

    public async Task<PolicyVersionDto?> GetActiveVersionAsync(Guid policyId, CancellationToken ct = default)
    {
        var active = await _db.PolicyVersions
            .AsNoTracking()
            .Where(v => v.PolicyId == policyId && v.State != LifecycleState.Draft)
            .OrderByDescending(v => v.Version)
            .FirstOrDefaultAsync(ct);
        return active is null ? null : ToVersionDto(active);
    }

    public async Task<PolicyVersionDto> CreateDraftAsync(CreatePolicyRequest request, string subjectId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(subjectId);

        var name = request.Name ?? string.Empty;
        if (!NameRegex().IsMatch(name))
        {
            throw new ValidationException(
                $"Policy name '{name}' is invalid. Expected pattern {NameRegexPattern}.");
        }

        var enforcement = ParseEnumOrThrow<EnforcementLevel>(request.Enforcement, "enforcement")
            ?? throw new ValidationException("enforcement is required.");
        var severity = ParseEnumOrThrow<Severity>(request.Severity, "severity")
            ?? throw new ValidationException("severity is required.");
        var scopes = CanonicaliseScopes(request.Scopes);
        ValidateRulesJson(request.RulesJson);

        // Single transaction spans the Policy + first PolicyVersion inserts so a failure
        // mid-flight rolls both rows back (atomic creation — P1.4 AC).
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        if (await _db.Policies.AnyAsync(p => p.Name == name, ct))
        {
            throw new ConflictException(
                $"Policy '{name}' already exists. Choose a different slug or bump the existing policy's version.");
        }

        var policy = new Policy
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = request.Description,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = subjectId,
        };
        var version = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            PolicyId = policy.Id,
            Version = 1,
            State = LifecycleState.Draft,
            Enforcement = enforcement,
            Severity = severity,
            Scopes = scopes.ToList(),
            Summary = request.Summary ?? string.Empty,
            RulesJson = request.RulesJson ?? "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = subjectId,
            ProposerSubjectId = subjectId,
        };

        _db.Policies.Add(policy);
        _db.PolicyVersions.Add(version);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Audit append after commit — matches BindingService.CreateAsync's
        // pattern. NoopAuditWriter today; the P6.2 real writer will need
        // to fold this into the same transaction when it lands.
        if (_audit is not null)
        {
            await _audit.AppendAsync(
                "policy.draft.created", policy.Id, subjectId, request.Rationale, ct)
                .ConfigureAwait(false);
        }

        return ToVersionDto(version);
    }

    public async Task<PolicyVersionDto> UpdateDraftAsync(Guid policyId, Guid versionId, UpdatePolicyVersionRequest request, string subjectId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(subjectId);

        var version = await _db.PolicyVersions.FirstOrDefaultAsync(
            v => v.PolicyId == policyId && v.Id == versionId, ct)
            ?? throw new NotFoundException($"PolicyVersion {versionId} not found under policy {policyId}.");

        // Service-level guard mirrors the domain MutateDraftField + the AppDbContext guard;
        // rejecting here yields a clearer error message than waiting for SaveChangesAsync.
        // Surfaced as ConflictException so the REST/MCP/gRPC layers map to 409 — wrong-state
        // mutation is a concurrency-style conflict, not a malformed request.
        if (version.State != LifecycleState.Draft)
        {
            throw new ConflictException(
                $"PolicyVersion {version.Id} is in state {version.State}; only Draft versions are mutable.");
        }

        var enforcement = ParseEnumOrThrow<EnforcementLevel>(request.Enforcement, "enforcement")
            ?? throw new ValidationException("enforcement is required.");
        var severity = ParseEnumOrThrow<Severity>(request.Severity, "severity")
            ?? throw new ValidationException("severity is required.");
        var scopes = CanonicaliseScopes(request.Scopes);
        ValidateRulesJson(request.RulesJson);

        version.MutateDraftField(() =>
        {
            version.Summary = request.Summary ?? string.Empty;
            version.Enforcement = enforcement;
            version.Severity = severity;
            version.Scopes = scopes.ToList();
            version.RulesJson = request.RulesJson ?? "{}";
        });

        // EF Core will raise DbUpdateConcurrencyException if Revision has advanced between
        // the load above and this save — the API layer (P1.5) maps that to 412 Precondition
        // Failed.
        await _db.SaveChangesAsync(ct);

        if (_audit is not null)
        {
            await _audit.AppendAsync(
                "policy.draft.updated", version.Id, subjectId, request.Rationale, ct)
                .ConfigureAwait(false);
        }

        return ToVersionDto(version);
    }

    public async Task<PolicyVersionDto> BumpDraftFromVersionAsync(Guid policyId, Guid sourceVersionId, string subjectId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectId);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var policy = await _db.Policies.FirstOrDefaultAsync(p => p.Id == policyId, ct)
            ?? throw new NotFoundException($"Policy {policyId} not found.");

        var source = await _db.PolicyVersions.AsNoTracking().FirstOrDefaultAsync(
            v => v.PolicyId == policyId && v.Id == sourceVersionId, ct)
            ?? throw new NotFoundException(
                $"Source version {sourceVersionId} not found under policy {policyId}.");

        // ADR 0001 §4: only one open Draft per policy at a time.
        var existingDraft = await _db.PolicyVersions.AsNoTracking().FirstOrDefaultAsync(
            v => v.PolicyId == policyId && v.State == LifecycleState.Draft, ct);
        if (existingDraft is not null)
        {
            throw new ConflictException(
                $"Policy '{policy.Name}' already has an open draft v{existingDraft.Version}.");
        }

        var maxVersion = await _db.PolicyVersions
            .Where(v => v.PolicyId == policyId)
            .MaxAsync(v => v.Version, ct);

        var next = new PolicyVersion
        {
            Id = Guid.NewGuid(),
            PolicyId = policyId,
            Version = maxVersion + 1,
            State = LifecycleState.Draft,
            Enforcement = source.Enforcement,
            Severity = source.Severity,
            Scopes = source.Scopes.ToList(),
            Summary = source.Summary,
            RulesJson = source.RulesJson,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBySubjectId = subjectId,
            ProposerSubjectId = subjectId,
        };

        _db.PolicyVersions.Add(next);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ToVersionDto(next);
    }

    // ---- helpers ------------------------------------------------------------

    private static PolicyVersion? ResolveActive(IEnumerable<PolicyVersion> versions)
    {
        // P1 rule: "highest Version with State != Draft". P2 tightens to State == Active
        // via its own transition service; the resolver here stays compatible with both.
        return versions
            .Where(v => v.State != LifecycleState.Draft)
            .OrderByDescending(v => v.Version)
            .FirstOrDefault();
    }

    private static PolicyDto ToPolicyDto(Policy p)
    {
        var active = ResolveActive(p.Versions);
        return new PolicyDto(
            p.Id,
            p.Name,
            p.Description,
            p.CreatedAt,
            p.CreatedBySubjectId,
            p.Versions.Count,
            active?.Id);
    }

    private static PolicyVersionDto ToVersionDto(PolicyVersion v) => new(
        v.Id,
        v.PolicyId,
        v.Version,
        v.State.ToString(),
        ToEnforcementWire(v.Enforcement),
        ToSeverityWire(v.Severity),
        v.Scopes.ToArray(),
        v.Summary,
        v.RulesJson,
        v.CreatedAt,
        v.CreatedBySubjectId,
        v.ProposerSubjectId);

    /// <summary>ADR 0001 §6: uppercase RFC 2119 tokens on the wire.</summary>
    private static string ToEnforcementWire(EnforcementLevel level) => level switch
    {
        EnforcementLevel.May => "MAY",
        EnforcementLevel.Should => "SHOULD",
        EnforcementLevel.Must => "MUST",
        _ => throw new InvalidOperationException($"Unknown EnforcementLevel: {level}"),
    };

    /// <summary>ADR 0001 §6: lowercase severity tokens on the wire.</summary>
    private static string ToSeverityWire(Severity severity) => severity switch
    {
        Severity.Info => "info",
        Severity.Moderate => "moderate",
        Severity.Critical => "critical",
        _ => throw new InvalidOperationException($"Unknown Severity: {severity}"),
    };

    private static TEnum? ParseEnumOrThrow<TEnum>(string? value, string fieldName) where TEnum : struct, Enum
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
        {
            if (Enum.IsDefined(parsed)) return parsed;
        }
        var allowed = string.Join(", ", Enum.GetNames<TEnum>());
        throw new ValidationException(
            $"{fieldName} '{value}' is not a recognised value. Allowed (case-insensitive): {allowed}.");
    }

    private static IReadOnlyList<string> CanonicaliseScopes(IEnumerable<string>? scopes)
    {
        var input = scopes ?? Array.Empty<string>();
        try
        {
            return PolicyScope.Canonicalise(input);
        }
        catch (ArgumentException ex)
        {
            throw new ValidationException(ex.Message, ex);
        }
    }

    private static void ValidateRulesJson(string? rulesJson)
    {
        var payload = rulesJson ?? "{}";
        var bytes = System.Text.Encoding.UTF8.GetByteCount(payload);
        if (bytes > MaxRulesJsonBytes)
        {
            throw new ValidationException(
                $"RulesJson is {bytes} bytes; the cap is {MaxRulesJsonBytes} bytes (ADR 0001 §5).");
        }

        try
        {
            using var _ = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new ValidationException($"RulesJson is not valid JSON: {ex.Message}", ex);
        }
    }
}
