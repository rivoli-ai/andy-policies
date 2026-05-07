// Copyright (c) Rivoli AI 2026. All rights reserved.

import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  EventEmitter,
  Input,
  Output,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  FormControl,
  FormGroup,
  NonNullableFormBuilder,
  ReactiveFormsModule,
  Validators,
} from '@angular/forms';
import {
  ApiService,
  LifecycleState,
  PolicyVersionDto,
} from '../../shared/services/api.service';
import { LifecycleDiagramComponent } from './lifecycle-diagram.component';
import {
  LIFECYCLE_GRAPH,
  LIFECYCLE_LABEL,
  TRANSITION_HINT,
} from './lifecycle-graph';

interface TransitionForm {
  targetState: FormControl<LifecycleState>;
  rationale: FormControl<string>;
}

/**
 * P9.4 (rivoli-ai/andy-policies#69) — modal that wraps a single lifecycle
 * transition. Renders the diagram of the four-state graph, lets the user
 * choose a legal target (computed from `version.state` against
 * `LIFECYCLE_GRAPH`), and forwards a non-empty rationale to the server.
 *
 * Server is authoritative: illegal target → 409, missing rationale → 400.
 * On 409 the modal stays open so the user can adjust rationale or cancel;
 * on success the modal emits the updated DTO via `closed` so the host can
 * refresh without a full page reload.
 */
@Component({
  selector: 'app-lifecycle-transition-modal',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, LifecycleDiagramComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './lifecycle-transition-modal.component.html',
  styleUrls: ['./lifecycle-transition-modal.component.scss'],
})
export class LifecycleTransitionModalComponent {
  static readonly minRationaleLength = 10;

  @Input({ required: true }) version!: PolicyVersionDto;
  @Output() readonly closed = new EventEmitter<PolicyVersionDto | null>();

  private readonly api = inject(ApiService);
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly destroyRef = inject(DestroyRef);

  readonly submitting = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly LIFECYCLE_LABEL = LIFECYCLE_LABEL;
  readonly TRANSITION_HINT = TRANSITION_HINT;

  readonly form: FormGroup<TransitionForm> = this.fb.group<TransitionForm>({
    targetState: this.fb.control<LifecycleState>('Active', Validators.required),
    rationale: this.fb.control('', [
      Validators.required,
      Validators.minLength(LifecycleTransitionModalComponent.minRationaleLength),
    ]),
  });

  readonly legalTargets = computed<LifecycleState[]>(() =>
    LIFECYCLE_GRAPH[this.version?.state ?? 'Retired'] ?? [],
  );

  readonly hasNoTargets = computed(() => this.legalTargets().length === 0);

  ngOnInit(): void {
    const targets = this.legalTargets();
    if (targets.length > 0) {
      this.form.controls.targetState.setValue(targets[0]);
    }
  }

  submit(): void {
    if (this.form.invalid || this.submitting()) return;
    const { targetState, rationale } = this.form.getRawValue();

    this.submitting.set(true);
    this.errorMessage.set(null);
    this.api
      .transitionPolicyVersion(this.version.policyId, this.version.id, targetState, rationale)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: updated => {
          this.submitting.set(false);
          this.closed.emit(updated);
        },
        error: (err: HttpErrorResponse) => {
          this.submitting.set(false);
          // 409 = illegal transition / concurrent change. Keep the modal
          // open so the user can adjust or cancel.
          if (err.status === 409) {
            this.errorMessage.set(this.describeProblem(err)
              ?? 'Transition rejected by the server. Refresh and try again.');
            return;
          }
          this.errorMessage.set(this.describeProblem(err) ?? `Unexpected error (${err.status}).`);
        },
      });
  }

  cancel(): void {
    this.closed.emit(null);
  }

  /** Pull `detail` out of an RFC 7807 ProblemDetails body, falling back to
   *  `title` then a generic. */
  private describeProblem(err: HttpErrorResponse): string | null {
    const body = err.error;
    if (!body) return null;
    if (typeof body.detail === 'string' && body.detail) return body.detail;
    if (typeof body.title === 'string' && body.title) return `${body.title} (${err.status}).`;
    return null;
  }
}
