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
import { forkJoin } from 'rxjs';
import {
  ApiService,
  PolicyDto,
  PolicyVersionDto,
} from '../../shared/services/api.service';
import { LifecycleDiagramComponent } from './lifecycle-diagram.component';
import { LifecycleTransitionModalComponent } from './lifecycle-transition-modal.component';
import { LIFECYCLE_GRAPH, LIFECYCLE_LABEL } from './lifecycle-graph';

/**
 * P9.4 (rivoli-ai/andy-policies#69) — minimum viable policy detail page.
 * Shows policy header, the version list, and a Transition button that
 * opens `LifecycleTransitionModalComponent` for the selected version. Edit
 * (P9.2) link surfaces only on Draft versions. Bindings/overrides/audit
 * panels arrive in P9.5/P9.6/P9.7.
 */
@Component({
  selector: 'app-policy-detail',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    LifecycleDiagramComponent,
    LifecycleTransitionModalComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './policy-detail.component.html',
  styleUrls: ['./policy-detail.component.scss'],
})
export class PolicyDetailComponent {
  private readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly LIFECYCLE_LABEL = LIFECYCLE_LABEL;

  readonly policy = signal<PolicyDto | null>(null);
  readonly versions = signal<PolicyVersionDto[]>([]);
  readonly loading = signal(true);
  readonly errorMessage = signal<string | null>(null);

  readonly transitioningVersion = signal<PolicyVersionDto | null>(null);

  readonly activeVersion = computed<PolicyVersionDto | null>(() => {
    const list = this.versions();
    const p = this.policy();
    if (!p?.activeVersionId) return null;
    return list.find(v => v.id === p.activeVersionId) ?? null;
  });

  constructor() {
    const id = this.route.snapshot.paramMap.get('id');
    if (id) this.load(id);
  }

  hasLegalTransitions(version: PolicyVersionDto): boolean {
    return (LIFECYCLE_GRAPH[version.state] ?? []).length > 0;
  }

  openTransition(version: PolicyVersionDto): void {
    this.transitioningVersion.set(version);
  }

  onModalClosed(updated: PolicyVersionDto | null): void {
    if (updated) {
      // Replace the version in-place, then refetch the policy header so
      // activeVersionId moves with the transition.
      this.versions.update(vs =>
        vs.map(v => (v.id === updated.id ? updated : v)),
      );
      const id = this.policy()?.id;
      if (id) {
        this.api
          .getPolicy(id)
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({
            next: p => this.policy.set(p),
            error: () => {/* non-critical */},
          });
      }
    }
    this.transitioningVersion.set(null);
  }

  private load(id: string): void {
    this.loading.set(true);
    this.errorMessage.set(null);
    forkJoin({
      policy: this.api.getPolicy(id),
      versions: this.api.listPolicyVersions(id),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ policy, versions }) => {
          this.policy.set(policy);
          // Sort by version desc so latest is first.
          this.versions.set([...versions].sort((a, b) => b.version - a.version));
          this.loading.set(false);
        },
        error: (err: HttpErrorResponse) => {
          this.loading.set(false);
          this.errorMessage.set(err.error?.title ?? `Failed to load policy (${err.status}).`);
        },
      });
  }
}
