// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { Router, provideRouter } from '@angular/router';
import { ApiService, BundleDto } from '../../shared/services/api.service';
import { BundlesListComponent } from './bundles-list.component';

describe('BundlesListComponent (P9.8)', () => {
  let fixture: ComponentFixture<BundlesListComponent>;
  let component: BundlesListComponent;
  let api: jasmine.SpyObj<ApiService>;
  let router: Router;
  let navigateSpy: jasmine.Spy;

  const a: BundleDto = {
    id: 'a', name: 'alpha', description: null,
    createdAt: '2026-05-01T00:00:00Z', createdBySubjectId: 'u',
    snapshotHash: 'h1', state: 'Active', deletedAt: null, deletedBySubjectId: null,
  };
  const b: BundleDto = { ...a, id: 'b', name: 'beta', snapshotHash: 'h2' };
  const c: BundleDto = { ...a, id: 'c', name: 'gamma', snapshotHash: 'h3' };

  function build(opts: { rows?: BundleDto[]; listError?: HttpErrorResponse } = {}): void {
    TestBed.resetTestingModule();
    api = jasmine.createSpyObj<ApiService>('ApiService', ['listBundles']);
    if (opts.listError) {
      api.listBundles.and.returnValue(throwError(() => opts.listError!));
    } else {
      api.listBundles.and.returnValue(of(opts.rows ?? []));
    }
    TestBed.configureTestingModule({
      imports: [BundlesListComponent],
      providers: [
        provideRouter([]),
        { provide: ApiService, useValue: api },
      ],
    });

    fixture = TestBed.createComponent(BundlesListComponent);
    component = fixture.componentInstance;
    router = TestBed.inject(Router);
    navigateSpy = spyOn(router, 'navigate').and.returnValue(Promise.resolve(true));
    fixture.detectChanges();
  }

  it('loads bundles on init', () => {
    build({ rows: [a, b] });
    expect(api.listBundles).toHaveBeenCalledWith(false);
    expect(component.bundles().length).toBe(2);
  });

  it('renders empty state when no bundles', () => {
    build({});
    expect(fixture.nativeElement.querySelector('[data-testid="empty"]')).toBeTruthy();
  });

  it('toggleIncludeDeleted reloads with includeDeleted=true', () => {
    build({ rows: [a] });
    api.listBundles.calls.reset();

    component.toggleIncludeDeleted();

    expect(api.listBundles).toHaveBeenCalledWith(true);
  });

  it('canCompare is true only when exactly two bundles are selected', () => {
    build({ rows: [a, b, c] });

    expect(component.canCompare()).toBeFalse();
    component.toggleSelect('a');
    expect(component.canCompare()).toBeFalse();
    component.toggleSelect('b');
    expect(component.canCompare()).toBeTrue();
    component.toggleSelect('c');
    expect(component.canCompare()).toBeFalse();
  });

  it('compare navigates to /bundles/diff with a + b query params', () => {
    build({ rows: [a, b] });
    component.toggleSelect('a');
    component.toggleSelect('b');

    component.compare();

    expect(navigateSpy).toHaveBeenCalled();
    const args = navigateSpy.calls.mostRecent().args;
    expect(args[0]).toEqual(['/bundles/diff']);
    const queryParams = (args[1] as any).queryParams;
    expect([queryParams.a, queryParams.b].sort()).toEqual(['a', 'b']);
  });

  it('compare is a no-op when canCompare is false', () => {
    build({ rows: [a] });
    component.toggleSelect('a');

    component.compare();

    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it('onCreateClosed(bundle) prepends the new row', () => {
    build({ rows: [a] });

    const created: BundleDto = { ...a, id: 'new', name: 'fresh' };
    component.onCreateClosed(created);

    expect(component.bundles()[0].id).toBe('new');
    expect(component.bundles().length).toBe(2);
  });

  it('onCreateClosed(null) closes the modal without mutating the list', () => {
    build({ rows: [a] });

    component.onCreateClosed(null);

    expect(component.bundles().length).toBe(1);
    expect(component.showCreate()).toBeFalse();
  });

  it('list error surfaces a retryable banner', () => {
    build({ listError: new HttpErrorResponse({ status: 500, error: { title: 'oops' } }) });

    expect(component.errorMessage()).toContain('oops');
    expect(fixture.nativeElement.querySelector('[data-testid="banner"]')).toBeTruthy();
  });
});
