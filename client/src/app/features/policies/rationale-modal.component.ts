// Copyright (c) Rivoli AI 2026. All rights reserved.

import { CommonModule } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  Input,
  Output,
  signal,
} from '@angular/core';
import {
  FormControl,
  FormGroup,
  NonNullableFormBuilder,
  ReactiveFormsModule,
  Validators,
} from '@angular/forms';

interface RationaleForm {
  rationale: FormControl<string>;
}

/**
 * P9.3 (#68) — generic rationale-only modal used by Propose and Reject
 * (and any future single-rationale handoff). Symmetric with
 * `LifecycleTransitionModalComponent` but doesn't carry the lifecycle
 * graph rendering since these transitions don't change `state`.
 *
 * The host owns the HTTP call: this modal validates that the rationale
 * is non-empty + at least 10 chars (matches the lifecycle modal's
 * convention) and emits the trimmed value on submit. The host invokes
 * the API, handles 4xx/5xx, and either re-shows an inline error via
 * the {@link errorMessage} input or closes via {@link cancel}.
 */
@Component({
  selector: 'app-rationale-modal',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="rationale-modal-backdrop" (click)="cancel()">
      <div class="rationale-modal" (click)="$event.stopPropagation()">
        <header>
          <h2>{{ title }}</h2>
          <p class="subtitle">{{ subtitle }}</p>
        </header>

        <form [formGroup]="form" (ngSubmit)="submit()">
          <label>
            <span>Rationale</span>
            <textarea
              formControlName="rationale"
              rows="4"
              [placeholder]="placeholder"
              [disabled]="submitting()"></textarea>
            <small *ngIf="form.controls.rationale.touched && form.controls.rationale.invalid">
              Rationale is required (min {{ minRationaleLength }} chars).
            </small>
          </label>

          <p *ngIf="errorMessage" class="error-banner">{{ errorMessage }}</p>

          <div class="actions">
            <button type="button" class="btn-secondary" (click)="cancel()" [disabled]="submitting()">Cancel</button>
            <button type="submit" class="btn-primary" [disabled]="form.invalid || submitting()">
              {{ submitting() ? 'Working…' : confirmLabel }}
            </button>
          </div>
        </form>
      </div>
    </div>
  `,
  styles: [`
    .rationale-modal-backdrop {
      position: fixed; inset: 0; background: rgba(0, 0, 0, 0.4);
      display: flex; align-items: center; justify-content: center; z-index: 1000;
    }
    .rationale-modal {
      background: white; padding: 1.5rem; border-radius: 6px;
      max-width: 480px; width: 90%; box-shadow: 0 10px 25px rgba(0, 0, 0, 0.2);
    }
    h2 { margin: 0 0 0.25rem; font-size: 1.25rem; }
    .subtitle { margin: 0 0 1rem; color: #555; }
    label { display: flex; flex-direction: column; gap: 0.25rem; margin-bottom: 1rem; }
    textarea { font-family: inherit; font-size: 0.95rem; padding: 0.5rem; resize: vertical; }
    small { color: #b00; }
    .error-banner { color: #b00; margin: 0 0 1rem; }
    .actions { display: flex; justify-content: flex-end; gap: 0.5rem; }
  `],
})
export class RationaleModalComponent {
  static readonly minRationaleLength = 10;

  @Input({ required: true }) title!: string;
  @Input() subtitle = '';
  @Input() placeholder = '';
  @Input() confirmLabel = 'Confirm';
  /** Inline error banner — set by the host after a failed API call. */
  @Input() errorMessage: string | null = null;

  @Output() readonly confirmed = new EventEmitter<string>();
  @Output() readonly cancelled = new EventEmitter<void>();

  readonly minRationaleLength = RationaleModalComponent.minRationaleLength;
  readonly submitting = signal(false);

  readonly form: FormGroup<RationaleForm>;

  constructor(fb: NonNullableFormBuilder) {
    this.form = fb.group<RationaleForm>({
      rationale: fb.control('', [
        Validators.required,
        Validators.minLength(RationaleModalComponent.minRationaleLength),
      ]),
    });
  }

  /** Hosts call this to re-enable the form after the API errored. */
  resetSubmitting(): void {
    this.submitting.set(false);
  }

  submit(): void {
    if (this.form.invalid || this.submitting()) return;
    this.submitting.set(true);
    this.confirmed.emit(this.form.controls.rationale.value.trim());
  }

  cancel(): void {
    if (this.submitting()) return;
    this.cancelled.emit();
  }
}
