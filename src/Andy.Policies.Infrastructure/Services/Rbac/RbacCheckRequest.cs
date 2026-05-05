// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Andy.Policies.Infrastructure.Services.Rbac;

internal sealed record RbacCheckRequest(
    string SubjectId,
    string Permission,
    IReadOnlyList<string> Groups,
    string? ResourceInstanceId);

internal sealed record RbacCheckResponse(bool Allowed, string Reason);
