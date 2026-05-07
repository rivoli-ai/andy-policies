// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import {
  ApiService,
  OverrideDto,
  OverrideListQuery,
} from '../../shared/services/api.service';
import { OverridesManagerComponent } from './overrides-manager.component';

describe('OverridesManagerComponent (P9.6)', () => {
  let fixture: ComponentFixture<OverridesManagerComponent>;
  let component: OverridesManagerComponent;
  let api: jasmine.SpyObj<ApiService>;

  const proposed: OverrideDto = {
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

  function build(opts: {
    perStateRows?: Record<string, OverrideDto[]>;
    listError?: HttpErrorResponse | null;
  } = {}): void {
    TestBed.resetTestingModule();
    api = jasmine.createSpyObj<ApiService>('ApiService', [
      'listOverrides',
      'approveOverride',
      'revokeOverride',
    ]);
    api.listOverrides.and.callFake((query: OverrideListQuery = {}) => {
      if (opts.listError) return throwError(() => opts.listError!);
      const state = query.state ?? 'Proposed';
      return of(opts.perStateRows?.[state] ?? []);
    });

    TestBed.configureTestingModule({
      imports: [OverridesManagerComponent],
      providers: [{ provide: ApiService, useValue: api }],
    });

    fixture = TestBed.createComponent(OverridesManagerComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('loads Proposed state by default and renders a row per override', () => {
    build({ perStateRows: { Proposed: [proposed] } });
    expect(component.state()).toBe('Proposed');
    expect(component.overrides().length).toBe(1);

    const row = fixture.nativeElement.querySelector('[data-testid="row-oid-1"]');
    expect(row).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="approve-oid-1"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="revoke-oid-1"]')).toBeTruthy();
  });

  it('switching tabs reloads with the new state filter', () => {
    build({
      perStateRows: {
        Proposed: [proposed],
        Approved: [{ ...proposed, id: 'oid-2', state: 'Approved' }],
      },
    });
    api.listOverrides.calls.reset();

    component.setState('Approved');

    expect(api.listOverrides).toHaveBeenCalled();
    const args = api.listOverrides.calls.mostRecent().args[0] as OverrideListQuery;
    expect(args.state).toBe('Approved');
    expect(component.overrides()[0].id).toBe('oid-2');
  });

  it('renders empty state when no rows for the active tab', () => {
    build({ perStateRows: { Proposed: [] } });
    const empty = fixture.nativeElement.querySelector('[data-testid="empty"]');
    expect(empty).toBeTruthy();
    expect(empty.textContent).toContain('Proposed');
  });

  it('toggleExpand opens and closes the inline detail row', () => {
    build({ perStateRows: { Proposed: [proposed] } });

    component.toggleExpand('oid-1');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="detail-oid-1"]')).toBeTruthy();

    component.toggleExpand('oid-1');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="detail-oid-1"]')).toBeFalsy();
  });

  it('approve removes the row when the override moves to a different state', () => {
    build({ perStateRows: { Proposed: [proposed] } });
    const moved: OverrideDto = { ...proposed, state: 'Approved' };
    api.approveOverride.and.returnValue(of(moved));

    component.approve(proposed);

    expect(component.overrides().find(o => o.id === 'oid-1')).toBeUndefined();
    expect(component.approveError()).toBeNull();
  });

  it('approve patches in place when the new DTO still matches the active tab', () => {
    // Edge case — server returns a new revision but state unchanged.
    build({ perStateRows: { Proposed: [proposed] } });
    const samePatched: OverrideDto = { ...proposed, rationale: 'updated rationale' };
    api.approveOverride.and.returnValue(of(samePatched));

    component.approve(proposed);

    const row = component.overrides().find(o => o.id === 'oid-1');
    expect(row?.rationale).toBe('updated rationale');
  });

  it('approve 403 surfaces an inline approve-error banner', () => {
    build({ perStateRows: { Proposed: [proposed] } });
    api.approveOverride.and.returnValue(
      throwError(() => new HttpErrorResponse({ status: 403, error: { detail: 'forbidden' } })),
    );

    component.approve(proposed);
    fixture.detectChanges();

    expect(component.approveError()).toContain('experimentally disabled');
    const banner = fixture.nativeElement.querySelector('[data-testid="approve-error"]');
    expect(banner).toBeTruthy();
  });

  it('onRevokeClosed(updated) routes through applyUpdated', () => {
    build({ perStateRows: { Proposed: [proposed] } });

    const revoked: OverrideDto = { ...proposed, state: 'Revoked' };
    component.onRevokeClosed(revoked);

    expect(component.overrides().find(o => o.id === 'oid-1')).toBeUndefined();
    expect(component.revoking()).toBeNull();
  });

  it('list error sets a retryable banner', () => {
    build({ listError: new HttpErrorResponse({ status: 500, error: { title: 'oops' } }) });

    expect(component.errorMessage()).toContain('oops');
    expect(fixture.nativeElement.querySelector('[data-testid="banner"]')).toBeTruthy();
  });
});
