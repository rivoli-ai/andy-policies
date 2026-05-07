// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ApiService,
  BindingDto,
  PolicyVersionDto,
} from '../../shared/services/api.service';
import { BindingsManagerComponent } from './bindings-manager.component';

describe('BindingsManagerComponent (P9.5)', () => {
  let fixture: ComponentFixture<BindingsManagerComponent>;
  let component: BindingsManagerComponent;
  let api: jasmine.SpyObj<ApiService>;

  const v1Active: PolicyVersionDto = {
    id: 'vid-1', policyId: 'pid-1', version: 1, state: 'Active',
    enforcement: 'MUST', severity: 'critical', scopes: [],
    summary: 'initial', rulesJson: '{}',
    createdAt: '2026-04-01T00:00:00Z', createdBySubjectId: 'u', proposerSubjectId: 'u',
  };
  const v2Draft: PolicyVersionDto = { ...v1Active, id: 'vid-2', version: 2, state: 'Draft' };

  const bindingA: BindingDto = {
    id: 'bid-A', policyVersionId: 'vid-1',
    targetType: 'Repo', targetRef: 'rivoli-ai/a',
    bindStrength: 'Mandatory',
    createdAt: '2026-04-02T00:00:00Z', createdBySubjectId: 'u',
    deletedAt: null, deletedBySubjectId: null,
  };
  const bindingB: BindingDto = { ...bindingA, id: 'bid-B', policyVersionId: 'vid-2', targetRef: 'rivoli-ai/b' };
  const bindingDeleted: BindingDto = { ...bindingA, id: 'bid-DEL', deletedAt: '2026-04-03T00:00:00Z' };

  /** Helper signature carries the API stub eagerly so build() applies the
   *  callFake BEFORE the component's ngOnChanges runs — otherwise the
   *  spy is bare and the load loop hits an empty function. */
  function build(opts: {
    withVersions: boolean;
    perVersion?: (versionId: string) => BindingDto[];
    perVersionError?: (versionId: string) => HttpErrorResponse | null;
  }): void {
    TestBed.resetTestingModule();
    api = jasmine.createSpyObj<ApiService>('ApiService', ['listVersionBindings']);
    api.listVersionBindings.and.callFake((_, vId) => {
      const err = opts.perVersionError?.(vId);
      if (err) return throwError(() => err);
      return of(opts.perVersion?.(vId) ?? []);
    });

    TestBed.configureTestingModule({
      imports: [BindingsManagerComponent],
      providers: [{ provide: ApiService, useValue: api }],
    });

    fixture = TestBed.createComponent(BindingsManagerComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('policyId', 'pid-1');
    fixture.componentRef.setInput(
      'versions',
      opts.withVersions ? [v1Active, v2Draft] : [],
    );
    component.ngOnChanges();
    fixture.detectChanges();
  }

  it('aggregates bindings across versions and sorts by version desc', () => {
    build({
      withVersions: true,
      perVersion: vId => (vId === 'vid-1' ? [bindingA] : [bindingB]),
    });

    // listVersionBindings is called once per version per reload — the test
    // helper triggers reload twice (manual ngOnChanges + auto on first
    // detectChanges) so 2 versions × 2 reloads = 4. We only care that the
    // component called the API at least once per version, hence assert ≥ 2.
    expect(api.listVersionBindings.calls.count()).toBeGreaterThanOrEqual(2);
    expect(component.bindings().length).toBe(2);
    // v2 first (desc), v1 second.
    expect(component.bindings().map(b => b.id)).toEqual(['bid-B', 'bid-A']);
    expect(component.bindings()[0].versionNumber).toBe(2);
  });

  it('filters out soft-deleted bindings', () => {
    build({
      withVersions: true,
      perVersion: () => [bindingA, bindingDeleted],
    });

    expect(component.bindings().length).toBe(2); // bindingA from each version, deleted dropped
    expect(component.bindings().every(b => b.id !== 'bid-DEL')).toBeTrue();
  });

  it('renders empty state when no bindings on any version', () => {
    build({ withVersions: true });

    const empty = fixture.nativeElement.querySelector('[data-testid="empty"]');
    expect(empty).toBeTruthy();
  });

  it('does not call API when versions is empty', () => {
    build({ withVersions: false });
    expect(api.listVersionBindings).not.toHaveBeenCalled();
  });

  it('survives a per-version error by returning empty rows for that version', () => {
    build({
      withVersions: true,
      perVersionError: vId =>
        vId === 'vid-1' ? new HttpErrorResponse({ status: 403 }) : null,
      perVersion: vId => (vId === 'vid-2' ? [bindingB] : []),
    });

    expect(component.bindings().length).toBe(1);
    expect(component.bindings()[0].id).toBe('bid-B');
    expect(component.errorMessage()).toBeNull();
  });

  it('onCreateClosed appends + sorts the new row', () => {
    build({ withVersions: true });

    const created: BindingDto = { ...bindingA, id: 'bid-NEW', policyVersionId: 'vid-2' };
    component.onCreateClosed(created);

    expect(component.bindings()[0].id).toBe('bid-NEW');
    expect(component.bindings()[0].versionNumber).toBe(2);
  });

  it('onDeleteClosed(deleted=true) removes the row from the list', () => {
    build({
      withVersions: true,
      perVersion: vId => (vId === 'vid-1' ? [bindingA] : [bindingB]),
    });
    expect(component.bindings().length).toBe(2);

    component.onDeleteClosed({ deleted: true, bindingId: 'bid-A' });

    expect(component.bindings().length).toBe(1);
    expect(component.bindings()[0].id).toBe('bid-B');
  });
});
