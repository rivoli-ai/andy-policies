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
import { FormsModule } from '@angular/forms';
import { ApiService, BindingDto } from '../../shared/services/api.service';

/**
 * P9.5 (rivoli-ai/andy-policies#70) — confirmation dialog for deleting a
 * binding. Captures a rationale (recorded in the audit chain via the
 * server's `?rationale=` query param) and surfaces 409 ProblemDetails
 * inline so the user stays in context — the spec asked for inline
 * (not a toast) and that's what we do.
 *
 * Server-side note: the tighten-only check fires on **create**, not
 * delete; deletes are unconditional once the caller has
 * `andy-policies:binding:manage`. The 409 path is therefore unlikely
 * for delete in normal operation, but defensive coverage stays since
 * any conflict (e.g. concurrent deletion) can still surface here.
 */
@Component({
  selector: 'app-delete-binding-modal',
  standalone: true,
  imports: [CommonModule, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="overlay" (click)="cancel()" data-testid="overlay">
      <div
        class="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="delete-binding-title"
        (click)="$event.stopPropagation()">
        <header class="modal-header">
          <h2 id="delete-binding-title">Delete binding?</h2>
          <p class="subtitle">
            <strong>{{ binding.targetType }}</strong>
            <code class="ref">{{ binding.targetRef }}</code>
            ·
            <span class="strength" [class.mandatory]="binding.bindStrength === 'Mandatory'">
              {{ binding.bindStrength }}
            </span>
          </p>
        </header>

        <label class="field">
          <span>
            Rationale
            <em>(optional, recorded in the audit chain)</em>
          </span>
          <textarea
            class="input rationale"
            rows="3"
            [(ngModel)]="rationale"
            data-testid="rationale"
            placeholder="Why is this binding being removed?"></textarea>
        </label>

        <div *ngIf="errorMessage()" class="banner-error" role="alert" data-testid="banner">
          {{ errorMessage() }}
        </div>

        <footer class="modal-footer">
          <button type="button" class="btn-secondary" (click)="cancel()" [disabled]="submitting()">
            Cancel
          </button>
          <button
            type="button"
            class="btn-danger"
            (click)="confirm()"
            [disabled]="submitting()"
            data-testid="confirm">
            <span *ngIf="!submitting()">Delete</span>
            <span *ngIf="submitting()">Deleting…</span>
          </button>
        </footer>
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
    .ref {
      background: var(--background);
      padding: 1px 6px;
      border-radius: 3px;
      font-size: 12px;
    }
    .strength { padding: 1px 8px; border-radius: 12px; background: var(--background); font-size: 11px; }
    .strength.mandatory { background: #fce8e6; color: var(--error); }
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
export class DeleteBindingModalComponent {
  @Input({ required: true }) binding!: BindingDto;
  @Output() readonly closed = new EventEmitter<{ deleted: boolean; bindingId: string } | null>();

  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  rationale = '';
  readonly submitting = signal(false);
  readonly errorMessage = signal<string | null>(null);

  confirm(): void {
    if (this.submitting()) return;

    this.submitting.set(true);
    this.errorMessage.set(null);
    this.api
      .deleteBinding(this.binding.id, this.rationale.trim())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.submitting.set(false);
          this.closed.emit({ deleted: true, bindingId: this.binding.id });
        },
        error: (err: HttpErrorResponse) => {
          this.submitting.set(false);
          // Inline 409 — modal stays open per the epic spec.
          if (err.status === 409) {
            this.errorMessage.set(this.describeProblem(err)
              ?? 'Deletion was rejected by the server.');
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
