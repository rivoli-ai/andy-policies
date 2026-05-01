// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Interfaces;

/// <summary>
/// Field-level diff producer for the catalog audit chain (P6.3,
/// story rivoli-ai/andy-policies#43). Every mutating service
/// (Policy, PolicyVersion, Binding, Override, Scope, Bundle)
/// snapshots its DTO before and after the mutation and calls
/// <see cref="GenerateJsonPatch"/>; the resulting RFC 6902 JSON
/// Patch is embedded verbatim in
/// <c>AuditEvent.FieldDiffJson</c> and contributes to the chain
/// hash, making the diff itself tamper-evident.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why RFC 6902?</b> Compliance officers and external auditors
/// need to read the diff in a standardised format. RFC 6902 has
/// IETF-published semantics and is consumable by <c>jsonpatch</c>
/// tooling in every language. The proprietary "before/after"
/// shape would triple row size and make "did this field change
/// between T0 and T1?" require client-side comparison.
/// </para>
/// <para>
/// <b>Decoration contract.</b> Properties on the DTO type may
/// carry <c>[AuditIgnore]</c> (drops the property from the diff
/// entirely) or <c>[AuditRedact]</c> (replaces <c>value</c> with
/// <c>"***"</c> in <c>add</c>/<c>replace</c> ops). Both attributes
/// live in <c>Andy.Policies.Shared.Auditing</c>.
/// </para>
/// <para>
/// <b>Stability.</b> The implementation must be byte-stable: two
/// invocations on equal inputs must produce byte-identical JSON.
/// The hash chain (P6.2) depends on this — a non-stable diff
/// would corrupt every chain ever written.
/// </para>
/// </remarks>
public interface IAuditDiffGenerator
{
    /// <summary>
    /// Produces an RFC 6902 JSON Patch document mapping
    /// <paramref name="before"/> to <paramref name="after"/>.
    /// Either side may be <c>null</c> (create / delete).
    /// </summary>
    /// <typeparam name="T">DTO type. The implementation
    /// reflects over the public readable properties of this
    /// type.</typeparam>
    /// <param name="before">Snapshot before the mutation. Null
    /// for create-style events.</param>
    /// <param name="after">Snapshot after the mutation. Null
    /// for delete-style events.</param>
    /// <returns>JSON array of patch ops. Returns <c>"[]"</c>
    /// when nothing audit-relevant changed.</returns>
    string GenerateJsonPatch<T>(T? before, T? after) where T : class;
}
