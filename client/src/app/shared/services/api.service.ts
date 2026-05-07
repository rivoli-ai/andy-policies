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

  // --- Bundles (P8.3) ---

  listBundles(includeDeleted = false): Observable<BundleDto[]> {
    let params = new HttpParams();
    if (includeDeleted) params = params.set('includeDeleted', 'true');
    return this.http.get<BundleDto[]>(`${this.baseUrl}/bundles`, { params });
  }
}
