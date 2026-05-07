// Copyright (c) Rivoli AI 2026. All rights reserved.

import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import {
  ApiService,
  AuditEventDto,
  AuditQuery,
  ChainVerificationDto,
} from '../../shared/services/api.service';
import { Rfc6902DiffViewComponent } from '../../shared/components/rfc6902-diff-view.component';

type VerifyState = 'idle' | 'running' | 'ok' | 'diverged' | 'error';

/**
 * P9.7 (rivoli-ai/andy-policies#89) — chronological audit event browser.
 *
 * Reality vs. spec compromises:
 * - The actual server pages by **opaque cursor** (`AuditPageDto.nextCursor`),
 *   not by `fromSeq`. The UI advances via "Load more" rather than infinite-scroll.
 * - `AuditEventDto.fieldDiff` arrives already parsed (JsonElement on the
 *   server, plain array on the wire) so the diff renderer takes an array,
 *   not a stringified blob.
 * - `ChainVerificationDto` shape is `{ valid, firstDivergenceSeq,
 *   inspectedCount, lastSeq }` — no `verifiedAt` timestamp; we display
 *   the local UI clock instead.
 *
 * Filter changes reset the cursor and reload from scratch. "Verify chain"
 * is independent of the filter (the server verifies the whole chain).
 */
@Component({
  selector: 'app-audit-timeline',
  standalone: true,
  imports: [CommonModule, FormsModule, Rfc6902DiffViewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './audit-timeline.component.html',
  styleUrls: ['./audit-timeline.component.scss'],
})
export class AuditTimelineComponent {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  // Filter state.
  readonly actor = signal<string>('');
  readonly action = signal<string>('');
  readonly entityType = signal<string>('');
  readonly entityId = signal<string>('');
  readonly pageSize = signal<number>(50);

  // Result state.
  readonly events = signal<AuditEventDto[]>([]);
  readonly cursor = signal<string | null>(null);
  readonly hasMore = computed(() => this.cursor() !== null);
  readonly loading = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly expanded = signal<ReadonlySet<string>>(new Set());

  // Verify state.
  readonly verifyState = signal<VerifyState>('idle');
  readonly verifyResult = signal<ChainVerificationDto | null>(null);
  readonly verifyAt = signal<Date | null>(null);

  constructor() {
    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.errorMessage.set(null);
    this.events.set([]);
    this.cursor.set(null);
    this.fetchPage(/* append */ false);
  }

  loadMore(): void {
    if (!this.hasMore() || this.loading()) return;
    this.fetchPage(/* append */ true);
  }

  toggle(eventId: string): void {
    this.expanded.update(curr => {
      const next = new Set(curr);
      if (next.has(eventId)) next.delete(eventId); else next.add(eventId);
      return next;
    });
  }

  isExpanded(eventId: string): boolean {
    return this.expanded().has(eventId);
  }

  verify(): void {
    this.verifyState.set('running');
    this.verifyResult.set(null);
    this.api
      .verifyAuditChain()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: r => {
          this.verifyResult.set(r);
          this.verifyAt.set(new Date());
          this.verifyState.set(r.valid ? 'ok' : 'diverged');
        },
        error: () => {
          this.verifyState.set('error');
        },
      });
  }

  copyDiagnostic(): void {
    const r = this.verifyResult();
    if (!r) return;
    const payload = JSON.stringify(
      {
        ...r,
        verifiedAt: this.verifyAt()?.toISOString() ?? null,
      },
      null,
      2,
    );
    // navigator.clipboard is the only modern path; older browsers fall
    // back silently — failure is not user-actionable beyond "screenshot".
    if (typeof navigator !== 'undefined' && navigator.clipboard) {
      void navigator.clipboard.writeText(payload);
    }
  }

  private fetchPage(append: boolean): void {
    const query: AuditQuery = {
      actor: this.actor() || undefined,
      action: this.action() || undefined,
      entityType: this.entityType() || undefined,
      entityId: this.entityId() || undefined,
      pageSize: this.pageSize(),
      cursor: append ? this.cursor() ?? undefined : undefined,
    };

    this.loading.set(true);
    this.errorMessage.set(null);
    this.api
      .listAudit(query)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: page => {
          this.events.update(prev => (append ? [...prev, ...page.items] : page.items));
          this.cursor.set(page.nextCursor);
          this.loading.set(false);
        },
        error: (err: HttpErrorResponse) => {
          this.loading.set(false);
          this.errorMessage.set(this.describeError(err));
        },
      });
  }

  private describeError(err: HttpErrorResponse): string {
    const body = err.error;
    if (typeof body?.detail === 'string' && body.detail) return body.detail;
    if (typeof body?.title === 'string') return `${body.title} (${err.status}).`;
    return `Unexpected error (${err.status}).`;
  }
}
