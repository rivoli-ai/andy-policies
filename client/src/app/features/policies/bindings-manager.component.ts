// Copyright (c) Rivoli AI 2026. All rights reserved.

import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  Input,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import {
  ApiService,
  BindingDto,
  PolicyVersionDto,
} from '../../shared/services/api.service';
import { CreateBindingModalComponent } from './create-binding-modal.component';
import { DeleteBindingModalComponent } from './delete-binding-modal.component';

interface BindingRow extends BindingDto {
  versionNumber: number;
  versionState: string;
}

/**
 * P9.5 (rivoli-ai/andy-policies#70) — bindings manager mounted under
 * the policy detail page. Aggregates bindings across every version of
 * the policy via parallel `GET /api/policies/{id}/versions/{vId}/bindings`
 * calls (the server has no per-policy list endpoint), and renders a
 * single table with a Version column.
 *
 * Spec called for autocomplete on target-ref via a `/target-refs`
 * endpoint that doesn't exist; target-ref is free-text here. Filed as
 * a follow-up.
 */
@Component({
  selector: 'app-bindings-manager',
  standalone: true,
  imports: [
    CommonModule,
    CreateBindingModalComponent,
    DeleteBindingModalComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './bindings-manager.component.html',
  styleUrls: ['./bindings-manager.component.scss'],
})
export class BindingsManagerComponent {
  @Input({ required: true }) policyId!: string;
  @Input({ required: true }) versions: readonly PolicyVersionDto[] = [];

  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly bindings = signal<BindingRow[]>([]);
  readonly loading = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly showCreate = signal(false);
  readonly deleting = signal<BindingDto | null>(null);

  readonly hasAnyBindings = computed(() => this.bindings().length > 0);

  ngOnChanges(): void {
    // versions @Input arrives once the parent has loaded — refresh whenever it changes.
    if (this.policyId && this.versions.length > 0) {
      this.reload();
    }
  }

  reload(): void {
    if (this.versions.length === 0) {
      this.bindings.set([]);
      return;
    }
    this.loading.set(true);
    this.errorMessage.set(null);

    // forkJoin across versions — empty result on a 4xx for one version
    // doesn't sink the whole page; the version-scoped 403 (e.g. the
    // current user can't read a particular version) gets swallowed.
    const calls = this.versions.map(v =>
      this.api.listVersionBindings(this.policyId, v.id).pipe(
        map(rows => ({ version: v, rows })),
        catchError(() => of({ version: v, rows: [] as BindingDto[] })),
      ),
    );
    forkJoin(calls)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: results => {
          const rows: BindingRow[] = results.flatMap(({ version, rows }) =>
            rows
              .filter(r => r.deletedAt === null)
              .map(r => ({
                ...r,
                versionNumber: version.version,
                versionState: version.state,
              })),
          );
          // Sort by version desc, then targetType, then targetRef so reads
          // through the table are stable across reloads.
          rows.sort((a, b) =>
            b.versionNumber - a.versionNumber
            || a.targetType.localeCompare(b.targetType)
            || a.targetRef.localeCompare(b.targetRef),
          );
          this.bindings.set(rows);
          this.loading.set(false);
        },
        error: (err: HttpErrorResponse) => {
          this.loading.set(false);
          this.errorMessage.set(err.error?.title ?? `Failed to load bindings (${err.status}).`);
        },
      });
  }

  openCreate(): void {
    this.showCreate.set(true);
  }

  onCreateClosed(created: BindingDto | null): void {
    this.showCreate.set(false);
    if (created) {
      // Locate the version this binding hit so we can decorate the row
      // without a full reload.
      const ver = this.versions.find(v => v.id === created.policyVersionId);
      const row: BindingRow = {
        ...created,
        versionNumber: ver?.version ?? -1,
        versionState: ver?.state ?? 'Unknown',
      };
      this.bindings.update(rows => {
        const next = [row, ...rows];
        next.sort((a, b) =>
          b.versionNumber - a.versionNumber
          || a.targetType.localeCompare(b.targetType)
          || a.targetRef.localeCompare(b.targetRef),
        );
        return next;
      });
    }
  }

  openDelete(binding: BindingDto): void {
    this.deleting.set(binding);
  }

  onDeleteClosed(result: { deleted: boolean; bindingId: string } | null): void {
    if (result?.deleted) {
      this.bindings.update(rows => rows.filter(r => r.id !== result.bindingId));
    }
    this.deleting.set(null);
  }
}
