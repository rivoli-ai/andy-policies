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
  OnInit,
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
  ValidatorFn,
  Validators,
} from '@angular/forms';
import {
  ApiService,
  OverrideDto,
  OverrideEffect,
  OverrideScopeKind,
  PolicyVersionDto,
  ProposeOverrideRequest,
} from '../../shared/services/api.service';

interface ProposeOverrideForm {
  scopeKind: FormControl<OverrideScopeKind>;
  scopeRef: FormControl<string>;
  effect: FormControl<OverrideEffect>;
  replacementPolicyVersionId: FormControl<string | null>;
  expiresAt: FormControl<string>;
  rationale: FormControl<string>;
}

/**
 * #200 — modal that proposes a new override against a specific
 * `PolicyVersion`. Symmetric with the existing
 * `LifecycleTransitionModalComponent` and `RationaleModalComponent`:
 * the host owns the API call orchestration, the modal owns form
 * shape + client-side invariant checks.
 *
 * Client-side validation mirrors what the server enforces (P5.2):
 *   - `Replace` ⇒ `replacementPolicyVersionId` non-null
 *   - `Exempt` ⇒ `replacementPolicyVersionId` null
 *   - `expiresAt` ≥ now + 1 minute
 *   - `scopeRef` non-empty, ≤ 256 chars
 *   - `rationale` non-empty, ≤ 2000 chars
 *
 * Server is the authoritative gate (validation runs there too); this
 * is just to avoid round-trip delay on form fixups. The modal stays
 * open on 4xx so the user can adjust without losing context.
 */
@Component({
  selector: 'app-propose-override-modal',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './propose-override-modal.component.html',
  styleUrls: ['./propose-override-modal.component.scss'],
})
export class ProposeOverrideModalComponent implements OnInit {
  static readonly minRationaleLength = 10;
  static readonly maxScopeRefLength = 256;
  static readonly maxRationaleLength = 2000;
  /** Server enforces 1-minute MinimumLifetime; pad the client-side
   *  default a bit so a slow form-fill doesn't trip the boundary. */
  static readonly defaultExpiryMs = 24 * 60 * 60 * 1000; // 1 day

  @Input({ required: true }) version!: PolicyVersionDto;

  /**
   * Other versions of the same policy that the user may pick as a
   * Replace target. The host (`PolicyDetailComponent`) supplies them
   * pre-filtered. Excludes Retired (refused server-side) and the
   * version being overridden itself (replacing a version with itself
   * is meaningless).
   */
  @Input() replacementCandidates: PolicyVersionDto[] = [];

  @Output() readonly closed = new EventEmitter<OverrideDto | null>();

  private readonly api = inject(ApiService);
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly destroyRef = inject(DestroyRef);

  readonly submitting = signal(false);
  readonly errorMessage = signal<string | null>(null);

  /** Effect signal — kept in sync with the form's effect control via
   *  `valueChanges` in ngOnInit. Drives the *ngIf on the replacement
   *  version dropdown. Reading `this.form.controls.effect.value`
   *  inside `computed()` wouldn't be reactive (form values aren't
   *  signals); a mirroring signal is the standard pattern. */
  readonly effect = signal<OverrideEffect>('Exempt');
  readonly isReplace = computed(() => this.effect() === 'Replace');

  readonly form: FormGroup<ProposeOverrideForm>;

  /** Template alias — exposes the static for use in the .html error
   *  message. Avoids hard-coding the magic number in the template. */
  readonly minRationaleLengthVal = ProposeOverrideModalComponent.minRationaleLength;

  constructor() {
    this.form = this.fb.group<ProposeOverrideForm>({
      scopeKind: this.fb.control<OverrideScopeKind>('Principal', Validators.required),
      scopeRef: this.fb.control('', [
        Validators.required,
        Validators.maxLength(ProposeOverrideModalComponent.maxScopeRefLength),
      ]),
      effect: this.fb.control<OverrideEffect>('Exempt', Validators.required),
      // Nullable because Exempt ⇒ null. The form-level validator below
      // enforces the cross-field invariant.
      replacementPolicyVersionId: new FormControl<string | null>(null),
      expiresAt: this.fb.control(this.defaultExpiryIso(), Validators.required),
      rationale: this.fb.control('', [
        Validators.required,
        Validators.minLength(ProposeOverrideModalComponent.minRationaleLength),
        Validators.maxLength(ProposeOverrideModalComponent.maxRationaleLength),
      ]),
    }, { validators: [effectReplacementInvariant] });
  }

  ngOnInit(): void {
    // Reset the replacement when switching to Exempt — keeps the form
    // value-shape valid even if the user toggles back and forth — and
    // mirror the value into the `effect` signal so the *ngIf on the
    // replacement dropdown reacts.
    this.form.controls.effect.valueChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(effect => {
        this.effect.set(effect);
        if (effect === 'Exempt') {
          this.form.controls.replacementPolicyVersionId.setValue(null);
        }
      });
  }

  submit(): void {
    if (this.form.invalid || this.submitting()) return;
    const v = this.form.getRawValue();

    const request: ProposeOverrideRequest = {
      policyVersionId: this.version.id,
      scopeKind: v.scopeKind,
      scopeRef: v.scopeRef.trim(),
      effect: v.effect,
      replacementPolicyVersionId:
        v.effect === 'Replace' ? v.replacementPolicyVersionId : null,
      // <input type="datetime-local"> emits a local-zone string without
      // a timezone suffix. Convert via Date so the ISO-8601 we send
      // carries an explicit Z (server expects DateTimeOffset).
      expiresAt: new Date(v.expiresAt).toISOString(),
      rationale: v.rationale.trim(),
    };

    this.submitting.set(true);
    this.errorMessage.set(null);
    this.api
      .proposeOverride(request)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: created => {
          this.submitting.set(false);
          this.closed.emit(created);
        },
        error: (err: HttpErrorResponse) => {
          this.submitting.set(false);
          this.errorMessage.set(this.describeError(err));
        },
      });
  }

  cancel(): void {
    if (this.submitting()) return;
    this.closed.emit(null);
  }

  private describeError(err: HttpErrorResponse): string {
    if (err.status === 403) {
      return 'You do not have permission to propose an override, ' +
             'or the experimental overrides gate is off.';
    }
    const body = err.error;
    if (typeof body?.detail === 'string' && body.detail) return body.detail;
    if (typeof body?.title === 'string') return `${body.title} (${err.status}).`;
    return `Unexpected error (${err.status}).`;
  }

  /** Default ExpiresAt: now + 1 day, formatted for `<input type="datetime-local">`
   *  (no zone suffix, minutes precision). */
  private defaultExpiryIso(): string {
    const d = new Date(Date.now() + ProposeOverrideModalComponent.defaultExpiryMs);
    // toISOString returns UTC; for datetime-local we want the local-zone
    // wall clock so the user sees a friendly default. Strip seconds.
    const offsetMs = d.getTimezoneOffset() * 60_000;
    const local = new Date(d.getTime() - offsetMs);
    return local.toISOString().slice(0, 16);
  }
}

/**
 * Cross-field invariant: Replace ⇒ replacementPolicyVersionId non-null;
 * Exempt ⇒ replacementPolicyVersionId null. Server-side validation
 * mirrors this exactly (P5.2 / `OverrideService.ProposeAsync`).
 */
const effectReplacementInvariant: ValidatorFn = control => {
  const group = control as FormGroup<ProposeOverrideForm>;
  const effect = group.controls.effect?.value;
  const replacement = group.controls.replacementPolicyVersionId?.value;
  if (effect === 'Replace' && !replacement) {
    return { replacementRequired: true };
  }
  if (effect === 'Exempt' && replacement) {
    return { replacementForbidden: true };
  }
  return null;
};
