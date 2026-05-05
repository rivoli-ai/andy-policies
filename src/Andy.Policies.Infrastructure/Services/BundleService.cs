// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Andy.Policies.Application.Exceptions;
using Andy.Policies.Application.Interfaces;
using Andy.Policies.Domain.Entities;
using Andy.Policies.Domain.Enums;
using Andy.Policies.Infrastructure.Data;
using Andy.Policies.Shared.Auditing;
using Microsoft.EntityFrameworkCore;

namespace Andy.Policies.Infrastructure.Services;

/// <summary>
/// Application-layer service for <c>Bundle</c> mutation and query (P8.2,
/// story rivoli-ai/andy-policies#82). Surfaces in P8.3 (REST), P8.5
/// (MCP), P8.6 (gRPC + CLI) all delegate here so the snapshot-build
/// + hash + audit-append discipline is uniform across the parity
/// surfaces.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reproducibility.</b> <see cref="CreateAsync"/> opens a
/// <see cref="IsolationLevel.Serializable"/> transaction so the catalog
/// view fed into the snapshot builder is frozen — a publish or
/// binding mutation that commits between the read and the bundle
/// insert cannot leak into the snapshot.
/// </para>
/// <para>
/// <b>Audit linkage.</b> The <c>bundle.create</c> event's
/// <c>FieldDiffJson</c> carries the snapshot hash and the parent
/// audit-tail-hash so the chain can be cross-walked: an auditor can
/// re-hash <c>SnapshotJson</c>, match it to the audit event payload,
/// and walk the chain back from there.
/// </para>
/// </remarks>
public sealed class BundleService : IBundleService
{
    /// <summary>Slug shape: lower-case alphanumeric, optional dashes,
    /// must start with [a-z0-9], 1..63 chars total. Mirrors the
    /// Policy.Name pattern.</summary>
    private static readonly Regex NameRegex = new(
        "^[a-z0-9][a-z0-9-]{0,62}$",
        RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly IBundleSnapshotBuilder _builder;
    private readonly IAuditChain _audit;
    private readonly TimeProvider _clock;

    public BundleService(
        AppDbContext db,
        IBundleSnapshotBuilder builder,
        IAuditChain audit,
        TimeProvider clock)
    {
        _db = db;
        _builder = builder;
        _audit = audit;
        _clock = clock;
    }

    public async Task<BundleDto> CreateAsync(
        CreateBundleRequest request, string actorSubjectId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(actorSubjectId);

        var name = (request.Name ?? string.Empty).Trim();
        if (!NameRegex.IsMatch(name))
        {
            throw new ValidationException(
                $"Bundle name '{name}' is not a valid slug. Expected pattern: ^[a-z0-9][a-z0-9-]{{0,62}}$.");
        }
        var rationale = (request.Rationale ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(rationale))
        {
            throw new ValidationException("Rationale is required and may not be empty or whitespace.");
        }

        // Honour an ambient transaction in tests (the InMemory
        // provider rejects BeginTransactionAsync). In production the
        // ambient is null and we own the serializable txn.
        var ambient = _db.Database.CurrentTransaction;
        var ownTxn = ambient is null && _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct).ConfigureAwait(false)
            : null;
        try
        {
            // Active-name uniqueness precheck. The DB filtered unique
            // index will also catch a race, but checking here lets us
            // throw a clean ConflictException rather than surface a
            // DbUpdateException with a constraint name in it.
            var nameTaken = await _db.Bundles
                .AsNoTracking()
                .AnyAsync(b => b.Name == name && b.State == BundleState.Active, ct)
                .ConfigureAwait(false);
            if (nameTaken)
            {
                throw new ConflictException(
                    $"Bundle name '{name}' is already in use by an active bundle.");
            }

            var capturedAt = _clock.GetUtcNow();
            var snapshot = await _builder.BuildAsync(capturedAt, ct).ConfigureAwait(false);

            var canonicalBytes = CanonicalJson.SerializeObject(snapshot);
            var snapshotJson = Encoding.UTF8.GetString(canonicalBytes);
            var snapshotHash = Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant();

            var bundle = new Bundle
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
                CreatedAt = capturedAt,
                CreatedBySubjectId = actorSubjectId,
                SnapshotJson = snapshotJson,
                SnapshotHash = snapshotHash,
                State = BundleState.Active,
            };
            _db.Bundles.Add(bundle);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            // Audit append after the bundle insert is committed in
            // memory (still inside the same txn). The chain stamps a
            // bundle.create event whose payload references both the
            // snapshot hash and the chain-tail-hash captured by the
            // builder — so an auditor can pivot in either direction.
            var fieldDiffJson = BuildFieldDiffJson(
                bundle.Id, snapshot.AuditTailHash, snapshotHash, snapshot);
            await _audit.AppendAsync(new AuditAppendRequest(
                Action: "bundle.create",
                EntityType: "Bundle",
                EntityId: bundle.Id.ToString(),
                FieldDiffJson: fieldDiffJson,
                Rationale: rationale,
                ActorSubjectId: actorSubjectId,
                ActorRoles: Array.Empty<string>()), ct).ConfigureAwait(false);

            if (ownTxn is not null)
            {
                await ownTxn.CommitAsync(ct).ConfigureAwait(false);
            }
            return Map(bundle);
        }
        finally
        {
            if (ownTxn is not null)
            {
                await ownTxn.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<BundleDto?> GetAsync(Guid bundleId, CancellationToken ct = default)
    {
        var bundle = await _db.Bundles
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == bundleId, ct)
            .ConfigureAwait(false);
        return bundle is null ? null : Map(bundle);
    }

    public async Task<IReadOnlyList<BundleDto>> ListAsync(
        ListBundlesFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var skip = Math.Max(0, filter.Skip);
        var take = Math.Clamp(filter.Take, 1, 500);

        IQueryable<Bundle> q = _db.Bundles.AsNoTracking();
        if (!filter.IncludeDeleted)
        {
            q = q.Where(b => b.State == BundleState.Active);
        }
        // SQLite cannot ORDER BY DateTimeOffset (same posture as the
        // OverrideExpiryReaper / PolicyService list paths). Pull the
        // filtered set, then order + page client-side. Bundle counts
        // are bounded in practice; if they grow large enough to
        // matter, a future migration would push the ordering via a
        // raw SQL CAST or a ix_bundles_created_at_iso column.
        var rows = await q
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows
            .OrderByDescending(b => b.CreatedAt)
            .ThenBy(b => b.Id)
            .Skip(skip)
            .Take(take)
            .Select(Map)
            .ToList();
    }

    public async Task<bool> SoftDeleteAsync(
        Guid bundleId, string actorSubjectId, string rationale, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(actorSubjectId);
        if (string.IsNullOrWhiteSpace(rationale))
        {
            throw new ValidationException("Rationale is required and may not be empty or whitespace.");
        }

        var bundle = await _db.Bundles
            .FirstOrDefaultAsync(b => b.Id == bundleId, ct)
            .ConfigureAwait(false);
        if (bundle is null || bundle.State != BundleState.Active)
        {
            // Idempotent: already-tombstoned or unknown ids return
            // false without writing an audit event.
            return false;
        }

        bundle.State = BundleState.Deleted;
        bundle.DeletedAt = _clock.GetUtcNow();
        bundle.DeletedBySubjectId = actorSubjectId;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await _audit.AppendAsync(new AuditAppendRequest(
            Action: "bundle.delete",
            EntityType: "Bundle",
            EntityId: bundle.Id.ToString(),
            FieldDiffJson: $"[{{\"op\":\"replace\",\"path\":\"/state\",\"value\":\"Deleted\"}}]",
            Rationale: rationale.Trim(),
            ActorSubjectId: actorSubjectId,
            ActorRoles: Array.Empty<string>()), ct).ConfigureAwait(false);
        return true;
    }

    private static string BuildFieldDiffJson(
        Guid bundleId,
        string auditTailHashHex,
        string snapshotHashHex,
        Domain.ValueObjects.BundleSnapshot snapshot)
    {
        // Hand-rolled JSON Patch (RFC 6902 'add') so the body is a
        // tiny dependency-free emit. The audit hash chain canonicalises
        // it on append; here we just need a string that a
        // FieldDiffJson column accepts and that downstream auditors
        // can introspect.
        var sb = new StringBuilder();
        sb.Append("[{\"op\":\"add\",\"path\":\"/bundle\",\"value\":{");
        sb.Append("\"id\":\"").Append(bundleId).Append("\",");
        sb.Append("\"snapshotHash\":\"").Append(snapshotHashHex).Append("\",");
        sb.Append("\"auditTailHash\":\"").Append(auditTailHashHex).Append("\",");
        sb.Append("\"policyCount\":").Append(snapshot.Policies.Count).Append(',');
        sb.Append("\"bindingCount\":").Append(snapshot.Bindings.Count).Append(',');
        sb.Append("\"overrideCount\":").Append(snapshot.Overrides.Count).Append(',');
        sb.Append("\"scopeCount\":").Append(snapshot.Scopes.Count);
        sb.Append("}}]");
        return sb.ToString();
    }

    private static BundleDto Map(Bundle b) => new(
        Id: b.Id,
        Name: b.Name,
        Description: b.Description,
        CreatedAt: b.CreatedAt,
        CreatedBySubjectId: b.CreatedBySubjectId,
        SnapshotHash: b.SnapshotHash,
        State: b.State.ToString(),
        DeletedAt: b.DeletedAt,
        DeletedBySubjectId: b.DeletedBySubjectId);
}
