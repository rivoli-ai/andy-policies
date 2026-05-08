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
import { RouterLink } from '@angular/router';
import { ApiService, PolicyVersionDto } from '../../shared/services/api.service';
import { PermissionsService } from '../../core/auth/permissions.service';
import { RationaleModalComponent } from './rationale-modal.component';

type ModalKind = 'approve' | 'reject' | null;

/**
 * P9.3 (#68) — approver inbox queue. Polls
 * `GET /api/policies/pending-approval` every 30 s and renders the
 * Draft+ReadyForReview rows. Approve / Reject buttons are visible
 * only when the cached `PermissionsService.canPublish()` /
 * `canReject()` signals are true; the API itself rejects unauthorized
 * calls with 403, so the visibility check is a UX convenience, not a
 * security boundary (#103).
 *
 * The Approve flow drives `transitionPolicyVersion(target='Active')`
 * with `expectedRevision` set to the row's `revision` — a 412 means
 * the draft was edited between inbox load and click; we surface that
 * inline so the user can reload.
 *
 * The Reject flow drives `rejectPolicyVersion`. Both paths reuse a
 * single `RationaleModalComponent` parametrised by verb labels.
 */
@Component({
  selector: 'app-approvals-inbox',
  standalone: true,
  imports: [CommonModule, RouterLink, RationaleModalComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './approvals-inbox.component.html',
  styleUrls: ['./approvals-inbox.component.scss'],
})
export class ApprovalsInboxComponent implements OnInit, OnDestroy {
  /** Inbox poll interval. Public so Karma specs can short-circuit. */
  static readonly pollIntervalMs = 30_000;

  private readonly api = inject(ApiService);
  private readonly perms = inject(PermissionsService);
  private readonly destroyRef = inject(DestroyRef);

  readonly rows = signal<PolicyVersionDto[]>([]);
  readonly loading = signal(false);
  readonly errorMessage = signal<string | null>(null);

  /** Which row's modal is open (and which verb). */
  readonly activeRow = signal<PolicyVersionDto | null>(null);
  readonly modalKind = signal<ModalKind>(null);
  readonly modalError = signal<string | null>(null);

  readonly canPublish = this.perms.canPublish;
  readonly canReject = this.perms.canReject;

  readonly hasRows = computed(() => this.rows().length > 0);

  private intervalId: number | null = null;

  ngOnInit(): void {
    this.refresh();
    this.intervalId = window.setInterval(
      () => this.refresh(),
      ApprovalsInboxComponent.pollIntervalMs,
    );
  }

  ngOnDestroy(): void {
    if (this.intervalId !== null) {
      window.clearInterval(this.intervalId);
      this.intervalId = null;
    }
  }

  refresh(): void {
    this.loading.set(true);
    this.errorMessage.set(null);
    this.api
      .listPendingApprovals()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: rows => {
          this.rows.set(rows);
          this.loading.set(false);
        },
        error: (err: HttpErrorResponse) => {
          this.loading.set(false);
          this.errorMessage.set(this.describeProblem(err)
            ?? `Could not load inbox (${err.status}).`);
        },
      });
  }

  openApprove(row: PolicyVersionDto): void {
    if (!this.canPublish()) return;
    this.activeRow.set(row);
    this.modalKind.set('approve');
    this.modalError.set(null);
  }

  openReject(row: PolicyVersionDto): void {
    if (!this.canReject()) return;
    this.activeRow.set(row);
    this.modalKind.set('reject');
    this.modalError.set(null);
  }

  closeModal(): void {
    this.activeRow.set(null);
    this.modalKind.set(null);
    this.modalError.set(null);
  }

  /** Modal confirmation handler. Routes to the right API call based on
   *  `modalKind`; on 412 (stale revision) we keep the modal open with
   *  a Reload-the-row banner. */
  onModalConfirmed(rationale: string): void {
    const row = this.activeRow();
    const kind = this.modalKind();
    if (!row || !kind) return;

    this.modalError.set(null);

    const call$ = kind === 'approve'
      ? this.api.transitionPolicyVersion(
          row.policyId, row.id, 'Active', rationale, row.revision)
      : this.api.rejectPolicyVersion(row.policyId, row.id, rationale);

    call$
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: updated => {
          // Drop the row from the inbox: it either left Draft (approve)
          // or is no longer ReadyForReview (reject).
          this.rows.update(current => current.filter(r => r.id !== updated.id));
          this.closeModal();
        },
        error: (err: HttpErrorResponse) => {
          this.modalError.set(this.describeRowError(err, kind));
        },
      });
  }

  private describeRowError(err: HttpErrorResponse, kind: 'approve' | 'reject'): string {
    if (err.status === 412 && kind === 'approve') {
      // Optimistic-concurrency 412 — the draft was edited since the
      // inbox loaded. Tell the user to reload; the next poll tick
      // will pick it up automatically too.
      return 'This draft was modified since you loaded the inbox. Click Cancel and reload to pick up the latest revision.';
    }
    if (err.status === 409) {
      return kind === 'approve'
        ? 'Cannot approve — the version is no longer in Draft state. Reload the inbox.'
        : 'Cannot reject — the version is no longer in Draft state. Reload the inbox.';
    }
    if (err.status === 403) {
      return 'Permission denied.';
    }
    return this.describeProblem(err) ?? `Unexpected error (${err.status}).`;
  }

  private describeProblem(err: HttpErrorResponse): string | null {
    const body = err.error;
    if (!body) return null;
    if (typeof body.detail === 'string' && body.detail) return body.detail;
    if (typeof body.title === 'string' && body.title) return `${body.title} (${err.status}).`;
    return null;
  }
}
