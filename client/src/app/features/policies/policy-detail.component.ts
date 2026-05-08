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
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import {
  ApiService,
  PolicyDto,
  PolicyVersionDto,
} from '../../shared/services/api.service';
import { BindingsManagerComponent } from './bindings-manager.component';
import { LifecycleDiagramComponent } from './lifecycle-diagram.component';
import { LifecycleTransitionModalComponent } from './lifecycle-transition-modal.component';
import { LIFECYCLE_GRAPH, LIFECYCLE_LABEL } from './lifecycle-graph';
import { PermissionsService } from '../../core/auth/permissions.service';
import { RationaleModalComponent } from './rationale-modal.component';
import { ProposeOverrideModalComponent } from '../overrides/propose-override-modal.component';
import { OverrideDto } from '../../shared/services/api.service';

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
    BindingsManagerComponent,
    LifecycleDiagramComponent,
    LifecycleTransitionModalComponent,
    RationaleModalComponent,
    ProposeOverrideModalComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './policy-detail.component.html',
  styleUrls: ['./policy-detail.component.scss'],
})
export class PolicyDetailComponent {
  private readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly perms = inject(PermissionsService);

  readonly LIFECYCLE_LABEL = LIFECYCLE_LABEL;

  readonly policy = signal<PolicyDto | null>(null);
  readonly versions = signal<PolicyVersionDto[]>([]);
  readonly loading = signal(true);
  readonly errorMessage = signal<string | null>(null);

  readonly transitioningVersion = signal<PolicyVersionDto | null>(null);

  // P9.3 (#68) — propose-for-publish flow. The button only appears
  // for Draft versions when the user has :propose; the modal collects
  // the rationale and posts it. Approver-side approve/reject lives
  // in the inbox, not here.
  readonly proposingVersion = signal<PolicyVersionDto | null>(null);
  readonly proposeError = signal<string | null>(null);
  readonly canPropose = this.perms.canPropose;

  // #200 — propose-override flow. Distinct from propose-for-publish
  // (which marks the *current* draft ready for review): an override
  // creates a new Override row scoped to a Principal/Cohort and is
  // approved separately via the Overrides manager.
  readonly proposingOverrideFor = signal<PolicyVersionDto | null>(null);
  readonly canProposeOverride = computed(() =>
    this.perms.has('andy-policies:override:propose'));

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

  /** Show the Propose button only for Draft versions that aren't
   *  already ReadyForReview. The :propose permission gate is layered
   *  on the button itself in the template. */
  canProposeVersion(version: PolicyVersionDto): boolean {
    return version.state === 'Draft' && !version.readyForReview;
  }

  openPropose(version: PolicyVersionDto): void {
    if (!this.canPropose()) return;
    this.proposingVersion.set(version);
    this.proposeError.set(null);
  }

  closePropose(): void {
    this.proposingVersion.set(null);
    this.proposeError.set(null);
  }

  /** Show "Propose override" only when we have :override:propose
   *  AND the version is not Retired (server refuses Retired). */
  canProposeOverrideFor(v: PolicyVersionDto): boolean {
    return v.state !== 'Retired';
  }

  /** Other versions of *this* policy that can serve as the
   *  Replace target. Excludes Retired (server refuses) and the
   *  version being overridden itself (replacing v1 with v1 is a
   *  no-op). The modal further constrains the dropdown to whatever
   *  list we hand it. */
  replacementCandidatesFor(v: PolicyVersionDto): PolicyVersionDto[] {
    return this.versions()
      .filter(c => c.id !== v.id && c.state !== 'Retired');
  }

  openProposeOverride(version: PolicyVersionDto): void {
    if (!this.canProposeOverride()) return;
    this.proposingOverrideFor.set(version);
  }

  onProposeOverrideClosed(created: OverrideDto | null): void {
    this.proposingOverrideFor.set(null);
    if (created) {
      // Per #200's acceptance: navigate to /overrides on 201 (vs.
      // refresh-inline-list — there's no inline override list on the
      // detail page today). Pre-filter by `state=Proposed` so the
      // user lands on the row they just created.
      this.router.navigate(['/overrides'], { queryParams: { state: 'Proposed' } });
    }
  }

  onProposeConfirmed(rationale: string): void {
    const v = this.proposingVersion();
    if (!v) return;
    this.proposeError.set(null);
    this.api
      .proposePolicyVersion(v.policyId, v.id, rationale)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: updated => {
          this.versions.update(vs =>
            vs.map(curr => (curr.id === updated.id ? updated : curr)),
          );
          this.closePropose();
        },
        error: (err: HttpErrorResponse) => {
          if (err.status === 403) {
            this.proposeError.set('Permission denied.');
            return;
          }
          if (err.status === 409) {
            this.proposeError.set(
              'Cannot propose — the version is no longer in Draft. Refresh the page.');
            return;
          }
          const body = err.error;
          this.proposeError.set(
            (typeof body?.detail === 'string' && body.detail)
              || (typeof body?.title === 'string' && `${body.title} (${err.status}).`)
              || `Unexpected error (${err.status}).`,
          );
        },
      });
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
