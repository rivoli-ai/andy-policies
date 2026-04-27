// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Application.Events;

/// <summary>
/// Emitted in-process by <see cref="Interfaces.ILifecycleTransitionService"/>
/// after a successful publish (Draft -> Active) commit. P6 (audit), P8 (bundle
/// snapshot), and any local listeners subscribe; cross-service signalling is
/// the consumer's responsibility (see Epic P2 non-goals).
/// </summary>
public sealed record PolicyVersionPublished(
    Guid PolicyId,
    Guid VersionId,
    int Version,
    string ActorSubjectId,
    string Rationale,
    DateTimeOffset At);

/// <summary>
/// Emitted in-process when a publish auto-supersedes the previous Active
/// version of the same policy. Always fires *before* the matching
/// <see cref="PolicyVersionPublished"/> event for the new active version,
/// so subscribers can build the (old, new) pair atomically.
/// </summary>
public sealed record PolicyVersionSuperseded(
    Guid PolicyId,
    Guid PreviousVersionId,
    Guid NewActiveVersionId,
    DateTimeOffset At);

/// <summary>
/// Emitted in-process when a version transitions to
/// <see cref="Domain.Enums.LifecycleState.Retired"/>. Both
/// Active -> Retired and WindingDown -> Retired paths emit this.
/// </summary>
public sealed record PolicyVersionRetired(
    Guid PolicyId,
    Guid VersionId,
    string ActorSubjectId,
    string Rationale,
    DateTimeOffset At);
