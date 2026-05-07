// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ApiService,
  BundleDto,
  PolicyDto,
  PolicyQuery,
} from '../../shared/services/api.service';
import { PoliciesListComponent } from './policies-list.component';

describe('PoliciesListComponent (P9.1)', () => {
  let fixture: ComponentFixture<PoliciesListComponent>;
  let component: PoliciesListComponent;
  let api: jasmine.SpyObj<ApiService>;

  const samplePolicies: PolicyDto[] = [
    {
      id: '11111111-1111-1111-1111-111111111111',
      name: 'no-prod',
      description: 'Block prod writes from drafts',
      createdAt: '2026-04-01T12:00:00Z',
      createdBySubjectId: 'user:alice',
      versionCount: 2,
      activeVersionId: '22222222-2222-2222-2222-222222222222',
    },
    {
      id: '33333333-3333-3333-3333-333333333333',
      name: 'read-only',
      description: null,
      createdAt: '2026-04-02T12:00:00Z',
      createdBySubjectId: 'user:bob',
      versionCount: 1,
      activeVersionId: null,
    },
  ];

  const sampleBundles: BundleDto[] = [
    {
      id: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
      name: 'release-2026-04',
      description: null,
      createdAt: '2026-04-15T00:00:00Z',
      createdBySubjectId: 'user:alice',
      snapshotHash: 'deadbeef',
      state: 'Active',
      deletedAt: null,
      deletedBySubjectId: null,
    },
  ];

  beforeEach(async () => {
    api = jasmine.createSpyObj<ApiService>('ApiService', ['listPolicies', 'listBundles']);
    api.listPolicies.and.returnValue(of(samplePolicies));
    api.listBundles.and.returnValue(of(sampleBundles));

    await TestBed.configureTestingModule({
      imports: [PoliciesListComponent],
      providers: [
        { provide: ApiService, useValue: api },
        provideRouter([]),
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(PoliciesListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('loads policies + bundles on init and renders rows', () => {
    expect(api.listPolicies).toHaveBeenCalledTimes(1);
    expect(api.listBundles).toHaveBeenCalledTimes(1);
    expect(component.policies().length).toBe(2);

    const rows = fixture.nativeElement.querySelectorAll('tbody tr');
    expect(rows.length).toBe(2);
    const names = Array.from(rows).map((r: any) => r.querySelector('.name').textContent.trim());
    expect(names).toEqual(['no-prod', 'read-only']);
  });

  it('renders the empty-state when listPolicies returns no rows', () => {
    api.listPolicies.and.returnValue(of([]));
    component.reload();
    fixture.detectChanges();

    const empty = fixture.nativeElement.querySelector('[data-testid="empty"]');
    expect(empty).toBeTruthy();
    expect(empty.textContent).toContain('No policies match');
  });

  it('debounces search input (250ms) and reissues exactly once with namePrefix', fakeAsync(() => {
    api.listPolicies.calls.reset();

    component.onSearchInput('no');
    component.onSearchInput('no-pr');
    component.onSearchInput('no-prod');

    tick(249);
    expect(api.listPolicies).not.toHaveBeenCalled();

    tick(1);
    expect(api.listPolicies).toHaveBeenCalledTimes(1);
    const args = api.listPolicies.calls.mostRecent().args[0] as PolicyQuery;
    expect(args.namePrefix).toBe('no-prod');
  }));

  it('changing the enforcement filter resets skip and triggers a reload', () => {
    api.listPolicies.calls.reset();

    component.enforcement.set('MUST');
    component.skip.set(50);
    component.onFilterChange();

    expect(component.skip()).toBe(0);
    expect(api.listPolicies).toHaveBeenCalledTimes(1);
    const args = api.listPolicies.calls.mostRecent().args[0] as PolicyQuery;
    expect(args.enforcement).toBe('MUST');
    expect(args.skip).toBe(0);
  });

  it('surfaces a pin-gate banner when listPolicies 400s with a bundle-pin error code', () => {
    const pinError = new HttpErrorResponse({
      status: 400,
      error: { errorCode: 'bundle.pinning_required', detail: 'bundleId query is required when pinning is enabled' },
    });
    api.listPolicies.and.returnValue(throwError(() => pinError));

    component.reload();
    fixture.detectChanges();

    expect(component.pinningRequired()).toBeTrue();
    const banner = fixture.nativeElement.querySelector('[data-testid="banner"]');
    expect(banner).toBeTruthy();
    expect(banner.textContent).toContain('Bundle version pinning');
  });

  it('loadMore appends to the existing list rather than replacing it', () => {
    const page2: PolicyDto[] = [
      {
        id: '44444444-4444-4444-4444-444444444444',
        name: 'sandboxed',
        description: 'Run in sandbox',
        createdAt: '2026-04-03T12:00:00Z',
        createdBySubjectId: 'user:carol',
        versionCount: 1,
        activeVersionId: null,
      },
    ];
    api.listPolicies.and.returnValue(of(page2));

    expect(component.policies().length).toBe(2);
    component.loadMore();
    fixture.detectChanges();

    expect(component.policies().length).toBe(3);
    const args = api.listPolicies.calls.mostRecent().args[0] as PolicyQuery;
    expect(args.skip).toBe(25);
  });

  it('non-pin error sets a generic error banner with Retry', () => {
    const serverError = new HttpErrorResponse({
      status: 500,
      error: { title: 'Internal Server Error' },
    });
    api.listPolicies.and.returnValue(throwError(() => serverError));

    component.reload();
    fixture.detectChanges();

    expect(component.pinningRequired()).toBeFalse();
    const banner = fixture.nativeElement.querySelector('[data-testid="banner"]');
    expect(banner).toBeTruthy();
    expect(banner.textContent).toContain('Internal Server Error');
    expect(banner.textContent).toContain('Retry');
  });
});
