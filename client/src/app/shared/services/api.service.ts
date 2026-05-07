// Copyright (c) Rivoli AI 2026. All rights reserved.

import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';

/** Lifecycle states match `Andy.Policies.Domain.Enums.LifecycleState` (PascalCase wire). */
export type LifecycleState = 'Draft' | 'Active' | 'WindingDown' | 'Retired';

/** RFC 2119 enforcement tokens — uppercase per ADR 0001 §6. */
export type Enforcement = 'MUST' | 'SHOULD' | 'MAY';

/** Severity tier — lowercase per ADR 0001 §6. */
export type Severity = 'info' | 'moderate' | 'critical';

/** Wire shape for `GET /api/policies` rows (matches `PolicyDto`). */
export interface PolicyDto {
  id: string;
  name: string;
  description: string | null;
  createdAt: string;
  createdBySubjectId: string;
  versionCount: number;
  activeVersionId: string | null;
}

/** Wire shape matching `PolicyVersionDto`. Enforcement uppercase RFC 2119,
 *  severity lowercase, state PascalCase per ADR 0001 §6. */
export interface PolicyVersionDto {
  id: string;
  policyId: string;
  version: number;
  state: LifecycleState;
  enforcement: Enforcement;
  severity: Severity;
  scopes: string[];
  summary: string;
  rulesJson: string;
  createdAt: string;
  createdBySubjectId: string;
  proposerSubjectId: string;
}

/** Body for `POST /api/policies` (create policy + first draft version). */
export interface CreatePolicyRequest {
  name: string;
  description?: string | null;
  summary: string;
  enforcement: Enforcement;
  severity: Severity;
  scopes: string[];
  rulesJson: string;
}

/** Body for `PUT /api/policies/{id}/versions/{versionId}` (update existing draft). */
export interface UpdatePolicyVersionRequest {
  summary: string;
  enforcement: Enforcement;
  severity: Severity;
  scopes: string[];
  rulesJson: string;
}

/** Body for the lifecycle transition endpoints (publish / winding-down / retire). */
export interface LifecycleTransitionBody {
  rationale: string;
}

/**
 * Maps a target lifecycle state to the action-shaped path segment used by
 * `PolicyVersionsLifecycleController`. `Draft` is intentionally null —
 * versions are born Draft, never transitioned to it.
 */
const TRANSITION_PATH_SEGMENTS: Record<LifecycleState, string | null> = {
  Active: 'publish',
  WindingDown: 'winding-down',
  Retired: 'retire',
  Draft: null,
};

/** Filters accepted by `GET /api/policies`. The server has no full-text search;
 *  `namePrefix` does prefix matching only. No `state=` filter exists; per-row
 *  `activeVersionId == null` means "all versions are still draft" client-side. */
export interface PolicyQuery {
  namePrefix?: string;
  scope?: string;
  enforcement?: Enforcement;
  severity?: Severity;
  skip?: number;
  take?: number;
  bundleId?: string | null;
}

/** Wire shape for `GET /api/bundles` rows (matches `BundleDto`). */
export interface BundleDto {
  id: string;
  name: string;
  description: string | null;
  createdAt: string;
  createdBySubjectId: string;
  snapshotHash: string;
  state: string;
  deletedAt: string | null;
  deletedBySubjectId: string | null;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private baseUrl = environment.apiUrl;

  constructor(private http: HttpClient) {}

  // --- Policies (P9.1, #66) ---

  listPolicies(query: PolicyQuery = {}): Observable<PolicyDto[]> {
    let params = new HttpParams();
    if (query.namePrefix)  params = params.set('namePrefix', query.namePrefix);
    if (query.scope)       params = params.set('scope', query.scope);
    if (query.enforcement) params = params.set('enforcement', query.enforcement);
    if (query.severity)    params = params.set('severity', query.severity);
    if (query.skip != null) params = params.set('skip', query.skip.toString());
    if (query.take != null) params = params.set('take', query.take.toString());
    if (query.bundleId)    params = params.set('bundleId', query.bundleId);
    return this.http.get<PolicyDto[]>(`${this.baseUrl}/policies`, { params });
  }

  /** P9.2 (#67) — fetch a single policy (header data: name, description, version count). */
  getPolicy(id: string): Observable<PolicyDto> {
    return this.http.get<PolicyDto>(`${this.baseUrl}/policies/${id}`);
  }

  /** P9.2 (#67) — fetch a specific version (rules + dimensions for editing). */
  getPolicyVersion(id: string, versionId: string): Observable<PolicyVersionDto> {
    return this.http.get<PolicyVersionDto>(
      `${this.baseUrl}/policies/${id}/versions/${versionId}`,
    );
  }

  /** P9.2 (#67) — create policy + first draft version in one shot. */
  createPolicy(request: CreatePolicyRequest): Observable<PolicyVersionDto> {
    return this.http.post<PolicyVersionDto>(`${this.baseUrl}/policies`, request);
  }

  /** P9.2 (#67) — update an existing draft version (state must be Draft server-side). */
  updatePolicyVersion(
    id: string,
    versionId: string,
    request: UpdatePolicyVersionRequest,
  ): Observable<PolicyVersionDto> {
    return this.http.put<PolicyVersionDto>(
      `${this.baseUrl}/policies/${id}/versions/${versionId}`,
      request,
    );
  }

  /** P9.4 (#69) — list all versions of a policy. */
  listPolicyVersions(id: string): Observable<PolicyVersionDto[]> {
    return this.http.get<PolicyVersionDto[]>(
      `${this.baseUrl}/policies/${id}/versions`,
    );
  }

  /**
   * P9.4 (#69) — transition a version to a new lifecycle state.
   * Routes to the action-shaped endpoint that the server exposes
   * (`publish` / `winding-down` / `retire`). The server is the
   * authoritative gate for legality — illegal transitions return 409
   * regardless of what the client requested.
   */
  transitionPolicyVersion(
    id: string,
    versionId: string,
    targetState: LifecycleState,
    rationale: string,
  ): Observable<PolicyVersionDto> {
    const segment = TRANSITION_PATH_SEGMENTS[targetState];
    if (!segment) {
      throw new Error(`No endpoint exists for transition to '${targetState}'.`);
    }
    return this.http.post<PolicyVersionDto>(
      `${this.baseUrl}/policies/${id}/versions/${versionId}/${segment}`,
      { rationale },
    );
  }

  // --- Bundles (P8.3) ---

  listBundles(includeDeleted = false): Observable<BundleDto[]> {
    let params = new HttpParams();
    if (includeDeleted) params = params.set('includeDeleted', 'true');
    return this.http.get<BundleDto[]>(`${this.baseUrl}/bundles`, { params });
  }
}
