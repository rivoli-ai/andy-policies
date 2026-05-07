// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import {
  ApiService,
  AuditEventDto,
  AuditPageDto,
  AuditQuery,
  ChainVerificationDto,
} from '../../shared/services/api.service';
import { AuditTimelineComponent } from './audit-timeline.component';

describe('AuditTimelineComponent (P9.7)', () => {
  let fixture: ComponentFixture<AuditTimelineComponent>;
  let component: AuditTimelineComponent;
  let api: jasmine.SpyObj<ApiService>;

  function makeEvent(seq: number, idSuffix: string): AuditEventDto {
    return {
      id: `evt-${idSuffix}`,
      seq,
      prevHashHex: 'a'.repeat(64),
      hashHex: 'b'.repeat(64),
      timestamp: '2026-05-07T12:00:00Z',
      actorSubjectId: 'user:alice',
      actorRoles: ['admin'],
      action: 'policy.publish',
      entityType: 'PolicyVersion',
      entityId: 'vid-1',
      fieldDiff: [{ op: 'replace', path: '/state', value: 'Active' }],
      rationale: 'Promoting to Active',
    };
  }

  function build(opts: {
    pages?: AuditPageDto[];
    listError?: HttpErrorResponse;
  } = {}): void {
    TestBed.resetTestingModule();
    api = jasmine.createSpyObj<ApiService>('ApiService', [
      'listAudit',
      'verifyAuditChain',
    ]);
    if (opts.listError) {
      api.listAudit.and.returnValue(throwError(() => opts.listError!));
    } else {
      let i = 0;
      const pages = opts.pages ?? [{ items: [], nextCursor: null, pageSize: 50 }];
      api.listAudit.and.callFake(() => of(pages[Math.min(i++, pages.length - 1)]));
    }

    TestBed.configureTestingModule({
      imports: [AuditTimelineComponent],
      providers: [{ provide: ApiService, useValue: api }],
    });

    fixture = TestBed.createComponent(AuditTimelineComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('loads first page on init', () => {
    build({
      pages: [{
        items: [makeEvent(2, '2'), makeEvent(1, '1')],
        nextCursor: 'cur-1',
        pageSize: 50,
      }],
    });

    expect(api.listAudit).toHaveBeenCalledTimes(1);
    expect(component.events().length).toBe(2);
    expect(component.events()[0].seq).toBe(2);
    expect(component.hasMore()).toBeTrue();
  });

  it('renders empty state when first page is empty', () => {
    build({ pages: [{ items: [], nextCursor: null, pageSize: 50 }] });
    const empty = fixture.nativeElement.querySelector('[data-testid="empty"]');
    expect(empty).toBeTruthy();
  });

  it('loadMore appends with the cursor; stops when nextCursor null', () => {
    build({
      pages: [
        { items: [makeEvent(2, '2'), makeEvent(1, '1')], nextCursor: 'cur-1', pageSize: 50 },
        { items: [makeEvent(0, '0')], nextCursor: null, pageSize: 50 },
      ],
    });

    component.loadMore();
    expect(api.listAudit).toHaveBeenCalledTimes(2);
    const args = api.listAudit.calls.mostRecent().args[0] as AuditQuery;
    expect(args.cursor).toBe('cur-1');
    expect(component.events().length).toBe(3);
    expect(component.hasMore()).toBeFalse();
  });

  it('filter change resets cursor and reloads from scratch', () => {
    build({
      pages: [
        { items: [makeEvent(5, '5')], nextCursor: 'cur-x', pageSize: 50 },
        { items: [makeEvent(9, '9')], nextCursor: null, pageSize: 50 },
      ],
    });

    component.actor.set('user:bob');
    component.reload();

    // Second call carries the actor filter and no cursor.
    const args = api.listAudit.calls.mostRecent().args[0] as AuditQuery;
    expect(args.actor).toBe('user:bob');
    expect(args.cursor).toBeUndefined();
    expect(component.events().length).toBe(1);
    expect(component.events()[0].id).toBe('evt-9');
  });

  it('toggle expands and collapses the diff panel for a row', () => {
    build({
      pages: [{ items: [makeEvent(1, '1')], nextCursor: null, pageSize: 50 }],
    });

    component.toggle('evt-1');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="diff-evt-1"]')).toBeTruthy();

    component.toggle('evt-1');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="diff-evt-1"]')).toBeFalsy();
  });

  it('verify happy-path renders the OK banner', () => {
    build({});
    const ok: ChainVerificationDto = {
      valid: true, firstDivergenceSeq: null, inspectedCount: 42, lastSeq: 42,
    };
    api.verifyAuditChain.and.returnValue(of(ok));

    component.verify();
    fixture.detectChanges();

    expect(component.verifyState()).toBe('ok');
    const banner = fixture.nativeElement.querySelector('[data-testid="verify-ok"]');
    expect(banner).toBeTruthy();
    expect(banner.textContent).toContain('42');
  });

  it('verify divergence renders the red banner with firstDivergenceSeq', () => {
    build({});
    const bad: ChainVerificationDto = {
      valid: false, firstDivergenceSeq: 17, inspectedCount: 100, lastSeq: 16,
    };
    api.verifyAuditChain.and.returnValue(of(bad));

    component.verify();
    fixture.detectChanges();

    expect(component.verifyState()).toBe('diverged');
    const banner = fixture.nativeElement.querySelector('[data-testid="verify-diverged"]');
    expect(banner).toBeTruthy();
    expect(banner.textContent).toContain('17');
  });

  it('verify error sets the error banner', () => {
    build({});
    api.verifyAuditChain.and.returnValue(
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );

    component.verify();
    fixture.detectChanges();

    expect(component.verifyState()).toBe('error');
    expect(fixture.nativeElement.querySelector('[data-testid="verify-error"]')).toBeTruthy();
  });

  it('list error surfaces a retryable banner', () => {
    build({ listError: new HttpErrorResponse({ status: 500, error: { title: 'oops' } }) });
    fixture.detectChanges();

    expect(component.errorMessage()).toContain('oops');
    expect(fixture.nativeElement.querySelector('[data-testid="banner"]')).toBeTruthy();
  });
});
