// Copyright (c) Rivoli AI 2026. All rights reserved.

import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, map, tap } from 'rxjs';
import { ApiService } from '../../shared/services/api.service';

/**
 * Caches the current subject's permission allow-set for the lifetime
 * of the SPA session and exposes signal-shaped accessors that
 * components and guards can consume reactively. The single source of
 * truth is `GET /api/auth/permissions` — the browser must NEVER call
 * andy-rbac directly (#103 firewall).
 *
 * Trust posture: we cache the API's answer, not the JWT claims. JWT
 * claims are a moving target (andy-rbac can revoke without token
 * refresh), so the SPA refreshes via this service whenever it would
 * otherwise gate a sensitive action client-side.
 */
@Injectable({ providedIn: 'root' })
export class PermissionsService {
  private readonly api = inject(ApiService);

  private readonly perms = signal<ReadonlySet<string>>(new Set());
  private readonly loaded = signal(false);

  /** True after the first successful refresh; gates wall in the UI
   *  while the refresh is in flight to avoid flashing a hidden Approve
   *  button visible. */
  readonly isLoaded = this.loaded.asReadonly();

  /**
   * Refresh the cached allow-set. Returns the new set on success;
   * components can subscribe and `tap` it for one-shot post-refresh
   * actions. Re-emits the cached value if the refresh fails so the UI
   * stays in a stable state — the component that triggered the
   * refresh decides whether to surface the failure as a toast.
   */
  refresh(): Observable<ReadonlySet<string>> {
    return this.api.getMyPermissions().pipe(
      tap(list => {
        this.perms.set(new Set(list));
        this.loaded.set(true);
      }),
      map(list => new Set(list) as ReadonlySet<string>),
    );
  }

  /** Synchronous predicate. Use inside `computed()` to derive
   *  reactive signals from the underlying set. */
  has(permission: string): boolean {
    return this.perms().has(permission);
  }

  /**
   * Test-only seam for Karma specs that want to set the allow-set
   * directly without driving the HTTP refresh. Production callers
   * should use {@link refresh}.
   */
  setForTesting(codes: Iterable<string>): void {
    this.perms.set(new Set(codes));
    this.loaded.set(true);
  }

  // --- P9.3 (#68) — publish-workflow gates ---------------------------

  /** Approve / reject a proposed draft. Same code gates both paths
   *  server-side (the reject endpoint also requires :reject, but the
   *  inbox itself is :publish-gated). */
  readonly canPublish = computed(() => this.has('andy-policies:policy:publish'));

  /** Author marks a draft "ready for review". */
  readonly canPropose = computed(() => this.has('andy-policies:policy:propose'));

  /** Approver rejects (revert-to-draft, not terminal). */
  readonly canReject = computed(() => this.has('andy-policies:policy:reject'));
}
