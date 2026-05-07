// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Rfc6902DiffViewComponent } from './rfc6902-diff-view.component';

describe('Rfc6902DiffViewComponent (P9.7)', () => {
  let fixture: ComponentFixture<Rfc6902DiffViewComponent>;
  let component: Rfc6902DiffViewComponent;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [Rfc6902DiffViewComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(Rfc6902DiffViewComponent);
    component = fixture.componentInstance;
  });

  function setOps(ops: unknown): void {
    fixture.componentRef.setInput('fieldDiff', ops);
    fixture.detectChanges();
  }

  it('renders empty-state copy when there are no ops', () => {
    setOps([]);
    expect(fixture.nativeElement.querySelector('.empty')).toBeTruthy();
  });

  it('renders + / - / ~ markers per op kind', () => {
    setOps([
      { op: 'add', path: '/policy/name', value: 'x' },
      { op: 'remove', path: '/policy/old' },
      { op: 'replace', path: '/policy/severity', value: 'critical' },
    ]);
    const markers = Array.from(fixture.nativeElement.querySelectorAll('.marker'))
      .map((m: any) => m.textContent.trim());
    expect(markers).toEqual(['+', '-', '~']);
  });

  it('formats path-to-path for move/copy with the from arrow', () => {
    setOps([{ op: 'move', path: '/policy/dst', from: '/policy/src' }]);
    const detail = fixture.nativeElement.querySelector('.detail').textContent;
    expect(detail).toContain('from /policy/src');
  });

  it('truncates a long value past 200 chars and exposes Expand', () => {
    const big = 'x'.repeat(500);
    setOps([{ op: 'add', path: '/big', value: big }]);

    const detail = fixture.nativeElement.querySelector('.detail').textContent;
    expect(detail).toContain('…');
    expect(fixture.nativeElement.querySelector('[data-testid="expand-0"]')).toBeTruthy();

    component.toggle(0);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="collapse-0"]')).toBeTruthy();
    const full = fixture.nativeElement.querySelector('.full').textContent;
    expect(full.length).toBeGreaterThan(200);
  });

  it('accepts a stringified array as a defensive parse path', () => {
    setOps(JSON.stringify([{ op: 'add', path: '/a', value: 1 }]));
    expect(component.ops.length).toBe(1);
    expect(component.parseError()).toBeFalse();
  });

  it('flags parseError when the input is malformed JSON or not an array', () => {
    setOps('not json');
    expect(component.parseError()).toBeTrue();
    expect(fixture.nativeElement.querySelector('[data-testid="parse-error"]')).toBeTruthy();
  });

  it('treats null fieldDiff as empty without parseError', () => {
    setOps(null);
    expect(component.ops.length).toBe(0);
    expect(component.parseError()).toBeFalse();
  });
});
