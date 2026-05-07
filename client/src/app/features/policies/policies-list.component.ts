// Copyright (c) Rivoli AI 2026. All rights reserved.

import { CommonModule } from '@angular/common';
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
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Subject, debounceTime, switchMap } from 'rxjs';
import {
  ApiService,
  BundleDto,
  Enforcement,
  PolicyDto,
  PolicyQuery,
  Severity,
} from '../../shared/services/api.service';

/**
 * P9.1 (rivoli-ai/andy-policies#66) — first admin surface. Lists policies via
 * `GET /api/policies` with the four filters the server actually supports
 * (namePrefix, scope, enforcement, severity), `skip`/`take` paging, and a
 * bundle context picker. The `[RequiresBundlePin]` gate (P8.4) is reactive:
 * if the catalog has bundle pinning required, the list call returns 400 with
 * a structured ProblemDetails — we surface that as a banner prompting the
 * user to pick a bundle from the dropdown.
 */
@Component({
  selector: 'app-policies-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './policies-list.component.html',
  styleUrls: ['./policies-list.component.scss'],
})
export class PoliciesListComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  // Filter state (signals — Angular 18 idiom for local UI state).
  readonly namePrefix   = signal<string>('');
  readonly scope        = signal<string>('');
  readonly enforcement  = signal<Enforcement | ''>('');
  readonly severity     = signal<Severity | ''>('');
  readonly bundleId     = signal<string | ''>('');
  readonly take         = signal<number>(25);
  readonly skip         = signal<number>(0);

  // Result state.
  readonly policies     = signal<PolicyDto[]>([]);
  readonly bundles      = signal<BundleDto[]>([]);
  readonly loading      = signal<boolean>(false);
  readonly errorMessage = signal<string | null>(null);
  readonly pinningRequired = signal<boolean>(false);

  // Derived: whether the most recent page returned a full take (i.e. there's
  // probably more). The list endpoint doesn't return total, so we infer.
  readonly hasMore = computed(
    () => this.policies().length > 0 && this.policies().length % this.take() === 0,
  );

  readonly enforcementOptions: Enforcement[] = ['MUST', 'SHOULD', 'MAY'];
  readonly severityOptions: Severity[] = ['info', 'moderate', 'critical'];

  // Debounce signal for the namePrefix input — avoids a request per keystroke.
  private readonly searchInput$ = new Subject<string>();

  constructor() {
    // Hydrate filter state from URL query params on first paint, so a deep
    // link / refresh restores the same view. Subsequent changes write back
    // via reload().
    const qp = this.route.snapshot.queryParamMap;
    if (qp.get('namePrefix')) this.namePrefix.set(qp.get('namePrefix')!);
    if (qp.get('scope')) this.scope.set(qp.get('scope')!);
    if (qp.get('enforcement')) this.enforcement.set(qp.get('enforcement') as Enforcement);
    if (qp.get('severity')) this.severity.set(qp.get('severity') as Severity);
    if (qp.get('bundleId')) this.bundleId.set(qp.get('bundleId')!);
    if (qp.get('take')) this.take.set(this.clampTake(Number(qp.get('take'))));

    // Search debounce — fires reload() after 250ms of input idle.
    this.searchInput$
      .pipe(
        debounceTime(250),
        switchMap(value => {
          this.namePrefix.set(value);
          this.skip.set(0);
          this.reloadInner();
          return [];
        }),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe();

    // Initial load + bundle list.
    this.loadBundles();
    this.reloadInner();
  }

  onSearchInput(value: string): void {
    this.searchInput$.next(value);
  }

  onFilterChange(): void {
    this.skip.set(0);
    this.reload();
  }

  loadMore(): void {
    this.skip.update(s => s + this.take());
    // Append rather than replace on paginate — keeps the table state visible
    // while the next page arrives.
    this.reloadInner({ append: true });
  }

  retry(): void {
    this.errorMessage.set(null);
    this.reload();
  }

  /** Reload + sync URL query string so filters are bookmarkable. */
  reload(): void {
    this.syncUrl();
    this.reloadInner();
  }

  private loadBundles(): void {
    this.api
      .listBundles()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: list => this.bundles.set(list),
        // Bundle list isn't critical — silently leave the picker empty.
        error: () => this.bundles.set([]),
      });
  }

  private reloadInner(opts: { append?: boolean } = {}): void {
    const query: PolicyQuery = {
      namePrefix: this.namePrefix() || undefined,
      scope: this.scope() || undefined,
      enforcement: this.enforcement() || undefined,
      severity: this.severity() || undefined,
      skip: this.skip(),
      take: this.take(),
      bundleId: this.bundleId() || undefined,
    };

    this.loading.set(true);
    this.errorMessage.set(null);
    this.api
      .listPolicies(query)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: rows => {
          this.policies.update(prev => (opts.append ? [...prev, ...rows] : rows));
          // Successful read against this bundleId means pinning isn't blocking us.
          this.pinningRequired.set(false);
          this.loading.set(false);
        },
        error: (err: HttpErrorResponse) => {
          this.loading.set(false);
          if (this.isPinGateError(err)) {
            this.pinningRequired.set(true);
            this.errorMessage.set(
              'Bundle version pinning is enabled — pick a bundle context above to browse.',
            );
            return;
          }
          this.errorMessage.set(this.describeError(err));
        },
      });
  }

  private syncUrl(): void {
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        namePrefix: this.namePrefix() || null,
        scope: this.scope() || null,
        enforcement: this.enforcement() || null,
        severity: this.severity() || null,
        bundleId: this.bundleId() || null,
        take: this.take(),
      },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  private clampTake(n: number): number {
    if (!Number.isFinite(n) || n < 1) return 25;
    return Math.min(100, Math.floor(n));
  }

  private isPinGateError(err: HttpErrorResponse): boolean {
    if (err.status !== 400) return false;
    const code = err.error?.errorCode ?? err.error?.extensions?.errorCode;
    if (typeof code === 'string' && code.includes('bundle')) return true;
    const detail = (err.error?.detail ?? '').toString().toLowerCase();
    return detail.includes('bundle') && detail.includes('pin');
  }

  private describeError(err: HttpErrorResponse): string {
    if (err.error?.title) return `${err.error.title} (${err.status}).`;
    if (err.message) return err.message;
    return `Unexpected error (${err.status}).`;
  }
}
