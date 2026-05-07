// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import {
  ApiService,
  LifecycleState,
  PolicyVersionDto,
} from '../../shared/services/api.service';
import { LifecycleTransitionModalComponent } from './lifecycle-transition-modal.component';

describe('LifecycleTransitionModalComponent (P9.4)', () => {
  let fixture: ComponentFixture<LifecycleTransitionModalComponent>;
  let component: LifecycleTransitionModalComponent;
  let api: jasmine.SpyObj<ApiService>;

  const draftVersion: PolicyVersionDto = {
    id: 'vid-1',
    policyId: 'pid-1',
    version: 1,
    state: 'Draft',
    enforcement: 'MUST',
    severity: 'critical',
    scopes: ['prod'],
    summary: 'init',
    rulesJson: '{}',
    createdAt: '2026-04-01T00:00:00Z',
    createdBySubjectId: 'user:alice',
    proposerSubjectId: 'user:alice',
  };

  function build(version: PolicyVersionDto): void {
    // Each build() resets the TestBed so the same `it()` can rebuild with
    // different `version` inputs (legalTargets coverage walks all 4 states).
    TestBed.resetTestingModule();
    api = jasmine.createSpyObj<ApiService>('ApiService', ['transitionPolicyVersion']);
    TestBed.configureTestingModule({
      imports: [LifecycleTransitionModalComponent],
      providers: [{ provide: ApiService, useValue: api }],
    });

    fixture = TestBed.createComponent(LifecycleTransitionModalComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('version', version);
    component.ngOnInit();
    fixture.detectChanges();
  }

  it('legalTargets are computed from the current state', () => {
    build(draftVersion);
    expect(component.legalTargets()).toEqual(['Active']);

    build({ ...draftVersion, state: 'Active' });
    expect(component.legalTargets()).toEqual(['WindingDown', 'Retired']);

    build({ ...draftVersion, state: 'WindingDown' });
    expect(component.legalTargets()).toEqual(['Retired']);

    build({ ...draftVersion, state: 'Retired' });
    expect(component.legalTargets()).toEqual([]);
    expect(component.hasNoTargets()).toBeTrue();
  });

  it('Submit is disabled until rationale.length >= 10', () => {
    build(draftVersion);

    component.form.controls.rationale.setValue('short');
    expect(component.form.invalid).toBeTrue();

    component.form.controls.rationale.setValue('exactly 10');
    expect(component.form.valid).toBeTrue();
  });

  it('renders no submit button when version is terminal (Retired)', () => {
    build({ ...draftVersion, state: 'Retired' });

    const btn = fixture.nativeElement.querySelector('[data-testid="submit"]');
    expect(btn).toBeNull();
    const banner = fixture.nativeElement.querySelector('[data-testid="terminal"]');
    expect(banner).toBeTruthy();
  });

  it('submit calls transitionPolicyVersion with target + rationale', () => {
    build(draftVersion);
    const updated: PolicyVersionDto = { ...draftVersion, state: 'Active' };
    api.transitionPolicyVersion.and.returnValue(of(updated));

    const captures: (PolicyVersionDto | null)[] = [];
    component.closed.subscribe(v => captures.push(v));

    component.form.controls.rationale.setValue('publishing the initial draft');
    component.submit();

    expect(api.transitionPolicyVersion).toHaveBeenCalledTimes(1);
    const args = api.transitionPolicyVersion.calls.mostRecent().args;
    expect(args[0]).toBe('pid-1');
    expect(args[1]).toBe('vid-1');
    expect(args[2]).toBe('Active');
    expect(args[3]).toBe('publishing the initial draft');

    expect(captures).toEqual([updated]);
  });

  it('on 409 keeps the modal open and surfaces problem-detail', () => {
    build(draftVersion);
    const conflict = new HttpErrorResponse({
      status: 409,
      error: { detail: 'Lifecycle transition Draft → Retired is not allowed.' },
    });
    api.transitionPolicyVersion.and.returnValue(throwError(() => conflict));

    let emitCount = 0;
    component.closed.subscribe(() => emitCount++);

    component.form.controls.rationale.setValue('a valid rationale');
    component.submit();
    fixture.detectChanges();

    expect(emitCount).toBe(0); // modal stayed open — no closed emit
    expect(component.errorMessage()).toContain('Draft → Retired');
    const banner = fixture.nativeElement.querySelector('[data-testid="banner"]');
    expect(banner).toBeTruthy();
  });

  it('cancel emits null without calling the API', () => {
    build(draftVersion);
    const captures: (PolicyVersionDto | null)[] = [];
    component.closed.subscribe(v => captures.push(v));

    component.cancel();

    expect(api.transitionPolicyVersion).not.toHaveBeenCalled();
    expect(captures).toEqual([null]);
  });
});
