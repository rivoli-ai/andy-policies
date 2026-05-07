// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { ApiService, BundleDiffResult } from '../../shared/services/api.service';
import { BundleDiffComponent } from './bundle-diff.component';

describe('BundleDiffComponent (P9.8)', () => {
  let fixture: ComponentFixture<BundleDiffComponent>;
  let component: BundleDiffComponent;
  let api: jasmine.SpyObj<ApiService>;

  const diff: BundleDiffResult = {
    fromId: 'a', fromSnapshotHash: 'aaa',
    toId: 'b', toSnapshotHash: 'bbb',
    rfc6902PatchJson: JSON.stringify([{ op: 'add', path: '/policies/x', value: 1 }]),
    opCount: 1,
  };

  function build(opts: {
    a?: string | null;
    b?: string | null;
    diffError?: HttpErrorResponse;
  } = {}): void {
    TestBed.resetTestingModule();
    api = jasmine.createSpyObj<ApiService>('ApiService', ['diffBundles']);
    if (opts.diffError) {
      api.diffBundles.and.returnValue(throwError(() => opts.diffError!));
    } else {
      api.diffBundles.and.returnValue(of(diff));
    }

    const params: Record<string, string> = {};
    if (opts.a !== null && opts.a !== undefined) params['a'] = opts.a;
    if (opts.b !== null && opts.b !== undefined) params['b'] = opts.b;

    TestBed.configureTestingModule({
      imports: [BundleDiffComponent],
      providers: [
        provideRouter([]),
        { provide: ApiService, useValue: api },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: { queryParamMap: convertToParamMap(params) },
          },
        },
      ],
    });

    fixture = TestBed.createComponent(BundleDiffComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('shows missing-params banner when a or b is missing', () => {
    build({});
    expect(component.missingParams()).toBeTrue();
    expect(fixture.nativeElement.querySelector('[data-testid="missing"]')).toBeTruthy();
    expect(api.diffBundles).not.toHaveBeenCalled();
  });

  it('shows identical-params banner when a === b without calling API', () => {
    build({ a: 'same', b: 'same' });

    expect(component.identicalParams()).toBeTrue();
    expect(fixture.nativeElement.querySelector('[data-testid="identical"]')).toBeTruthy();
    expect(api.diffBundles).not.toHaveBeenCalled();
  });

  it('calls diffBundles and renders the diff card on happy path', () => {
    build({ a: 'a', b: 'b' });

    expect(api.diffBundles).toHaveBeenCalledWith('a', 'b');
    expect(component.result()?.opCount).toBe(1);
    expect(fixture.nativeElement.querySelector('[data-testid="diff-card"]')).toBeTruthy();
  });

  it('surfaces server error', () => {
    build({
      a: 'a', b: 'b',
      diffError: new HttpErrorResponse({ status: 400, error: { detail: 'bad ids' } }),
    });

    expect(component.errorMessage()).toContain('bad ids');
    expect(fixture.nativeElement.querySelector('[data-testid="banner"]')).toBeTruthy();
  });

  it('delegates patch rendering to the shared Rfc6902 view', () => {
    build({ a: 'a', b: 'b' });

    // The shared component renders a [data-testid="diff-view"] container.
    expect(fixture.nativeElement.querySelector('app-rfc6902-diff-view')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="diff-view"]')).toBeTruthy();
  });
});
