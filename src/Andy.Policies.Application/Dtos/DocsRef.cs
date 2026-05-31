// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Dtos;

/// <summary>
/// Cross-service handle returned by andy-docs <c>docs.put</c>
/// (<c>POST /api/documents:put</c>) — see andy-docs
/// <c>docs/docs-ref-contract.md</c>. <see cref="DocumentId"/> names the
/// stored artifact; <see cref="LinkId"/> names the <em>first</em>
/// <c>DocumentLink</c> attaching that artifact to a target. Any extra
/// links requested in the same upload are listed in
/// <see cref="AdditionalLinkIds"/>.
/// </summary>
/// <remarks>
/// This is the join key Conductor (rivoli-ai/conductor#1946) uses to
/// discover the compliance/audit document for a goal/run/task via the
/// by-target query <c>GET /api/links?targetType=Goal&amp;targetId=…&amp;role=Audit</c>.
/// </remarks>
/// <param name="DocumentId">andy-docs <c>Document.Id</c> (GUID).</param>
/// <param name="LinkId">andy-docs <c>DocumentLink.Id</c> of the first link (GUID).</param>
/// <param name="VersionHash">Content hash of the stored version (bookkeeping).</param>
/// <param name="SizeBytes">Stored size in bytes (bookkeeping).</param>
/// <param name="AdditionalLinkIds">Link ids beyond the first, in request order.</param>
public sealed record DocsRef(
    Guid DocumentId,
    Guid LinkId,
    string? VersionHash,
    long SizeBytes,
    IReadOnlyList<Guid> AdditionalLinkIds);

/// <summary>
/// Target kinds an andy-docs <c>DocumentLink</c> can point at, per the
/// closed enum in <c>docs-ref-contract.md</c>. Wire form is the PascalCase
/// member name (<c>Issue</c>/<c>Task</c>/<c>Run</c>/<c>Goal</c>).
/// </summary>
public enum DocsLinkTargetType
{
    /// <summary>rivoli-ai/andy-issues issue.</summary>
    Issue = 1,

    /// <summary>rivoli-ai/andy-tasks task.</summary>
    Task = 2,

    /// <summary>rivoli-ai/conductor run (GUID).</summary>
    Run = 3,

    /// <summary>goal (GUID).</summary>
    Goal = 4,
}

/// <summary>
/// One link to attach to an uploaded document. Idempotent on
/// <c>(documentId, targetType, targetId, role)</c> at andy-docs, so
/// re-uploading the same compliance assessment for the same
/// <c>(goal, planVersion)</c> re-attaches rather than duplicating.
/// </summary>
/// <param name="TargetType">The kind of record this document is about.</param>
/// <param name="TargetId">
/// The opaque target id (GUID string for Goal/Run, external id for
/// Issue/Task). andy-docs does not validate it exists — the trust
/// boundary stays with andy-policies.
/// </param>
/// <param name="Role">
/// The attachment role. andy-policies always writes <c>Audit</c> here.
/// </param>
public sealed record DocsLink(
    DocsLinkTargetType TargetType,
    string TargetId,
    string Role);

/// <summary>
/// A single <c>docs.put</c> request: the file bytes + MIME, a name/title,
/// and the links to attach. Shaped to the andy-docs multipart contract
/// (<c>docs/agent-upload.md</c>): <c>file</c> part carries the bytes with
/// the MIME as its content type; <c>meta</c> part carries the JSON
/// <c>{ name, title, message, links }</c>.
/// </summary>
/// <param name="FileName">Filename for the <c>file</c> part (e.g. <c>compliance-assessment.json</c>).</param>
/// <param name="MimeType">Document MIME — must be on the andy-docs allowlist.</param>
/// <param name="Content">The artifact bytes.</param>
/// <param name="Title">Human-readable document title.</param>
/// <param name="Message">Commit-style note.</param>
/// <param name="Links">At least one link; andy-policies attaches <c>role:Audit</c>.</param>
public sealed record DocsPutRequest(
    string FileName,
    string MimeType,
    byte[] Content,
    string Title,
    string Message,
    IReadOnlyList<DocsLink> Links);
