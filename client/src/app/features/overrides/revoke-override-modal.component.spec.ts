// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { ApiService, OverrideDto } from '../../shared/services/api.service';
import { RevokeOverrideModalComponent } from './revoke-override-modal.component';

describe('RevokeOverrideModalComponent (P9.6)', () => {
  let fixture: ComponentFixture<RevokeOverrideModalComponent>;
  let component: RevokeOverrideModalComponent;
  let api: jasmine.SpyObj<ApiService>;

  const proposedOverride: OverrideDto = {
    id: 'oid-1',
    policyVersionId: 'vid-1',
    scopeKind: 'Principal',
    scopeRef: 'user:alice',
    effect: 'Exempt',
    replacementPolicyVersionId: null,
    proposerSubjectId: 'user:bob',
    approverSubjectId: null,
    state: 'Proposed',
    proposedAt: '2026-05-07T11:00:00Z',
    approvedAt: null,
    expiresAt: '2026-05-08T11:00:00Z',
    rationale: 'temporary exemption',
    revocationReason: null,
  };

  beforeEach(async () => {
    api = jasmine.createSpyObj<ApiService>('ApiService', ['revokeOverride']);
    await TestBed.configureTestingModule({
      imports: [RevokeOverrideModalComponent],
      providers: [{ provide: ApiService, useValue: api }],
    }).compileComponents();

    fixture = TestBed.createComponent(RevokeOverrideModalComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('override', proposedOverride);
    fixture.detectChanges();
  });

  it('Submit is disabled until revocationReason >= 10 chars', () => {
    component.form.controls.revocationReason.setValue('short');
    expect(component.form.invalid).toBeTrue();

    component.form.controls.revocationReason.setValue('exactly 10');
    expect(component.form.valid).toBeTrue();
  });

  it('submit calls revokeOverride with the trimmed reason', () => {
    const updated: OverrideDto = { ...proposedOverride, state: 'Revoked', revocationReason: 'no longer needed for safety' };
    api.revokeOverride.and.returnValue(of(updated));

    const captures: (OverrideDto | null)[] = [];
    component.closed.subscribe(v => captures.push(v));

    component.form.controls.revocationReason.setValue('  no longer needed for safety  ');
    component.submit();

    expect(api.revokeOverride).toHaveBeenCalledWith('oid-1', 'no longer needed for safety');
    expect(captures).toEqual([updated]);
  });

  it('on 403 surfaces the experimentally-disabled message inline', () => {
    const forbidden = new HttpErrorResponse({
      status: 403,
      error: { detail: 'Overrides are experimentally disabled.', title: 'Forbidden' },
    });
    api.revokeOverride.and.returnValue(throwError(() => forbidden));

    component.form.controls.revocationReason.setValue('valid revocation reason');
    component.submit();
    fixture.detectChanges();

    expect(component.errorMessage()).toContain('experimentally disabled');
    const banner = fixture.nativeElement.querySelector('[data-testid="banner"]');
    expect(banner).toBeTruthy();
  });

  it('cancel emits null without calling the API', () => {
    const captures: (OverrideDto | null)[] = [];
    component.closed.subscribe(v => captures.push(v));

    component.cancel();

    expect(api.revokeOverride).not.toHaveBeenCalled();
    expect(captures).toEqual([null]);
  });
});
