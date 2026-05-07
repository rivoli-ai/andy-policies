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
import { Router, RouterLink } from '@angular/router';
import { ApiService, BundleDto } from '../../shared/services/api.service';
import { CreateBundleModalComponent } from './create-bundle-modal.component';

/**
 * P9.8 (rivoli-ai/andy-policies#90) — list view for bundles.
 *
 * Spec asked for `policyCount`, `bindingCount`, `overrideCount` columns;
 * the actual `BundleDto` doesn't expose those (would need a per-bundle
 * detail endpoint that doesn't exist yet — filed follow-up). UI shows
 * the metadata that IS available: name, description, snapshotHash,
 * state, createdAt + by, deletedAt + by (when includeDeleted is on).
 *
 * Two-row checkbox selection enables a Compare CTA that routes to
 * `/bundles/diff?a=&b=`. Selecting any other count disables Compare.
 */
@Component({
  selector: 'app-bundles-list',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    CreateBundleModalComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './bundles-list.component.html',
  styleUrls: ['./bundles-list.component.scss'],
})
export class BundlesListComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly bundles = signal<BundleDto[]>([]);
  readonly loading = signal(false);
  readonly errorMessage = signal<string | null>(null);
  readonly includeDeleted = signal(false);
  readonly showCreate = signal(false);
  readonly selectedIds = signal<ReadonlySet<string>>(new Set());

  readonly canCompare = computed(() => this.selectedIds().size === 2);

  constructor() {
    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.errorMessage.set(null);
    this.selectedIds.set(new Set());
    this.api
      .listBundles(this.includeDeleted())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: rows => {
          this.bundles.set(rows);
          this.loading.set(false);
        },
        error: (err: HttpErrorResponse) => {
          this.loading.set(false);
          this.errorMessage.set(err.error?.title ?? `Failed to load (${err.status}).`);
        },
      });
  }

  toggleIncludeDeleted(): void {
    this.includeDeleted.update(v => !v);
    this.reload();
  }

  isSelected(id: string): boolean {
    return this.selectedIds().has(id);
  }

  toggleSelect(id: string): void {
    this.selectedIds.update(prev => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }

  openCreate(): void {
    this.showCreate.set(true);
  }

  onCreateClosed(created: BundleDto | null): void {
    this.showCreate.set(false);
    if (created) {
      this.bundles.update(rows => [created, ...rows]);
    }
  }

  compare(): void {
    if (!this.canCompare()) return;
    const [a, b] = Array.from(this.selectedIds());
    this.router.navigate(['/bundles/diff'], { queryParams: { a, b } });
  }
}
