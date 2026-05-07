// Copyright (c) Rivoli AI 2026. All rights reserved.

import { CommonModule } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostBinding,
  ViewChild,
  forwardRef,
  signal,
} from '@angular/core';
import { ControlValueAccessor, FormsModule, NG_VALUE_ACCESSOR } from '@angular/forms';

/**
 * P9.2 (rivoli-ai/andy-policies#67) — chip-style input for the `scopes` array.
 * Reactive-forms compatible (`ControlValueAccessor`); accepts comma or Enter
 * as the chip separator and rejects values that don't match the same slug
 * regex the backend enforces in P1.2 (`^[a-z0-9][a-z0-9:._-]*$`).
 */
@Component({
  selector: 'app-scope-chip-input',
  standalone: true,
  imports: [CommonModule, FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="chips-row" data-testid="chips">
      <span class="chip" *ngFor="let s of value(); let i = index" [attr.data-testid]="'chip-' + i">
        <span class="chip-label">{{ s }}</span>
        <button
          type="button"
          class="chip-remove"
          [attr.aria-label]="'Remove scope ' + s"
          (click)="remove(i)">×</button>
      </span>
      <input
        #input
        class="chip-input"
        type="text"
        [placeholder]="placeholder"
        [(ngModel)]="draft"
        (keydown)="onKeyDown($event)"
        (blur)="commit()"
        [attr.aria-label]="ariaLabel"
        [disabled]="disabled()"
        data-testid="scope-input" />
    </div>
    <p *ngIf="error()" class="chip-error" role="alert" data-testid="chip-error">
      {{ error() }}
    </p>
  `,
  styles: [`
    :host { display: block; }
    .chips-row {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      align-items: center;
      padding: 6px;
      border: 1px solid var(--border);
      border-radius: 4px;
      background: var(--surface);
      min-height: 38px;
    }
    .chip {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 2px 4px 2px 10px;
      background: var(--background);
      border: 1px solid var(--border);
      border-radius: 12px;
      font-size: 12px;
    }
    .chip-label { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
    .chip-remove {
      border: none;
      background: transparent;
      cursor: pointer;
      font-size: 14px;
      line-height: 1;
      padding: 2px 6px;
      color: var(--text-secondary);
    }
    .chip-remove:hover { color: var(--error); }
    .chip-input {
      flex: 1;
      min-width: 120px;
      border: none;
      outline: none;
      background: transparent;
      font-size: 13px;
      padding: 4px;
    }
    .chip-error {
      margin: 4px 0 0;
      color: var(--error);
      font-size: 12px;
    }
  `],
  providers: [
    {
      provide: NG_VALUE_ACCESSOR,
      useExisting: forwardRef(() => ScopeChipInputComponent),
      multi: true,
    },
  ],
})
export class ScopeChipInputComponent implements ControlValueAccessor {
  /** Backend slug regex from P1.2. Kept here so the chip input can pre-validate
   *  before the API rejects with 400 — saves a round-trip and is visible to authors. */
  static readonly slugPattern = /^[a-z0-9][a-z0-9:._-]*$/;

  @HostBinding('attr.role') role = 'group';
  @ViewChild('input') inputRef?: ElementRef<HTMLInputElement>;

  placeholder = 'add scope (e.g. project:frontend)';
  ariaLabel = 'Scopes';

  readonly value = signal<string[]>([]);
  readonly disabled = signal(false);
  readonly error = signal<string | null>(null);
  draft = '';

  private onChange: (v: string[]) => void = () => {};
  private onTouched: () => void = () => {};

  // ControlValueAccessor.
  writeValue(v: string[] | null): void {
    this.value.set(Array.isArray(v) ? [...v] : []);
  }
  registerOnChange(fn: (v: string[]) => void): void { this.onChange = fn; }
  registerOnTouched(fn: () => void): void { this.onTouched = fn; }
  setDisabledState(isDisabled: boolean): void { this.disabled.set(isDisabled); }

  onKeyDown(ev: KeyboardEvent): void {
    if (ev.key === 'Enter' || ev.key === ',') {
      ev.preventDefault();
      this.commit();
      return;
    }
    if (ev.key === 'Backspace' && this.draft === '' && this.value().length > 0) {
      this.remove(this.value().length - 1);
    }
  }

  commit(): void {
    const candidate = this.draft.trim();
    this.draft = '';
    if (!candidate) {
      this.error.set(null);
      return;
    }
    if (!ScopeChipInputComponent.slugPattern.test(candidate)) {
      this.error.set(
        `"${candidate}" is not a valid scope. Use lowercase letters/digits with : . _ - separators (e.g. project:frontend).`,
      );
      return;
    }
    if (this.value().includes(candidate)) {
      this.error.set(null);
      return;
    }
    this.value.update(v => [...v, candidate]);
    this.error.set(null);
    this.onChange(this.value());
    this.onTouched();
  }

  remove(index: number): void {
    if (this.disabled()) return;
    this.value.update(v => v.filter((_, i) => i !== index));
    this.onChange(this.value());
    this.onTouched();
  }
}
