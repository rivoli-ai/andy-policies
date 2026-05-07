// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import {
  ApiService,
  PolicyDto,
  PolicyVersionDto,
} from '../../shared/services/api.service';
import { PolicyDetailComponent } from './policy-detail.component';

describe('PolicyDetailComponent (P9.4)', () => {
  let fixture: ComponentFixture<PolicyDetailComponent>;
  let component: PolicyDetailComponent;
  let api: jasmine.SpyObj<ApiService>;

  const samplePolicy: PolicyDto = {
    id: 'pid-1',
    name: 'no-prod',
    description: 'No prod from drafts',
    createdAt: '2026-04-01T00:00:00Z',
    createdBySubjectId: 'user:alice',
    versionCount: 2,
    activeVersionId: 'vid-active',
  };

  const draftV2: PolicyVersionDto = {
    id: 'vid-2',
    policyId: 'pid-1',
    version: 2,
    state: 'Draft',
    enforcement: 'SHOULD',
    severity: 'moderate',
    scopes: [],
    summary: 'next iteration',
    rulesJson: '{}',
    createdAt: '2026-04-15T00:00:00Z',
    createdBySubjectId: 'user:bob',
    proposerSubjectId: 'user:bob',
  };

  const activeV1: PolicyVersionDto = {
    ...draftV2,
    id: 'vid-active',
    version: 1,
    state: 'Active',
    summary: 'initial',
  };

  function build(): void {
    api = jasmine.createSpyObj<ApiService>('ApiService', [
      'getPolicy',
      'listPolicyVersions',
      'listVersionBindings',
    ]);
    api.getPolicy.and.returnValue(of(samplePolicy));
    api.listPolicyVersions.and.returnValue(of([activeV1, draftV2]));
    // BindingsManager mounts as a child and pulls bindings per version on
    // ngOnChanges — stub to empty so the parent suite stays focused on
    // detail-level behaviour (P9.5 has its own bindings-manager.spec).
    api.listVersionBindings.and.returnValue(of([]));

    TestBed.configureTestingModule({
      imports: [PolicyDetailComponent],
      providers: [
        provideRouter([]),
        { provide: ApiService, useValue: api },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ id: 'pid-1' }) } },
        },
      ],
    });

    fixture = TestBed.createComponent(PolicyDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('loads policy + versions on init and sorts versions descending', () => {
    build();
    expect(api.getPolicy).toHaveBeenCalledWith('pid-1');
    expect(api.listPolicyVersions).toHaveBeenCalledWith('pid-1');
    expect(component.versions().map(v => v.version)).toEqual([2, 1]);
  });

  it('activeVersion resolves from policy.activeVersionId', () => {
    build();
    expect(component.activeVersion()?.id).toBe('vid-active');
  });

  it('Edit link is shown only on Draft rows', () => {
    build();
    const editDraft = fixture.nativeElement.querySelector('[data-testid="edit-vid-2"]');
    const editActive = fixture.nativeElement.querySelector('[data-testid="edit-vid-active"]');
    expect(editDraft).toBeTruthy();
    expect(editActive).toBeNull();
  });

  it('Transition button is shown only on rows with legal targets', () => {
    build();
    // Draft has [Active]; Active has [WindingDown, Retired]; both have legal targets.
    expect(fixture.nativeElement.querySelector('[data-testid="transition-vid-2"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="transition-vid-active"]')).toBeTruthy();
  });

  it('openTransition sets the modal source version', () => {
    build();
    component.openTransition(draftV2);
    expect(component.transitioningVersion()).toBe(draftV2);
  });

  it('onModalClosed(null) clears the modal without calling getPolicy', () => {
    build();
    component.openTransition(draftV2);
    api.getPolicy.calls.reset();

    component.onModalClosed(null);

    expect(component.transitioningVersion()).toBeNull();
    expect(api.getPolicy).not.toHaveBeenCalled();
  });

  it('onModalClosed(updated) replaces the version and refetches the policy', () => {
    build();
    const updated: PolicyVersionDto = { ...draftV2, state: 'Active' };

    component.openTransition(draftV2);
    api.getPolicy.calls.reset();
    api.getPolicy.and.returnValue(of({ ...samplePolicy, activeVersionId: 'vid-2' }));

    component.onModalClosed(updated);

    expect(component.transitioningVersion()).toBeNull();
    expect(component.versions().find(v => v.id === 'vid-2')!.state).toBe('Active');
    expect(api.getPolicy).toHaveBeenCalledWith('pid-1');
  });
});
