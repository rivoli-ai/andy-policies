// Copyright (c) Rivoli AI 2026. All rights reserved.

import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  EventEmitter,
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
  BundleDto,
  CreateBundleRequest,
} from '../../shared/services/api.service';

interface CreateBundleForm {
  name: FormControl<string>;
  description: FormControl<string>;
  rationale: FormControl<string>;
}

/**
 * P9.8 (rivoli-ai/andy-policies#90) — modal that creates a new bundle.
 * Server's `CreateBundleRequest` carries `Name`, `Description`,
 * `Rationale` only — no `includeOverrides` flag yet (filed as a
 * follow-up). 409 paths are surfaced inline (e.g. duplicate name).
 */
@Component({
  selector: 'app-create-bundle-modal',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './create-bundle-modal.component.html',
  styleUrls: ['./create-bundle-modal.component.scss'],
})
export class CreateBundleModalComponent {
  static readonly slugPattern = /^[a-z0-9][a-z0-9.-]*$/;
  static readonly minRationaleLength = 10;

  @Output() readonly closed = new EventEmitter<BundleDto | null>();

  private readonly api = inject(ApiService);
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly destroyRef = inject(DestroyRef);

  readonly submitting = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form: FormGroup<CreateBundleForm> = this.fb.group<CreateBundleForm>({
    name: this.fb.control('', [
      Validators.required,
      Validators.pattern(CreateBundleModalComponent.slugPattern),
    ]),
    description: this.fb.control('', Validators.maxLength(500)),
    rationale: this.fb.control('', [
      Validators.required,
      Validators.minLength(CreateBundleModalComponent.minRationaleLength),
    ]),
  });

  submit(): void {
    if (this.form.invalid || this.submitting()) return;

    const v = this.form.getRawValue();
    const req: CreateBundleRequest = {
      name: v.name.trim(),
      description: v.description.trim() || null,
      rationale: v.rationale.trim(),
    };

    this.submitting.set(true);
    this.errorMessage.set(null);
    this.api
      .createBundle(req)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: created => {
          this.submitting.set(false);
          this.closed.emit(created);
        },
        error: (err: HttpErrorResponse) => {
          this.submitting.set(false);
          this.errorMessage.set(this.describeProblem(err)
            ?? `Unexpected error (${err.status}).`);
        },
      });
  }

  cancel(): void {
    this.closed.emit(null);
  }

  private describeProblem(err: HttpErrorResponse): string | null {
    const body = err.error;
    if (!body) return null;
    if (typeof body.detail === 'string' && body.detail) return body.detail;
    if (typeof body.title === 'string') return `${body.title} (${err.status}).`;
    return null;
  }
}
