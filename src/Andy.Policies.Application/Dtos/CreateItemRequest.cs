// Copyright (c) Rivoli AI 2026. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel.DataAnnotations;

namespace Andy.Policies.Application.Dtos;

public record CreateItemRequest(
    [Required] string Name,
    string? Description);
