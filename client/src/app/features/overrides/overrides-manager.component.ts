// Copyright (c) Rivoli AI 2026. All rights reserved.

import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  ApiService,
  OverrideDto,
  OverrideState,
} from '../../shared/services/api.service';
import { ExpiryCountdownPipe } from './expiry-countdown.pipe';
import { RevokeOverrideModalComponent } from './revoke-override-modal.component';

const STATE_TABS: OverrideState[] = ['Proposed', 'Approved', 'Revoked', 'Expired'];

/**
 * P9.6 (rivoli-ai/andy-policies#88) — overrides browser + manager.
 *
 * Reality vs. spec compromises:
 * - The original spec described a two-column layout (Proposals | Active+Expired
 *   tabs) plus settings-driven banner gating, RBAC permission cache, and a
 *   Reject endpoint. Server-side: there's no /api/settings runtime endpoint
 *   (#191 family), no permissions cache endpoint (covered by P9.3 hold), and
 *   no Reject endpoint. The settings gate IS enforced server-side via
 *   `[OverrideWriteGate]` on the write endpoints, so we surface 403s
 *   reactively from approve/revoke calls instead of preemptive banner.
 * - Approve takes no body server-side (no rationale modal); revoke uses
 *   `revocationReason`, not `rationale`.
 * - "Active" in the spec maps to "Approved" in the actual `OverrideState`
 *   enum. We use the server names.
 *
 * The view: filter by state (one tab per server enum value), table per
 * state, click a row to expand inline detail (full chain + countdown).
 * Approved/Proposed rows expose Approve / Revoke; the buttons call the
 * server and rely on 403 / 409 to surface inline if the user lacks perms
 * or the experimental gate is off.
 */
@Component({
  selector: 'app-overrides-manager',
  standalone: true,
  imports: [
    CommonModule,
    ExpiryCountdownPipe,
    RevokeOverrideModalComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './overrides-manager.component.html',
  styleUrls: ['./overrides-manager.component.scss'],
})
export class OverridesManagerComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly STATE_TABS = STATE_TABS;
  readonly state = signal<OverrideState>('Proposed');
  readonly overrides = signal<OverrideDto[]>([]);
  readonly loading = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly expandedId = signal<string | null>(null);
  readonly revoking = signal<OverrideDto | null>(null);
  readonly approveError = signal<string | null>(null);

  /** Drives the impure ExpiryCountdownPipe — reading this in the template
   *  forces CD to re-evaluate the pipe each tick. */
  readonly tick = signal(0);
  private intervalId: number | null = null;

  readonly currentRows = computed(() => this.overrides());
  readonly hasRows = computed(() => this.currentRows().length > 0);

  ngOnInit(): void {
    this.reload();
    // Re-render countdowns once a minute. Use window.setInterval (not RxJS
    // timer) so the test harness can fast-forward via fakeAsync without
    // pulling in extra zone glue; we explicitly clear in ngOnDestroy.
    this.intervalId = window.setInterval(() => this.tick.update(t => t + 1), 60_000);
  }

  ngOnDestroy(): void {
    if (this.intervalId !== null) {
      window.clearInterval(this.intervalId);
      this.intervalId = null;
    }
  }

  setState(state: OverrideState): void {
    if (this.state() === state) return;
    this.state.set(state);
    this.expandedId.set(null);
    this.reload();
  }

  toggleExpand(id: string): void {
    this.expandedId.update(curr => (curr === id ? null : id));
  }

  approve(o: OverrideDto): void {
    this.approveError.set(null);
    this.api
      .approveOverride(o.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: updated => this.applyUpdated(updated),
        error: (err: HttpErrorResponse) => this.approveError.set(this.describeError(err, 'approve')),
      });
  }

  openRevoke(o: OverrideDto): void {
    this.revoking.set(o);
  }

  onRevokeClosed(updated: OverrideDto | null): void {
    this.revoking.set(null);
    if (updated) this.applyUpdated(updated);
  }

  retry(): void {
    this.errorMessage.set(null);
    this.reload();
  }

  private reload(): void {
    this.loading.set(true);
    this.errorMessage.set(null);
    this.api
      .listOverrides({ state: this.state() })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: rows => {
          this.overrides.set(rows);
          this.loading.set(false);
        },
        error: (err: HttpErrorResponse) => {
          this.loading.set(false);
          this.errorMessage.set(this.describeError(err, 'load'));
        },
      });
  }

  /** When approve / revoke returns the new DTO, update or remove from
   *  the current list depending on whether its new state still matches. */
  private applyUpdated(updated: OverrideDto): void {
    const currentTab = this.state();
    if (updated.state === currentTab) {
      this.overrides.update(rows =>
        rows.map(r => (r.id === updated.id ? updated : r)),
      );
    } else {
      // Moved to another state — drop from this list.
      this.overrides.update(rows => rows.filter(r => r.id !== updated.id));
      if (this.expandedId() === updated.id) this.expandedId.set(null);
    }
  }

  private describeError(err: HttpErrorResponse, op: 'approve' | 'revoke' | 'load'): string {
    if (err.status === 403) {
      return op === 'approve'
        ? 'You do not have permission to approve, or overrides are experimentally disabled.'
        : 'Permission denied.';
    }
    const body = err.error;
    if (typeof body?.detail === 'string' && body.detail) return body.detail;
    if (typeof body?.title === 'string') return `${body.title} (${err.status}).`;
    return `Unexpected error (${err.status}).`;
  }
}
