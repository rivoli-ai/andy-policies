// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { ApiService, BundleDto } from '../../shared/services/api.service';
import { BundleDetailComponent } from './bundle-detail.component';

describe('BundleDetailComponent (P9.8)', () => {
  let fixture: ComponentFixture<BundleDetailComponent>;
  let component: BundleDetailComponent;
  let api: jasmine.SpyObj<ApiService>;

  const sample: BundleDto = {
    id: 'bid-1', name: 'release-2026-05',
    description: 'May release snapshot',
    createdAt: '2026-05-01T12:00:00Z', createdBySubjectId: 'user:alice',
    snapshotHash: 'a'.repeat(64), state: 'Active',
    deletedAt: null, deletedBySubjectId: null,
  };

  function build(opts: { bundle?: BundleDto; loadError?: HttpErrorResponse } = {}): void {
    TestBed.resetTestingModule();
    api = jasmine.createSpyObj<ApiService>('ApiService', ['getBundle']);
    if (opts.loadError) {
      api.getBundle.and.returnValue(throwError(() => opts.loadError!));
    } else {
      api.getBundle.and.returnValue(of(opts.bundle ?? sample));
    }
    TestBed.configureTestingModule({
      imports: [BundleDetailComponent],
      providers: [
        provideRouter([]),
        { provide: ApiService, useValue: api },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ bundleId: 'bid-1' }) } },
        },
      ],
    });

    fixture = TestBed.createComponent(BundleDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('loads + renders bundle metadata', () => {
    build({});
    expect(api.getBundle).toHaveBeenCalledWith('bid-1');
    expect(component.bundle()?.name).toBe('release-2026-05');
    expect(component.loading()).toBeFalse();
  });

  it('renders the frozen-tree placeholder section', () => {
    build({});
    const stub = fixture.nativeElement.querySelector('[data-testid="frozen-tree-stub"]');
    expect(stub).toBeTruthy();
    expect(stub.textContent).toContain('not yet exposed');
  });

  it('surfaces an error banner on load failure', () => {
    build({ loadError: new HttpErrorResponse({ status: 404, error: { title: 'Not found' } }) });

    expect(component.errorMessage()).toContain('Not found');
    expect(fixture.nativeElement.querySelector('[data-testid="banner"]')).toBeTruthy();
  });

  it('shows deleted metadata when bundle is soft-deleted', () => {
    const deleted: BundleDto = {
      ...sample,
      state: 'Deleted',
      deletedAt: '2026-05-05T00:00:00Z',
      deletedBySubjectId: 'user:bob',
    };
    build({ bundle: deleted });

    const html = fixture.nativeElement.textContent;
    expect(html).toContain('Deleted');
    expect(html).toContain('user:bob');
  });
});
