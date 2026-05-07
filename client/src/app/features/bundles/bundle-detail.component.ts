// Copyright (c) Rivoli AI 2026. All rights reserved.

import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiService, BundleDto } from '../../shared/services/api.service';

/**
 * P9.8 (rivoli-ai/andy-policies#90) — bundle detail view, metadata only.
 *
 * The original spec called for a frozen-tree renderer (`policies[].bindings[]`,
 * `overrides[]`) but the server has no per-bundle tree-dump endpoint —
 * pinned policies are reachable individually via
 * `GET /api/bundles/{id}/policies/{policyId}` or by-target via
 * `.../resolve`. Filed as a follow-up; this view ships the metadata
 * fields that ARE returned by `GET /api/bundles/{id}` (BundleDto).
 */
@Component({
  selector: 'app-bundle-detail',
  standalone: true,
  imports: [CommonModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './bundle-detail.component.html',
  styleUrls: ['./bundle-detail.component.scss'],
})
export class BundleDetailComponent {
  private readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly bundle = signal<BundleDto | null>(null);
  readonly loading = signal(true);
  readonly errorMessage = signal<string | null>(null);

  constructor() {
    const id = this.route.snapshot.paramMap.get('bundleId');
    if (id) this.load(id);
  }

  private load(id: string): void {
    this.loading.set(true);
    this.errorMessage.set(null);
    this.api
      .getBundle(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: b => {
          this.bundle.set(b);
          this.loading.set(false);
        },
        error: (err: HttpErrorResponse) => {
          this.loading.set(false);
          this.errorMessage.set(err.error?.title ?? `Failed to load bundle (${err.status}).`);
        },
      });
  }
}
