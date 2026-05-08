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
 *  severity lowercase, state PascalCase per ADR 0001 §6.
 *  `revision` (#194) round-trips as an optimistic-concurrency token —
 *  callers preserve it across edit and pass it back via
 *  `expectedRevision` on publish/update so a stale tab gets a 412
 *  instead of overwriting concurrent work.
 *  `publisherSubjectId` (#216) is null until the version transitions to
 *  Active; populated on the same write that flips State.
 *  `readyForReview` (#216) is the author-driven "this draft is ready
 *  for an approver" handoff signal — only meaningful while
 *  `state === 'Draft'`. */
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
  revision?: number;
  publisherSubjectId?: string | null;
  readyForReview?: boolean;
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

/** Body for the lifecycle transition endpoints (publish / winding-down / retire).
 *  `expectedRevision` (#194) is optional optimistic-concurrency: when supplied,
 *  the server returns 412 if the version's revision has advanced (e.g. a
 *  concurrent edit landed between inbox load and approve). */
export interface LifecycleTransitionBody {
  rationale: string;
  expectedRevision?: number;
}

/** P9.5 (#70) — bind strength enum, matches `Andy.Policies.Domain.Enums.BindStrength`. */
export type BindStrength = 'Mandatory' | 'Recommended';

/**
 * P9.5 (#70) — what kind of foreign target a `Binding` attaches to.
 * Mirrors `Andy.Policies.Domain.Enums.BindingTargetType` (1..5).
 */
export type BindingTargetType =
  | 'Template'
  | 'Repo'
  | 'ScopeNode'
  | 'Tenant'
  | 'Org';

export const BINDING_TARGET_TYPES: BindingTargetType[] = [
  'Template',
  'Repo',
  'ScopeNode',
  'Tenant',
  'Org',
];

/** Wire shape matching `BindingDto`. `DeletedAt` is non-null for soft-deleted rows. */
export interface BindingDto {
  id: string;
  policyVersionId: string;
  targetType: BindingTargetType;
  targetRef: string;
  bindStrength: BindStrength;
  createdAt: string;
  createdBySubjectId: string;
  deletedAt: string | null;
  deletedBySubjectId: string | null;
}

/** Body for `POST /api/bindings`. Server has no `Rationale` field on create
 *  today — see follow-up issue. */
export interface CreateBindingRequest {
  policyVersionId: string;
  targetType: BindingTargetType;
  targetRef: string;
  bindStrength: BindStrength;
}

/**
 * P9.6 (#88) — Override lifecycle states. Reject (#201) added in PR #213:
 * `Proposed` can now also terminate as `Rejected` (distinct from `Revoked`,
 * which fires from `Approved`). The audit chain distinguishes the two.
 */
export type OverrideState = 'Proposed' | 'Approved' | 'Revoked' | 'Expired' | 'Rejected';

/** Mirrors `Andy.Policies.Domain.Enums.OverrideScopeKind`. */
export type OverrideScopeKind = 'Principal' | 'Cohort';

/** Mirrors `Andy.Policies.Domain.Enums.OverrideEffect`. `Replace` requires a
 *  non-null `replacementPolicyVersionId`; the DB enforces a CHECK constraint. */
export type OverrideEffect = 'Exempt' | 'Replace';

/** Wire shape for `OverrideDto`. ScopeRef format depends on `scopeKind`
 *  (e.g. principal subject id, cohort name). */
export interface OverrideDto {
  id: string;
  policyVersionId: string;
  scopeKind: OverrideScopeKind;
  scopeRef: string;
  effect: OverrideEffect;
  replacementPolicyVersionId: string | null;
  proposerSubjectId: string;
  approverSubjectId: string | null;
  state: OverrideState;
  proposedAt: string;
  approvedAt: string | null;
  expiresAt: string;
  rationale: string;
  revocationReason: string | null;
}

/** Filters accepted by `GET /api/overrides`. */
export interface OverrideListQuery {
  state?: OverrideState;
  scopeKind?: OverrideScopeKind;
  scopeRef?: string;
  policyVersionId?: string;
}

/** Body for `POST /api/overrides/{id}/revoke` — note the field is
 *  `revocationReason`, not `rationale`. */
export interface RevokeOverrideRequest {
  revocationReason: string;
}

/** Body for `POST /api/overrides`. Server enforces the Effect ↔
 *  ReplacementPolicyVersionId invariant: Replace requires non-null,
 *  Exempt requires null (mirrored client-side in the propose modal
 *  for fast feedback). `expiresAt` must be at least 1 minute in the
 *  future per the server's MinimumLifetime check. */
export interface ProposeOverrideRequest {
  policyVersionId: string;
  scopeKind: OverrideScopeKind;
  scopeRef: string;
  effect: OverrideEffect;
  replacementPolicyVersionId: string | null;
  expiresAt: string;
  rationale: string;
}

/** Subset of RFC 6902 operations we expect in `AuditEventDto.fieldDiff`. */
export interface Rfc6902Op {
  op: 'add' | 'remove' | 'replace' | 'move' | 'copy' | 'test';
  path: string;
  value?: unknown;
  from?: string;
}

/**
 * P9.7 (#89) — wire shape for `AuditEventDto`. Note `fieldDiff` arrives
 * already parsed (server emits it as a JSON array, not a stringified blob)
 * so client-side rendering can iterate operations directly. `prevHashHex`
 * + `hashHex` are hex-encoded SHA-256 strings.
 */
export interface AuditEventDto {
  id: string;
  seq: number;
  prevHashHex: string;
  hashHex: string;
  timestamp: string;
  actorSubjectId: string;
  actorRoles: string[];
  action: string;
  entityType: string;
  entityId: string;
  fieldDiff: Rfc6902Op[];
  rationale: string | null;
}

/** Page envelope returned by `GET /api/audit`. Cursor-based: pass
 *  `nextCursor` back as `cursor` to fetch the next page. */
export interface AuditPageDto {
  items: AuditEventDto[];
  nextCursor: string | null;
  pageSize: number;
}

/** Filters accepted by `GET /api/audit`. `from`/`to` are timestamps,
 *  `cursor` is the opaque token from a previous page's `nextCursor`. */
export interface AuditQuery {
  actor?: string;
  action?: string;
  entityType?: string;
  entityId?: string;
  from?: string;
  to?: string;
  cursor?: string | null;
  pageSize?: number;
}

/** Result of `GET /api/audit/verify`. `valid === true` ⇔
 *  `firstDivergenceSeq === null`. `lastSeq` is the highest seq inspected
 *  (or 0 if the chain is empty). */
export interface ChainVerificationDto {
  valid: boolean;
  firstDivergenceSeq: number | null;
  inspectedCount: number;
  lastSeq: number;
}

/** P9.8 (#90) — body for `POST /api/bundles`. Server's CreateBundleRequest
 *  has no `includeOverrides` flag; that's a P9.8 follow-up. */
export interface CreateBundleRequest {
  name: string;
  description: string | null;
  rationale: string;
}

/** P9.8 (#90) — return shape for `GET /api/bundles/{id}/diff?to={otherId}`,
 *  matches `BundleDiffResult` server-side. `rfc6902PatchJson` is a
 *  stringified array of operations; `Rfc6902DiffViewComponent` parses it. */
export interface BundleDiffResult {
  fromId: string;
  fromSnapshotHash: string;
  toId: string;
  toSnapshotHash: string;
  rfc6902PatchJson: string;
  opCount: number;
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
    expectedRevision?: number,
  ): Observable<PolicyVersionDto> {
    const segment = TRANSITION_PATH_SEGMENTS[targetState];
    if (!segment) {
      throw new Error(`No endpoint exists for transition to '${targetState}'.`);
    }
    const body: LifecycleTransitionBody = expectedRevision != null
      ? { rationale, expectedRevision }
      : { rationale };
    return this.http.post<PolicyVersionDto>(
      `${this.baseUrl}/policies/${id}/versions/${versionId}/${segment}`,
      body,
    );
  }

  // --- Bindings (P9.5, #70) ---

  /** Lists every (non-soft-deleted) binding on a specific version. */
  listVersionBindings(policyId: string, versionId: string): Observable<BindingDto[]> {
    return this.http.get<BindingDto[]>(
      `${this.baseUrl}/policies/${policyId}/versions/${versionId}/bindings`,
    );
  }

  createBinding(request: CreateBindingRequest): Observable<BindingDto> {
    return this.http.post<BindingDto>(`${this.baseUrl}/bindings`, request);
  }

  /** Server accepts rationale as a query param (not body) on DELETE. */
  deleteBinding(bindingId: string, rationale: string): Observable<void> {
    let params = new HttpParams();
    if (rationale) params = params.set('rationale', rationale);
    return this.http.delete<void>(`${this.baseUrl}/bindings/${bindingId}`, { params });
  }

  // --- Overrides (P9.6, #88) ---

  listOverrides(query: OverrideListQuery = {}): Observable<OverrideDto[]> {
    let params = new HttpParams();
    if (query.state) params = params.set('state', query.state);
    if (query.scopeKind) params = params.set('scopeKind', query.scopeKind);
    if (query.scopeRef) params = params.set('scopeRef', query.scopeRef);
    if (query.policyVersionId) params = params.set('policyVersionId', query.policyVersionId);
    return this.http.get<OverrideDto[]>(`${this.baseUrl}/overrides`, { params });
  }

  /** P9.6 follow-up #200 — propose a new override against a specific
   *  policy version. Server returns 201 + `OverrideDto` (state=Proposed)
   *  on success; 400 on Effect/Replacement invariant violation;
   *  403 when the experimental gate is off OR the caller lacks
   *  `andy-policies:override:propose`; 404 when the target version
   *  doesn't exist. */
  proposeOverride(request: ProposeOverrideRequest): Observable<OverrideDto> {
    return this.http.post<OverrideDto>(`${this.baseUrl}/overrides`, request);
  }

  /** Approve takes no body — server records the approver's subject id from
   *  the JWT and stamps `approvedAt`. Spec called for a rationale field that
   *  doesn't exist on this endpoint; deferred to a follow-up. */
  approveOverride(id: string): Observable<OverrideDto> {
    return this.http.post<OverrideDto>(
      `${this.baseUrl}/overrides/${id}/approve`,
      null,
    );
  }

  /** Revoke body uses `revocationReason` (NOT `rationale`). */
  revokeOverride(id: string, revocationReason: string): Observable<OverrideDto> {
    const body: RevokeOverrideRequest = { revocationReason };
    return this.http.post<OverrideDto>(
      `${this.baseUrl}/overrides/${id}/revoke`,
      body,
    );
  }

  // --- Audit (P9.7, #89) ---

  listAudit(query: AuditQuery = {}): Observable<AuditPageDto> {
    let params = new HttpParams();
    if (query.actor) params = params.set('actor', query.actor);
    if (query.action) params = params.set('action', query.action);
    if (query.entityType) params = params.set('entityType', query.entityType);
    if (query.entityId) params = params.set('entityId', query.entityId);
    if (query.from) params = params.set('from', query.from);
    if (query.to) params = params.set('to', query.to);
    if (query.cursor) params = params.set('cursor', query.cursor);
    if (query.pageSize != null) params = params.set('pageSize', query.pageSize.toString());
    return this.http.get<AuditPageDto>(`${this.baseUrl}/audit`, { params });
  }

  verifyAuditChain(fromSeq?: number, toSeq?: number): Observable<ChainVerificationDto> {
    let params = new HttpParams();
    if (fromSeq != null) params = params.set('fromSeq', fromSeq.toString());
    if (toSeq != null) params = params.set('toSeq', toSeq.toString());
    return this.http.get<ChainVerificationDto>(
      `${this.baseUrl}/audit/verify`,
      { params },
    );
  }

  // --- Bundles (P8.3 + P9.8 #90) ---

  listBundles(includeDeleted = false): Observable<BundleDto[]> {
    let params = new HttpParams();
    if (includeDeleted) params = params.set('includeDeleted', 'true');
    return this.http.get<BundleDto[]>(`${this.baseUrl}/bundles`, { params });
  }

  createBundle(request: CreateBundleRequest): Observable<BundleDto> {
    return this.http.post<BundleDto>(`${this.baseUrl}/bundles`, request);
  }

  getBundle(id: string): Observable<BundleDto> {
    return this.http.get<BundleDto>(`${this.baseUrl}/bundles/${id}`);
  }

  /** Diffs `aId` against `bId`. Server requires distinct ids and rejects
   *  same-id with 400; UI prevents that anyway via the 2-row select. */
  diffBundles(aId: string, bId: string): Observable<BundleDiffResult> {
    const params = new HttpParams().set('to', bId);
    return this.http.get<BundleDiffResult>(
      `${this.baseUrl}/bundles/${aId}/diff`,
      { params },
    );
  }

  /** Server accepts rationale as a query param (NOT body) on DELETE. */
  deleteBundle(id: string, rationale: string): Observable<void> {
    let params = new HttpParams();
    if (rationale) params = params.set('rationale', rationale);
    return this.http.delete<void>(`${this.baseUrl}/bundles/${id}`, { params });
  }

  // --- Schemas (#191 #192) ---

  /**
   * Fetch the rules-DSL JSON Schema served at `/api/schemas/rules.json`.
   * Anonymous endpoint — strong ETag + 5-minute Cache-Control on the
   * server side, so repeat calls are cheap. Used by the Monaco editor
   * (#192) to wire schema-aware diagnostics. Type is intentionally
   * `unknown` because the schema is permissive today (`type: object,
   * additionalProperties: true`) and Monaco accepts any JSON.
   */
  getRulesSchema(): Observable<unknown> {
    return this.http.get<unknown>(`${this.baseUrl}/schemas/rules.json`);
  }

  // --- Publish workflow (P9.3 #68 / backend #216) ---

  /**
   * Returns the current subject's effective permission codes for this
   * service. The browser must NEVER call andy-rbac directly — this is
   * the firewall (#103). Response is cached server-side for 60s
   * keyed on subject id, and `PermissionsService` caches it client-side
   * for the lifetime of the SPA session (refreshed at login).
   */
  getMyPermissions(): Observable<string[]> {
    return this.http.get<string[]>(`${this.baseUrl}/auth/permissions`);
  }

  /**
   * Author flips `ReadyForReview = true` on a Draft version. Idempotent
   * server-side (re-proposing an already-proposed draft is a no-op
   * without an extra audit event). Returns 409 if the version is past
   * Draft.
   */
  proposePolicyVersion(
    policyId: string,
    versionId: string,
    rationale: string,
  ): Observable<PolicyVersionDto> {
    return this.http.post<PolicyVersionDto>(
      `${this.baseUrl}/policies/${policyId}/versions/${versionId}/propose`,
      { rationale },
    );
  }

  /**
   * Approver bounces a Proposed draft back to plain Draft (option (a)
   * reject semantics: revert-to-draft, not terminal-state). Server
   * requires a non-empty rationale — the audit chain is the only
   * place an author finds out *why* the proposal bounced.
   */
  rejectPolicyVersion(
    policyId: string,
    versionId: string,
    rationale: string,
  ): Observable<PolicyVersionDto> {
    return this.http.post<PolicyVersionDto>(
      `${this.baseUrl}/policies/${policyId}/versions/${versionId}/reject`,
      { rationale },
    );
  }

  /**
   * Approver inbox feed. Returns Draft versions where
   * `readyForReview === true`, ordered most-recently-created first.
   * The route is gated on `andy-policies:policy:publish`.
   */
  listPendingApprovals(
    skip?: number,
    take?: number,
  ): Observable<PolicyVersionDto[]> {
    let params = new HttpParams();
    if (skip != null) params = params.set('skip', skip.toString());
    if (take != null) params = params.set('take', take.toString());
    return this.http.get<PolicyVersionDto[]>(
      `${this.baseUrl}/policies/pending-approval`,
      { params },
    );
  }
}
