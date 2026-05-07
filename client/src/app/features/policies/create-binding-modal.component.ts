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
  BINDING_TARGET_TYPES,
  BindStrength,
  BindingDto,
  BindingTargetType,
  CreateBindingRequest,
  PolicyVersionDto,
} from '../../shared/services/api.service';

interface CreateBindingForm {
  policyVersionId: FormControl<string>;
  targetType: FormControl<BindingTargetType>;
  targetRef: FormControl<string>;
  bindStrength: FormControl<BindStrength>;
}

/**
 * P9.5 (rivoli-ai/andy-policies#70) — modal for creating a binding.
 * Bindings live on a specific PolicyVersion server-side, so the modal
 * forces the user to pick the target version up front (filtered to
 * non-Retired since the server rejects bindings on retired versions
 * with `BindingRetiredVersionException`).
 *
 * Target type is a fixed enum (Template / Repo / ScopeNode / Tenant /
 * Org); target ref is free-text. The autocomplete the spec called for
 * isn't shipped — the server has no /target-refs endpoint to back it.
 *
 * Tighten-only violation (server-side) returns 409 with
 * `errorCode: 'binding.tighten-only-violation'` and structured
 * extensions. The modal stays open and renders the Detail inline so
 * the user can adjust target / strength.
 */
@Component({
  selector: 'app-create-binding-modal',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './create-binding-modal.component.html',
  styleUrls: ['./create-binding-modal.component.scss'],
})
export class CreateBindingModalComponent {
  @Input({ required: true }) versions!: readonly PolicyVersionDto[];
  @Output() readonly closed = new EventEmitter<BindingDto | null>();

  private readonly api = inject(ApiService);
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly destroyRef = inject(DestroyRef);

  readonly TARGET_TYPES = BINDING_TARGET_TYPES;
  readonly submitting = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form: FormGroup<CreateBindingForm> = this.fb.group<CreateBindingForm>({
    policyVersionId: this.fb.control('', Validators.required),
    targetType: this.fb.control<BindingTargetType>('Repo', Validators.required),
    targetRef: this.fb.control('', [Validators.required, Validators.minLength(1)]),
    bindStrength: this.fb.control<BindStrength>('Recommended', Validators.required),
  });

  ngOnInit(): void {
    const eligible = this.eligibleVersions();
    if (eligible.length > 0) {
      this.form.controls.policyVersionId.setValue(eligible[0].id);
    }
  }

  /** Server rejects bindings on Retired versions; filter the dropdown. */
  eligibleVersions(): readonly PolicyVersionDto[] {
    return (this.versions ?? []).filter(v => v.state !== 'Retired');
  }

  submit(): void {
    if (this.form.invalid || this.submitting()) return;

    const v = this.form.getRawValue();
    const req: CreateBindingRequest = {
      policyVersionId: v.policyVersionId,
      targetType: v.targetType,
      targetRef: v.targetRef.trim(),
      bindStrength: v.bindStrength,
    };

    this.submitting.set(true);
    this.errorMessage.set(null);
    this.api
      .createBinding(req)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: created => {
          this.submitting.set(false);
          this.closed.emit(created);
        },
        error: (err: HttpErrorResponse) => {
          this.submitting.set(false);
          // 409 = tighten-only violation or other conflict; surface inline so
          // the user can adjust target / strength without losing the form.
          if (err.status === 409) {
            this.errorMessage.set(this.describeProblem(err)
              ?? 'Binding rejected by the tighten-only validator.');
            return;
          }
          this.errorMessage.set(this.describeProblem(err)
            ?? `Unexpected error (${err.status}).`);
        },
      });
  }

  cancel(): void {
    this.closed.emit(null);
  }

  /** Combines `detail` and any structured extensions for the inline
   *  banner. Tighten-only adds the offending scope id which is helpful
   *  even though it's not human-friendly. */
  private describeProblem(err: HttpErrorResponse): string | null {
    const body = err.error;
    if (!body) return null;
    const parts: string[] = [];
    if (typeof body.detail === 'string' && body.detail) parts.push(body.detail);
    const ext = body.offendingScopeDisplayName ?? body['offendingScopeDisplayName'];
    if (typeof ext === 'string' && ext) {
      parts.push(`Offending scope: ${ext}`);
    }
    if (parts.length > 0) return parts.join(' ');
    if (typeof body.title === 'string') return `${body.title} (${err.status}).`;
    return null;
  }
}
