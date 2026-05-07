// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';
import { ApiService, BindingDto } from '../../shared/services/api.service';
import { DeleteBindingModalComponent } from './delete-binding-modal.component';

describe('DeleteBindingModalComponent (P9.5)', () => {
  let fixture: ComponentFixture<DeleteBindingModalComponent>;
  let component: DeleteBindingModalComponent;
  let api: jasmine.SpyObj<ApiService>;

  const binding: BindingDto = {
    id: 'bid-1', policyVersionId: 'vid-1',
    targetType: 'Repo', targetRef: 'rivoli-ai/example',
    bindStrength: 'Mandatory',
    createdAt: '2026-04-01T00:00:00Z', createdBySubjectId: 'u',
    deletedAt: null, deletedBySubjectId: null,
  };

  beforeEach(async () => {
    api = jasmine.createSpyObj<ApiService>('ApiService', ['deleteBinding']);
    await TestBed.configureTestingModule({
      imports: [DeleteBindingModalComponent],
      providers: [{ provide: ApiService, useValue: api }],
    }).compileComponents();

    fixture = TestBed.createComponent(DeleteBindingModalComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('binding', binding);
    fixture.detectChanges();
  });

  it('confirm sends rationale to deleteBinding and emits deleted=true', () => {
    api.deleteBinding.and.returnValue(of(undefined));
    const captures: ({ deleted: boolean; bindingId: string } | null)[] = [];
    component.closed.subscribe(v => captures.push(v));

    component.rationale = 'no longer relevant';
    component.confirm();

    expect(api.deleteBinding).toHaveBeenCalledWith('bid-1', 'no longer relevant');
    expect(captures).toEqual([{ deleted: true, bindingId: 'bid-1' }]);
  });

  it('confirm trims rationale before calling delete', () => {
    api.deleteBinding.and.returnValue(of(undefined));

    component.rationale = '   spaces around   ';
    component.confirm();

    expect(api.deleteBinding).toHaveBeenCalledWith('bid-1', 'spaces around');
  });

  it('on 409 keeps modal open and surfaces detail inline', () => {
    const conflict = new HttpErrorResponse({
      status: 409,
      error: { detail: 'Cannot delete: relied on by template X.' },
    });
    api.deleteBinding.and.returnValue(throwError(() => conflict));
    let emitCount = 0;
    component.closed.subscribe(() => emitCount++);

    component.confirm();
    fixture.detectChanges();

    expect(emitCount).toBe(0);
    expect(component.errorMessage()).toContain('relied on by template X');
    const banner = fixture.nativeElement.querySelector('[data-testid="banner"]');
    expect(banner).toBeTruthy();
  });

  it('cancel emits null without calling delete', () => {
    const captures: unknown[] = [];
    component.closed.subscribe(v => captures.push(v));

    component.cancel();

    expect(api.deleteBinding).not.toHaveBeenCalled();
    expect(captures).toEqual([null]);
  });
});
