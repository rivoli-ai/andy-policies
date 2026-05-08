// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ApiService, PolicyVersionDto } from '../../shared/services/api.service';
import { PermissionsService } from '../../core/auth/permissions.service';
import { ApprovalsInboxComponent } from './approvals-inbox.component';

describe('ApprovalsInboxComponent (P9.3 #68)', () => {
  let fixture: ComponentFixture<ApprovalsInboxComponent>;
  let component: ApprovalsInboxComponent;
  let api: jasmine.SpyObj<ApiService>;
  let perms: PermissionsService;

  const proposedDraft: PolicyVersionDto = {
    id: 'vid-1',
    policyId: 'pid-1',
    version: 3,
    state: 'Draft',
    enforcement: 'MUST',
    severity: 'critical',
    scopes: ['prod'],
    summary: 'updated',
    rulesJson: '{}',
    createdAt: '2026-05-07T10:00:00Z',
    createdBySubjectId: 'user:alice',
    proposerSubjectId: 'user:alice',
    revision: 4,
    readyForReview: true,
  };

  function build(opts: {
    rows?: PolicyVersionDto[];
    listError?: HttpErrorResponse;
    canPublish?: boolean;
    canReject?: boolean;
  } = {}): void {
    TestBed.resetTestingModule();
    api = jasmine.createSpyObj<ApiService>('ApiService', [
      'listPendingApprovals',
      'transitionPolicyVersion',
      'rejectPolicyVersion',
    ]);
    api.listPendingApprovals.and.callFake(() =>
      opts.listError ? throwError(() => opts.listError!) : of(opts.rows ?? []),
    );

    TestBed.configureTestingModule({
      imports: [ApprovalsInboxComponent],
      providers: [
        { provide: ApiService, useValue: api },
        provideRouter([]),
      ],
    });

    perms = TestBed.inject(PermissionsService);
    const seed: string[] = [];
    if (opts.canPublish ?? true) seed.push('andy-policies:policy:publish');
    if (opts.canReject ?? true) seed.push('andy-policies:policy:reject');
    perms.setForTesting(seed);

    fixture = TestBed.createComponent(ApprovalsInboxComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('loads pending rows on init and renders a row with both buttons when permitted', () => {
    build({ rows: [proposedDraft], canPublish: true, canReject: true });

    expect(api.listPendingApprovals).toHaveBeenCalledTimes(1);
    expect(component.rows().length).toBe(1);

    const approveBtn = fixture.nativeElement.querySelector('[data-testid="approve-button"]');
    const rejectBtn = fixture.nativeElement.querySelector('[data-testid="reject-button"]');
    expect(approveBtn).toBeTruthy();
    expect(rejectBtn).toBeTruthy();
  });

  it('hides Approve when canPublish is false (DOM, not just disabled)', () => {
    build({ rows: [proposedDraft], canPublish: false, canReject: true });
    const approveBtn = fixture.nativeElement.querySelector('[data-testid="approve-button"]');
    expect(approveBtn).toBeNull();
  });

  it('hides Reject when canReject is false', () => {
    build({ rows: [proposedDraft], canPublish: true, canReject: false });
    const rejectBtn = fixture.nativeElement.querySelector('[data-testid="reject-button"]');
    expect(rejectBtn).toBeNull();
  });

  it('Approve flow opens the rationale modal and posts publish with expectedRevision', () => {
    build({ rows: [proposedDraft] });
    api.transitionPolicyVersion.and.returnValue(of({ ...proposedDraft, state: 'Active' }));

    component.openApprove(proposedDraft);
    expect(component.modalKind()).toBe('approve');

    component.onModalConfirmed('reviewed and approved');

    expect(api.transitionPolicyVersion).toHaveBeenCalledWith(
      proposedDraft.policyId,
      proposedDraft.id,
      'Active',
      'reviewed and approved',
      proposedDraft.revision,
    );
    expect(component.rows().length).toBe(0,
      'the approved row drops from the inbox so the next poll does not flicker it');
    expect(component.modalKind()).toBeNull();
  });

  it('Reject flow posts to /reject with the rationale', () => {
    build({ rows: [proposedDraft] });
    api.rejectPolicyVersion.and.returnValue(of({ ...proposedDraft, readyForReview: false }));

    component.openReject(proposedDraft);
    component.onModalConfirmed('needs more detail');

    expect(api.rejectPolicyVersion).toHaveBeenCalledWith(
      proposedDraft.policyId,
      proposedDraft.id,
      'needs more detail',
    );
    expect(component.rows().length).toBe(0);
  });

  it('Approve 412 surfaces the stale-draft message and keeps the modal open', () => {
    build({ rows: [proposedDraft] });
    const stale = new HttpErrorResponse({ status: 412, statusText: 'Precondition Failed' });
    api.transitionPolicyVersion.and.returnValue(throwError(() => stale));

    component.openApprove(proposedDraft);
    component.onModalConfirmed('approve');

    expect(component.modalKind()).toBe('approve',
      'modal stays open on 412 so the user can cancel and reload');
    expect(component.modalError()).toContain('modified since you loaded the inbox');
    expect(component.rows().length).toBe(1, 'row not removed on stale 412');
  });

  it('polls the inbox at the configured interval', fakeAsync(() => {
    build({ rows: [] });
    api.listPendingApprovals.calls.reset();

    tick(ApprovalsInboxComponent.pollIntervalMs);
    expect(api.listPendingApprovals).toHaveBeenCalledTimes(1);

    tick(ApprovalsInboxComponent.pollIntervalMs);
    expect(api.listPendingApprovals).toHaveBeenCalledTimes(2);

    component.ngOnDestroy();
  }));

  it('list error renders an inline error banner', () => {
    const err = new HttpErrorResponse({
      status: 500,
      error: { detail: 'upstream rbac is down' },
    });
    build({ listError: err });
    expect(component.errorMessage()).toContain('upstream rbac is down');
  });
});
