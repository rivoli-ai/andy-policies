// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ScopeChipInputComponent } from './scope-chip-input.component';

describe('ScopeChipInputComponent (P9.2)', () => {
  let fixture: ComponentFixture<ScopeChipInputComponent>;
  let component: ScopeChipInputComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ScopeChipInputComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(ScopeChipInputComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  function input(): HTMLInputElement {
    return fixture.nativeElement.querySelector('[data-testid="scope-input"]');
  }

  it('renders existing values written via writeValue as chips', () => {
    component.writeValue(['prod', 'project:frontend']);
    fixture.detectChanges();

    const chips = fixture.nativeElement.querySelectorAll('.chip');
    expect(chips.length).toBe(2);
    expect(chips[0].textContent).toContain('prod');
    expect(chips[1].textContent).toContain('project:frontend');
  });

  it('commits a valid value on Enter and emits onChange', () => {
    const captures: string[][] = [];
    component.registerOnChange(v => captures.push(v));

    component.draft = 'project:frontend';
    component.onKeyDown(new KeyboardEvent('keydown', { key: 'Enter' }));
    fixture.detectChanges();

    expect(component.value()).toEqual(['project:frontend']);
    expect(captures.at(-1)).toEqual(['project:frontend']);
    expect(component.draft).toBe('');
  });

  it('rejects an invalid value and surfaces an error message', () => {
    const captures: string[][] = [];
    component.registerOnChange(v => captures.push(v));

    component.draft = 'FOO_BAR';
    component.onKeyDown(new KeyboardEvent('keydown', { key: 'Enter' }));
    fixture.detectChanges();

    expect(component.value()).toEqual([]);
    expect(captures.length).toBe(0);
    expect(component.error()).toContain('not a valid scope');
    const err = fixture.nativeElement.querySelector('[data-testid="chip-error"]');
    expect(err).toBeTruthy();
  });

  it('comma also commits the chip', () => {
    component.draft = 'prod';
    component.onKeyDown(new KeyboardEvent('keydown', { key: ',' }));
    expect(component.value()).toEqual(['prod']);
  });

  it('Backspace on empty draft removes the last chip', () => {
    component.writeValue(['prod', 'staging']);
    component.draft = '';

    component.onKeyDown(new KeyboardEvent('keydown', { key: 'Backspace' }));

    expect(component.value()).toEqual(['prod']);
  });

  it('does not double-add the same value', () => {
    component.draft = 'prod';
    component.onKeyDown(new KeyboardEvent('keydown', { key: 'Enter' }));
    component.draft = 'prod';
    component.onKeyDown(new KeyboardEvent('keydown', { key: 'Enter' }));

    expect(component.value()).toEqual(['prod']);
  });

  it('disables interaction when setDisabledState(true)', () => {
    component.setDisabledState(true);

    expect(component.disabled()).toBeTrue();
    // remove() must no-op while disabled.
    component.writeValue(['prod']);
    component.remove(0);
    expect(component.value()).toEqual(['prod']);
  });
});
