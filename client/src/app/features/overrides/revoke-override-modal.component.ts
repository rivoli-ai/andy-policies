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
import { ApiService, OverrideDto } from '../../shared/services/api.service';

interface RevokeForm {
  revocationReason: FormControl<string>;
}

/**
 * P9.6 (rivoli-ai/andy-policies#88) — modal that captures a revocation
 * reason and posts it to `POST /api/overrides/{id}/revoke`. Note the
 * server expects `revocationReason`, not `rationale` — the field name
 * from `RevokeOverrideRequest` is reflected here.
 *
 * 403 paths are surfaced inline (not as a toast) so the user stays in
 * context; common cause is the `[OverrideWriteGate]` server-side
 * filter when `andy.policies.experimentalOverridesEnabled = false`.
 */
@Component({
  selector: 'app-revoke-override-modal',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="overlay" (click)="cancel()" data-testid="overlay">
      <div
        class="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="revoke-override-title"
        (click)="$event.stopPropagation()">
        <header class="modal-header">
          <h2 id="revoke-override-title">Revoke override</h2>
          <p class="subtitle">
            <strong>{{ override.scopeKind }}</strong>
            <code class="ref">{{ override.scopeRef }}</code>
            · {{ override.effect }}
          </p>
        </header>

        <form [formGroup]="form" (ngSubmit)="submit()" class="form">
          <label class="field">
            <span>
              Revocation reason
              <em>(required, min 10 chars — recorded in the audit chain)</em>
            </span>
            <textarea
              formControlName="revocationReason"
              class="input rationale"
              rows="3"
              data-testid="reason"
              placeholder="Why is this override being revoked?"></textarea>
          </label>

          <div *ngIf="errorMessage()" class="banner-error" role="alert" data-testid="banner">
            {{ errorMessage() }}
          </div>

          <footer class="modal-footer">
            <button type="button" class="btn-secondary" (click)="cancel()" [disabled]="submitting()">
              Cancel
            </button>
            <button
              type="submit"
              class="btn-danger"
              [disabled]="form.invalid || submitting()"
              data-testid="submit">
              <span *ngIf="!submitting()">Revoke</span>
              <span *ngIf="submitting()">Revoking…</span>
            </button>
          </footer>
        </form>
      </div>
    </div>
  `,
  styles: [`
    .overlay {
      position: fixed; inset: 0;
      background: rgba(0, 0, 0, 0.45);
      display: flex; align-items: center; justify-content: center;
      z-index: 1000; padding: 16px;
    }
    .modal {
      background: var(--surface);
      border-radius: 8px;
      border: 1px solid var(--border);
      padding: 20px 24px;
      width: 100%; max-width: 480px;
      display: flex; flex-direction: column; gap: 14px;
      box-shadow: 0 12px 40px rgba(0, 0, 0, 0.18);
    }
    .modal-header h2 { margin: 0 0 4px; font-size: 18px; }
    .subtitle { margin: 0; font-size: 13px; color: var(--text-secondary); }
    .ref { background: var(--background); padding: 1px 6px; border-radius: 3px; font-size: 12px; }
    .form { display: flex; flex-direction: column; gap: 12px; }
    .field { display: flex; flex-direction: column; gap: 4px; font-size: 14px; }
    .field > span {
      color: var(--text-secondary);
      font-size: 12px;
      font-weight: 500;
      em { font-weight: 400; font-style: normal; }
    }
    .input {
      padding: 8px 12px;
      border: 1px solid var(--border);
      border-radius: 4px;
      background: var(--surface);
      color: inherit;
      font-size: 14px;
      width: 100%;
    }
    .rationale { resize: vertical; min-height: 70px; font-family: inherit; }
    .banner-error {
      padding: 10px 14px;
      border-radius: 6px;
      font-size: 13px;
      background: #fce8e6;
      border: 1px solid #f0a99a;
      color: var(--error);
    }
    .modal-footer {
      display: flex; justify-content: flex-end; gap: 8px;
      margin-top: 4px;
    }
    .btn-secondary, .btn-danger {
      padding: 8px 16px; font-size: 14px; border-radius: 4px;
      cursor: pointer; border: 1px solid var(--border);
    }
    .btn-secondary { background: var(--surface); color: inherit; }
    .btn-secondary:hover:not([disabled]) { background: var(--background); }
    .btn-danger {
      background: var(--error);
      color: white;
      border-color: var(--error);
    }
    .btn-danger[disabled] { opacity: 0.55; cursor: not-allowed; }
  `],
})
export class RevokeOverrideModalComponent {
  static readonly minReasonLength = 10;

  @Input({ required: true }) override!: OverrideDto;
  @Output() readonly closed = new EventEmitter<OverrideDto | null>();

  private readonly api = inject(ApiService);
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly destroyRef = inject(DestroyRef);

  readonly submitting = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly form: FormGroup<RevokeForm> = this.fb.group<RevokeForm>({
    revocationReason: this.fb.control('', [
      Validators.required,
      Validators.minLength(RevokeOverrideModalComponent.minReasonLength),
    ]),
  });

  submit(): void {
    if (this.form.invalid || this.submitting()) return;

    const reason = this.form.controls.revocationReason.value.trim();
    this.submitting.set(true);
    this.errorMessage.set(null);
    this.api
      .revokeOverride(this.override.id, reason)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: updated => {
          this.submitting.set(false);
          this.closed.emit(updated);
        },
        error: (err: HttpErrorResponse) => {
          this.submitting.set(false);
          if (err.status === 403) {
            this.errorMessage.set(
              this.describeProblem(err)
              ?? 'Overrides are experimentally disabled by this deployment.',
            );
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

  private describeProblem(err: HttpErrorResponse): string | null {
    const body = err.error;
    if (!body) return null;
    if (typeof body.detail === 'string' && body.detail) return body.detail;
    if (typeof body.title === 'string') return `${body.title} (${err.status}).`;
    return null;
  }
}
