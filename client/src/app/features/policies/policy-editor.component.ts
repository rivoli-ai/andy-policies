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
import {
  AbstractControl,
  FormControl,
  FormGroup,
  NonNullableFormBuilder,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  ApiService,
  CreatePolicyRequest,
  Enforcement,
  PolicyDto,
  PolicyVersionDto,
  Severity,
  UpdatePolicyVersionRequest,
} from '../../shared/services/api.service';
import { ScopeChipInputComponent } from './scope-chip-input.component';

type EditorMode = 'create' | 'edit';

interface EditorForm {
  name: FormControl<string>;
  description: FormControl<string>;
  summary: FormControl<string>;
  enforcement: FormControl<Enforcement>;
  severity: FormControl<Severity>;
  scopes: FormControl<string[]>;
  rulesJson: FormControl<string>;
}

/**
 * P9.2 (rivoli-ai/andy-policies#67) — policy editor for draft create + edit.
 * Reactive form with dimension pickers, scope chip input, and a JSON textarea
 * with client-side `JSON.parse` validation. Monaco editor + JSON-schema-aware
 * validation are deferred (no `/api/schemas/rules.json` endpoint exists yet).
 *
 * Two routes wire to the same component via `mode`:
 *   - `/policies/new` — empty form, name + description editable, calls `POST /api/policies`
 *   - `/policies/:id/versions/:vId/edit` — populates from `GET /api/policies/{id}/versions/{vId}`,
 *     calls `PUT /api/policies/{id}/versions/{vId}` on save. State must be `Draft`;
 *     non-draft versions redirect back to the list with a banner.
 */
@Component({
  selector: 'app-policy-editor',
  standalone: true,
  imports: [CommonModule, ReactiveFormsModule, RouterLink, ScopeChipInputComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './policy-editor.component.html',
  styleUrls: ['./policy-editor.component.scss'],
})
export class PolicyEditorComponent {
  static readonly slugPattern = /^[a-z0-9][a-z0-9:._-]*$/;
  static readonly defaultRulesJson = '{\n  "allow": [],\n  "deny": []\n}\n';

  private readonly api = inject(ApiService);
  private readonly fb = inject(NonNullableFormBuilder);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly mode = signal<EditorMode>('create');
  readonly policy = signal<PolicyDto | null>(null);
  readonly version = signal<PolicyVersionDto | null>(null);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly errorMessage = signal<string | null>(null);

  readonly enforcementOptions: Enforcement[] = ['MUST', 'SHOULD', 'MAY'];
  readonly severityOptions: Severity[] = ['info', 'moderate', 'critical'];

  readonly form: FormGroup<EditorForm> = this.fb.group<EditorForm>({
    name: this.fb.control('', [
      Validators.required,
      Validators.pattern(PolicyEditorComponent.slugPattern),
    ]),
    description: this.fb.control(''),
    summary: this.fb.control('', Validators.required),
    enforcement: this.fb.control<Enforcement>('SHOULD'),
    severity: this.fb.control<Severity>('moderate'),
    scopes: this.fb.control<string[]>([]),
    rulesJson: this.fb.control(PolicyEditorComponent.defaultRulesJson, [
      Validators.required,
      jsonValidator,
    ]),
  });

  readonly rulesJsonError = computed(() => {
    const c = this.form.controls.rulesJson;
    if (!c.touched && !c.dirty) return null;
    const errors = c.errors;
    if (!errors) return null;
    if (errors['required']) return 'Rules JSON is required.';
    if (errors['json']) return errors['json'] as string;
    return null;
  });

  readonly canSave = computed(() => {
    if (this.saving() || this.loading()) return false;
    const status = this.formStatus();
    return status === 'VALID';
  });

  /** Signal mirror of the form status. Drives template-side disable on save. */
  private readonly formStatus = signal<string>('INVALID');

  constructor() {
    // Decide mode from route params; switch the form layout via signals.
    const params = this.route.snapshot.paramMap;
    const policyId = params.get('id');
    const versionId = params.get('vId');

    this.form.statusChanges
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(s => this.formStatus.set(s));
    this.formStatus.set(this.form.status);

    if (policyId && versionId) {
      this.mode.set('edit');
      this.loadForEdit(policyId, versionId);
    } else {
      this.mode.set('create');
    }
  }

  save(): void {
    if (!this.canSave()) return;
    this.saving.set(true);
    this.errorMessage.set(null);

    const v = this.form.getRawValue();

    if (this.mode() === 'create') {
      const req: CreatePolicyRequest = {
        name: v.name,
        description: v.description || null,
        summary: v.summary,
        enforcement: v.enforcement,
        severity: v.severity,
        scopes: v.scopes,
        rulesJson: v.rulesJson,
      };
      this.api
        .createPolicy(req)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: created => {
            this.saving.set(false);
            // Created policy id is on the version DTO; redirect to edit
            // mode so the author can keep iterating without losing context.
            this.router.navigate([
              '/policies',
              created.policyId,
              'versions',
              created.id,
              'edit',
            ]);
          },
          error: err => this.handleSaveError(err),
        });
    } else {
      const ver = this.version();
      if (!ver) {
        this.saving.set(false);
        return;
      }
      const req: UpdatePolicyVersionRequest = {
        summary: v.summary,
        enforcement: v.enforcement,
        severity: v.severity,
        scopes: v.scopes,
        rulesJson: v.rulesJson,
      };
      this.api
        .updatePolicyVersion(ver.policyId, ver.id, req)
        .pipe(takeUntilDestroyed(this.destroyRef))
        .subscribe({
          next: updated => {
            this.saving.set(false);
            this.version.set(updated);
            this.errorMessage.set(null);
          },
          error: err => this.handleSaveError(err),
        });
    }
  }

  cancel(): void {
    this.router.navigate(['/policies']);
  }

  private loadForEdit(policyId: string, versionId: string): void {
    this.loading.set(true);
    this.api
      .getPolicyVersion(policyId, versionId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ver => {
          if (ver.state !== 'Draft') {
            this.loading.set(false);
            this.router.navigate(['/policies'], {
              queryParams: { error: 'only-draft-editable' },
            });
            return;
          }
          this.version.set(ver);
          // Patch the version-level fields; lock name + description (Policy
          // is write-once for those — see api.service.ts).
          this.form.patchValue({
            summary: ver.summary,
            enforcement: ver.enforcement,
            severity: ver.severity,
            scopes: ver.scopes,
            rulesJson: ver.rulesJson,
          });
          this.form.controls.name.disable();
          this.form.controls.description.disable();
          this.api
            .getPolicy(ver.policyId)
            .pipe(takeUntilDestroyed(this.destroyRef))
            .subscribe({
              next: p => {
                this.policy.set(p);
                this.form.controls.name.setValue(p.name);
                this.form.controls.description.setValue(p.description ?? '');
                this.loading.set(false);
              },
              error: () => this.loading.set(false), // Header data is non-critical for save.
            });
        },
        error: err => {
          this.loading.set(false);
          this.errorMessage.set(this.describeError(err));
        },
      });
  }

  private handleSaveError(err: HttpErrorResponse): void {
    this.saving.set(false);
    this.errorMessage.set(this.describeError(err));
  }

  private describeError(err: HttpErrorResponse): string {
    if (err.error?.title) return `${err.error.title} (${err.status}).`;
    if (err.message) return err.message;
    return `Unexpected error (${err.status}).`;
  }
}

/** Validates that the control's value parses as JSON and isn't a primitive
 *  (the rules block should be an object). */
function jsonValidator(control: AbstractControl): ValidationErrors | null {
  const raw = control.value;
  if (typeof raw !== 'string' || raw.trim() === '') return null;
  try {
    const parsed = JSON.parse(raw);
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return { json: 'Rules must be a JSON object.' };
    }
    return null;
  } catch (e) {
    return {
      json: e instanceof Error ? `Invalid JSON: ${e.message}` : 'Invalid JSON.',
    };
  }
}
