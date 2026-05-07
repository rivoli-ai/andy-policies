// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import {
  ApiService,
  BindingDto,
  CreateBindingRequest,
  PolicyVersionDto,
} from '../../shared/services/api.service';
import { CreateBindingModalComponent } from './create-binding-modal.component';

describe('CreateBindingModalComponent (P9.5)', () => {
  let fixture: ComponentFixture<CreateBindingModalComponent>;
  let component: CreateBindingModalComponent;
  let api: jasmine.SpyObj<ApiService>;

  const draft: PolicyVersionDto = {
    id: 'vid-1', policyId: 'pid-1', version: 1, state: 'Draft',
    enforcement: 'MUST', severity: 'critical', scopes: [],
    summary: 'init', rulesJson: '{}',
    createdAt: '2026-04-01T00:00:00Z', createdBySubjectId: 'u', proposerSubjectId: 'u',
  };

  const retired: PolicyVersionDto = { ...draft, id: 'vid-r', state: 'Retired' };

  function build(versions: PolicyVersionDto[]): void {
    TestBed.resetTestingModule();
    api = jasmine.createSpyObj<ApiService>('ApiService', ['createBinding']);
    TestBed.configureTestingModule({
      imports: [CreateBindingModalComponent],
      providers: [{ provide: ApiService, useValue: api }],
    });

    fixture = TestBed.createComponent(CreateBindingModalComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('versions', versions);
    component.ngOnInit();
    fixture.detectChanges();
  }

  it('filters Retired versions out of the dropdown', () => {
    build([draft, retired]);
    expect(component.eligibleVersions().map(v => v.id)).toEqual(['vid-1']);
  });

  it('shows the no-versions banner when only Retired versions exist', () => {
    build([retired]);
    const banner = fixture.nativeElement.querySelector('[data-testid="no-versions"]');
    expect(banner).toBeTruthy();
    const submit = fixture.nativeElement.querySelector('[data-testid="submit"]');
    expect(submit).toBeNull();
  });

  it('seeds policyVersionId to the first eligible version', () => {
    build([draft]);
    expect(component.form.controls.policyVersionId.value).toBe('vid-1');
  });

  it('submit fires createBinding with the canonical body shape', () => {
    build([draft]);
    const created: BindingDto = {
      id: 'bid-1', policyVersionId: 'vid-1',
      targetType: 'Repo', targetRef: 'rivoli-ai/example',
      bindStrength: 'Mandatory',
      createdAt: '2026-05-07T00:00:00Z', createdBySubjectId: 'u',
      deletedAt: null, deletedBySubjectId: null,
    };
    api.createBinding.and.returnValue(of(created));

    const captures: (BindingDto | null)[] = [];
    component.closed.subscribe(v => captures.push(v));

    component.form.patchValue({
      targetRef: '  rivoli-ai/example  ', // trims
      bindStrength: 'Mandatory',
    });
    component.submit();

    const req = api.createBinding.calls.mostRecent().args[0] as CreateBindingRequest;
    expect(req).toEqual({
      policyVersionId: 'vid-1',
      targetType: 'Repo',
      targetRef: 'rivoli-ai/example',
      bindStrength: 'Mandatory',
    });
    expect(captures).toEqual([created]);
  });

  it('on 409 keeps the modal open and surfaces detail + offending scope inline', () => {
    build([draft]);
    const conflict = new HttpErrorResponse({
      status: 409,
      error: {
        title: 'Tighten-only violation',
        detail: 'Recommended binding loosens an ancestor Mandatory.',
        offendingScopeDisplayName: 'Org/Tenant/Frontend',
        errorCode: 'binding.tighten-only-violation',
      },
    });
    api.createBinding.and.returnValue(throwError(() => conflict));
    let emitCount = 0;
    component.closed.subscribe(() => emitCount++);

    component.form.patchValue({ targetRef: 'rivoli-ai/example' });
    component.submit();
    fixture.detectChanges();

    expect(emitCount).toBe(0);
    expect(component.errorMessage()).toContain('Recommended binding loosens');
    expect(component.errorMessage()).toContain('Org/Tenant/Frontend');
    const banner = fixture.nativeElement.querySelector('[data-testid="banner"]');
    expect(banner).toBeTruthy();
  });

  it('cancel emits null without calling the API', () => {
    build([draft]);
    const captures: (BindingDto | null)[] = [];
    component.closed.subscribe(v => captures.push(v));

    component.cancel();

    expect(api.createBinding).not.toHaveBeenCalled();
    expect(captures).toEqual([null]);
  });
});
