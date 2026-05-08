// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import {
  ApiService,
  OverrideDto,
  PolicyVersionDto,
  ProposeOverrideRequest,
} from '../../shared/services/api.service';
import { ProposeOverrideModalComponent } from './propose-override-modal.component';

describe('ProposeOverrideModalComponent (#200)', () => {
  let fixture: ComponentFixture<ProposeOverrideModalComponent>;
  let component: ProposeOverrideModalComponent;
  let api: jasmine.SpyObj<ApiService>;

  const v1: PolicyVersionDto = {
    id: 'vid-1',
    policyId: 'pid-1',
    version: 1,
    state: 'Active',
    enforcement: 'MUST',
    severity: 'critical',
    scopes: ['prod'],
    summary: 'baseline',
    rulesJson: '{}',
    createdAt: '2026-04-01T00:00:00Z',
    createdBySubjectId: 'user:alice',
    proposerSubjectId: 'user:alice',
  };
  const v2: PolicyVersionDto = { ...v1, id: 'vid-2', version: 2, state: 'Draft' };

  function build(replacementCandidates: PolicyVersionDto[] = []): void {
    TestBed.resetTestingModule();
    api = jasmine.createSpyObj<ApiService>('ApiService', ['proposeOverride']);
    TestBed.configureTestingModule({
      imports: [ProposeOverrideModalComponent],
      providers: [{ provide: ApiService, useValue: api }],
    });

    fixture = TestBed.createComponent(ProposeOverrideModalComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('version', v1);
    fixture.componentRef.setInput('replacementCandidates', replacementCandidates);
    component.ngOnInit();
    fixture.detectChanges();
  }

  it('starts with Effect=Exempt and the Replace dropdown is hidden', () => {
    build();
    const dropdown = fixture.nativeElement
      .querySelector('[data-testid="replacementPolicyVersionId"]');
    expect(dropdown).toBeNull(
      'Exempt: replacement dropdown is *ngIf-d out of the DOM');
    expect(component.isReplace()).toBeFalse();
  });

  it('switching to Replace reveals the dropdown and validates non-null replacement', () => {
    build([v2]);

    component.form.controls.effect.setValue('Replace');
    fixture.detectChanges();

    const dropdown = fixture.nativeElement
      .querySelector('[data-testid="replacementPolicyVersionId"]');
    expect(dropdown).toBeTruthy();
    expect(component.isReplace()).toBeTrue();

    // Cross-field invariant: Replace + null replacement → form invalid.
    expect(component.form.errors?.['replacementRequired']).toBeTrue();

    component.form.controls.replacementPolicyVersionId.setValue(v2.id);
    expect(component.form.errors).toBeNull();
  });

  it('switching from Replace back to Exempt clears any replacement value', () => {
    build([v2]);
    component.form.controls.effect.setValue('Replace');
    component.form.controls.replacementPolicyVersionId.setValue(v2.id);
    component.form.controls.effect.setValue('Exempt');
    expect(component.form.controls.replacementPolicyVersionId.value).toBeNull(
      'switching to Exempt resets the dropdown — keeps the form value-shape valid');
    expect(component.form.errors).toBeNull(
      'invariant satisfied: Exempt with null replacement');
  });

  it('submit posts a well-formed ProposeOverrideRequest and emits the created DTO', () => {
    build();
    const created: OverrideDto = {
      id: 'oid-1',
      policyVersionId: v1.id,
      scopeKind: 'Principal',
      scopeRef: 'user:bob',
      effect: 'Exempt',
      replacementPolicyVersionId: null,
      proposerSubjectId: 'user:alice',
      approverSubjectId: null,
      state: 'Proposed',
      proposedAt: '2026-05-08T00:00:00Z',
      approvedAt: null,
      expiresAt: '2026-05-09T00:00:00Z',
      rationale: 'temporary exemption for the alpha rollout',
      revocationReason: null,
    };
    api.proposeOverride.and.returnValue(of(created));
    const emittedSpy = jasmine.createSpy<(v: OverrideDto | null) => void>('closed');
    component.closed.subscribe(emittedSpy);

    component.form.patchValue({
      scopeKind: 'Principal',
      scopeRef: 'user:bob',
      effect: 'Exempt',
      rationale: 'temporary exemption for the alpha rollout',
      // expiresAt defaults to now+1 day; leave it.
    });
    component.submit();

    expect(api.proposeOverride).toHaveBeenCalledTimes(1);
    const sent: ProposeOverrideRequest = api.proposeOverride.calls.mostRecent().args[0];
    expect(sent.policyVersionId).toBe(v1.id);
    expect(sent.scopeKind).toBe('Principal');
    expect(sent.scopeRef).toBe('user:bob');
    expect(sent.effect).toBe('Exempt');
    expect(sent.replacementPolicyVersionId).toBeNull();
    // ISO-8601 with explicit timezone (server expects DateTimeOffset).
    expect(sent.expiresAt).toMatch(/Z$|[+-]\d\d:\d\d$/);

    expect(emittedSpy).toHaveBeenCalledOnceWith(created);
  });

  it('403 surfaces an inline error and keeps the modal open', () => {
    build();
    const denied = new HttpErrorResponse({ status: 403, error: { detail: 'gate off' } });
    api.proposeOverride.and.returnValue(throwError(() => denied));
    let closedFires = 0;
    component.closed.subscribe(() => closedFires++);

    component.form.patchValue({
      scopeKind: 'Principal',
      scopeRef: 'user:bob',
      effect: 'Exempt',
      rationale: 'temporary exemption for the alpha rollout',
    });
    component.submit();

    expect(component.errorMessage()).toContain('do not have permission');
    expect(component.submitting()).toBeFalse();
    expect(closedFires).toBe(0,
      'closed never fires on 403 — modal stays open for the user to retry');
  });
});
