// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { LifecycleDiagramComponent } from './lifecycle-diagram.component';
import { LifecycleState } from '../../shared/services/api.service';

describe('LifecycleDiagramComponent (P9.4)', () => {
  let fixture: ComponentFixture<LifecycleDiagramComponent>;

  function build(current: LifecycleState): void {
    fixture = TestBed.createComponent(LifecycleDiagramComponent);
    fixture.componentRef.setInput('current', current);
    fixture.detectChanges();
  }

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [LifecycleDiagramComponent],
    }).compileComponents();
  });

  it('renders all four state nodes', () => {
    build('Draft');
    const nodes = fixture.nativeElement.querySelectorAll('.node');
    expect(nodes.length).toBe(4);
    expect(fixture.nativeElement.querySelector('[data-testid="node-Draft"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="node-Active"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="node-WindingDown"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="node-Retired"]')).toBeTruthy();
  });

  it('marks only the current state as active', () => {
    build('Active');
    const active = fixture.nativeElement.querySelector('[data-testid="node-Active"]');
    expect(active.classList).toContain('active');
    const draft = fixture.nativeElement.querySelector('[data-testid="node-Draft"]');
    expect(draft.classList).not.toContain('active');
  });

  it('moves the active class when current changes', () => {
    build('Draft');
    expect(fixture.nativeElement.querySelector('[data-testid="node-Draft"]').classList).toContain('active');

    fixture.componentRef.setInput('current', 'Retired');
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="node-Draft"]').classList).not.toContain('active');
    expect(fixture.nativeElement.querySelector('[data-testid="node-Retired"]').classList).toContain('active');
  });
});
