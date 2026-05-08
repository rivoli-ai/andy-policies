// Copyright (c) Rivoli AI 2026. All rights reserved.

import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { ApiService } from '../../shared/services/api.service';
import { PermissionsService } from './permissions.service';

describe('PermissionsService (P9.3 #68)', () => {
  let api: jasmine.SpyObj<ApiService>;
  let service: PermissionsService;

  beforeEach(() => {
    api = jasmine.createSpyObj<ApiService>('ApiService', ['getMyPermissions']);
    TestBed.configureTestingModule({
      providers: [{ provide: ApiService, useValue: api }],
    });
    service = TestBed.inject(PermissionsService);
  });

  it('starts with no permissions until refresh()', () => {
    expect(service.canPublish()).toBeFalse();
    expect(service.canPropose()).toBeFalse();
    expect(service.isLoaded()).toBeFalse();
  });

  it('refresh() populates the set and the canPublish/canPropose signals recompute', () => {
    api.getMyPermissions.and.returnValue(of([
      'andy-policies:policy:read',
      'andy-policies:policy:publish',
      'andy-policies:policy:propose',
    ]));

    service.refresh().subscribe();

    expect(service.isLoaded()).toBeTrue();
    expect(service.canPublish()).toBeTrue();
    expect(service.canPropose()).toBeTrue();
    expect(service.canReject()).toBeFalse();
    expect(service.has('andy-policies:policy:read')).toBeTrue();
    expect(service.has('andy-policies:override:revoke')).toBeFalse();
  });

  it('setForTesting overrides the cache without touching the API', () => {
    service.setForTesting(['andy-policies:policy:reject']);
    expect(service.canReject()).toBeTrue();
    expect(api.getMyPermissions).not.toHaveBeenCalled();
  });
});
