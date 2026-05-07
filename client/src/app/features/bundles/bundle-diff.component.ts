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
import { ActivatedRoute, RouterLink } from '@angular/router';
import {
  ApiService,
  BundleDiffResult,
} from '../../shared/services/api.service';
import { Rfc6902DiffViewComponent } from '../../shared/components/rfc6902-diff-view.component';

/**
 * P9.8 (rivoli-ai/andy-policies#90) — bundle diff. Reads `?a=` and
 * `?b=` from the URL, calls `GET /api/bundles/{a}/diff?to={b}`, and
 * delegates rendering to the same `Rfc6902DiffViewComponent` used by
 * the audit timeline (P9.7). Server returns a `BundleDiffResult`
 * carrying a stringified `rfc6902PatchJson` blob — the renderer's
 * defensive parse path turns that into an `Rfc6902Op[]`.
 */
@Component({
  selector: 'app-bundle-diff',
  standalone: true,
  imports: [CommonModule, RouterLink, Rfc6902DiffViewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './bundle-diff.component.html',
  styleUrls: ['./bundle-diff.component.scss'],
})
export class BundleDiffComponent {
  private readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly result = signal<BundleDiffResult | null>(null);
  readonly loading = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly a = signal<string | null>(null);
  readonly b = signal<string | null>(null);

  readonly missingParams = computed(() => !this.a() || !this.b());
  readonly identicalParams = computed(() =>
    !this.missingParams() && this.a() === this.b(),
  );

  constructor() {
    const qp = this.route.snapshot.queryParamMap;
    this.a.set(qp.get('a'));
    this.b.set(qp.get('b'));
    if (!this.missingParams() && !this.identicalParams()) {
      this.load();
    }
  }

  load(): void {
    const aId = this.a();
    const bId = this.b();
    if (!aId || !bId || aId === bId) return;

    this.loading.set(true);
    this.errorMessage.set(null);
    this.api
      .diffBundles(aId, bId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: r => {
          this.result.set(r);
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
