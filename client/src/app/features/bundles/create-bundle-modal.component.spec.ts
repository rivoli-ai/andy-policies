// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import {
  ApiService,
  BundleDto,
  CreateBundleRequest,
} from '../../shared/services/api.service';
import { CreateBundleModalComponent } from './create-bundle-modal.component';

describe('CreateBundleModalComponent (P9.8)', () => {
  let fixture: ComponentFixture<CreateBundleModalComponent>;
  let component: CreateBundleModalComponent;
  let api: jasmine.SpyObj<ApiService>;

  beforeEach(async () => {
    api = jasmine.createSpyObj<ApiService>('ApiService', ['createBundle']);
    await TestBed.configureTestingModule({
      imports: [CreateBundleModalComponent],
      providers: [{ provide: ApiService, useValue: api }],
    }).compileComponents();

    fixture = TestBed.createComponent(CreateBundleModalComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('rejects an UPPERCASE name (slug regex)', () => {
    component.form.patchValue({ name: 'BAD' });
    component.form.controls.name.markAsTouched();
    expect(component.form.controls.name.errors?.['pattern']).toBeTruthy();
  });

  it('rejects a name starting with a non-alphanumeric character', () => {
    component.form.patchValue({ name: '-bad' });
    expect(component.form.controls.name.errors?.['pattern']).toBeTruthy();
  });

  it('accepts a valid slug like release-2026-05', () => {
    component.form.patchValue({ name: 'release-2026-05' });
    expect(component.form.controls.name.errors).toBeNull();
  });

  it('rationale shorter than 10 chars makes the form invalid', () => {
    component.form.patchValue({ name: 'release', rationale: 'short' });
    expect(component.form.invalid).toBeTrue();
  });

  it('submit posts the trimmed payload and emits the created bundle', () => {
    const created: BundleDto = {
      id: 'bid-1', name: 'release-2026-05', description: 'desc',
      createdAt: '2026-05-07T00:00:00Z', createdBySubjectId: 'u',
      snapshotHash: 'aabb', state: 'Active',
      deletedAt: null, deletedBySubjectId: null,
    };
    api.createBundle.and.returnValue(of(created));

    const captures: (BundleDto | null)[] = [];
    component.closed.subscribe(v => captures.push(v));

    // Name is validated against the un-trimmed value (slug regex rejects
    // leading/trailing spaces); description + rationale exercise the
    // trimming submit() applies before posting.
    component.form.patchValue({
      name: 'release-2026-05',
      description: '  description here  ',
      rationale: '  rationale text long enough  ',
    });
    component.submit();

    const req = api.createBundle.calls.mostRecent().args[0] as CreateBundleRequest;
    expect(req.name).toBe('release-2026-05');
    expect(req.description).toBe('description here');
    expect(req.rationale).toBe('rationale text long enough');
    expect(captures).toEqual([created]);
  });

  it('null description when blank', () => {
    api.createBundle.and.returnValue(of({} as BundleDto));
    component.form.patchValue({
      name: 'release',
      description: '   ',
      rationale: 'rationale long enough',
    });
    component.submit();

    const req = api.createBundle.calls.mostRecent().args[0] as CreateBundleRequest;
    expect(req.description).toBeNull();
  });

  it('on 409 surfaces the inline banner', () => {
    api.createBundle.and.returnValue(
      throwError(() => new HttpErrorResponse({
        status: 409,
        error: { detail: 'A bundle with this name already exists.' },
      })),
    );

    component.form.patchValue({ name: 'release', rationale: 'rationale long enough' });
    component.submit();
    fixture.detectChanges();

    expect(component.errorMessage()).toContain('already exists');
    expect(fixture.nativeElement.querySelector('[data-testid="banner"]')).toBeTruthy();
  });

  it('cancel emits null without calling the API', () => {
    const captures: (BundleDto | null)[] = [];
    component.closed.subscribe(v => captures.push(v));

    component.cancel();

    expect(api.createBundle).not.toHaveBeenCalled();
    expect(captures).toEqual([null]);
  });
});
