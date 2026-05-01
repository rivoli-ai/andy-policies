// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Andy.Policies.Shared.Auditing;

/// <summary>
/// Pure SHA-256 hasher for the audit envelope (P6.2 chain
/// linkage; P6.5 offline verification). Lives in Shared so the
/// CLI's offline verifier (P6.5) and the Infrastructure
/// <c>AuditChain</c> writer compute identical hashes for
/// identical inputs — without the CLI inheriting an EF / Npgsql
/// dependency.
/// </summary>
/// <remarks>
/// <para>
/// <b>Envelope shape.</b> The canonicalised JSON is the closed
/// catalog audit envelope: <c>action</c>, <c>actorRoles</c> (sorted
/// lex order), <c>actorSubjectId</c>, <c>entityId</c>,
/// <c>entityType</c>, <c>fieldDiff</c> (parsed JSON, not a string),
/// <c>id</c>, <c>rationale</c> (null permitted), <c>timestamp</c>
/// (ISO 8601 with millisecond precision, always UTC). The
/// canonicaliser sorts keys lex order at every level; any
/// re-shaping of this envelope must be paired with a chain-
/// migration story (today there is none — the chain is append-only
/// and the schema is pinned).
/// </para>
/// <para>
/// <b>Genesis.</b> The first row's <c>prevHash</c> is 32 zero
/// bytes. Both the writer (AuditChain.AppendCoreAsync) and the
/// offline verifier seed the chain with that constant.
/// </para>
/// </remarks>
public static class AuditEnvelopeHasher
{
    /// <summary>
    /// Computes <c>SHA-256(prevHash || canonicalJson(envelope))</c>.
    /// </summary>
    /// <param name="prevHash">32-byte SHA-256 of the previous
    /// row's hash (or 32 zero bytes for genesis).</param>
    /// <param name="id">The event's stable random GUID.</param>
    /// <param name="timestamp">UTC instant the event was
    /// written; serialised at millisecond precision.</param>
    /// <param name="actorSubjectId">JWT <c>sub</c> claim of the
    /// actor.</param>
    /// <param name="actorRoles">RBAC roles snapshot. Sorted lex
    /// order before hashing so iteration order doesn't change
    /// the hash.</param>
    /// <param name="action">Dotted action code.</param>
    /// <param name="entityType">Canonical type of the mutated
    /// entity.</param>
    /// <param name="entityId">String form of the mutated row's
    /// primary key.</param>
    /// <param name="fieldDiffJson">RFC 6902 JSON Patch document.
    /// Embedded as parsed JSON (not as a string) so canonical
    /// re-serialisation kicks in.</param>
    /// <param name="rationale">Free-text rationale; null is
    /// permitted (the rationale-required toggle is enforced
    /// elsewhere).</param>
    /// <returns>32-byte SHA-256 output.</returns>
    public static byte[] ComputeHash(
        byte[] prevHash,
        Guid id,
        DateTimeOffset timestamp,
        string actorSubjectId,
        IReadOnlyList<string> actorRoles,
        string action,
        string entityType,
        string entityId,
        string fieldDiffJson,
        string? rationale)
    {
        using var diffDoc = JsonDocument.Parse(string.IsNullOrEmpty(fieldDiffJson) ? "[]" : fieldDiffJson);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("action", action);
            writer.WritePropertyName("actorRoles");
            writer.WriteStartArray();
            foreach (var r in actorRoles.OrderBy(s => s, StringComparer.Ordinal))
            {
                writer.WriteStringValue(r);
            }
            writer.WriteEndArray();
            writer.WriteString("actorSubjectId", actorSubjectId);
            writer.WriteString("entityId", entityId);
            writer.WriteString("entityType", entityType);
            writer.WritePropertyName("fieldDiff");
            diffDoc.RootElement.WriteTo(writer);
            writer.WriteString("id", id.ToString());
            if (rationale is null)
            {
                writer.WriteNull("rationale");
            }
            else
            {
                writer.WriteString("rationale", rationale);
            }
            writer.WriteString("timestamp",
                timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }
        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        var canonical = CanonicalJson.Serialize(doc.RootElement);

        using var sha = SHA256.Create();
        sha.TransformBlock(prevHash, 0, prevHash.Length, null, 0);
        sha.TransformFinalBlock(canonical, 0, canonical.Length);
        return sha.Hash!;
    }
}
