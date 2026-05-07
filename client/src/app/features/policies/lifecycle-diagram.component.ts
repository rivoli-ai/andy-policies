// Copyright (c) Rivoli AI 2026. All rights reserved.

import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, Input } from '@angular/core';
import { LifecycleState } from '../../shared/services/api.service';

/**
 * P9.4 (rivoli-ai/andy-policies#69) — tiny static SVG of the four-state
 * lifecycle graph. The `current` state gets `.active` styling; the rest are
 * dimmed. No dynamic graph library — the shape is fixed in P2 and only
 * changes via a coordinated server+client release.
 */
@Component({
  selector: 'app-lifecycle-diagram',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg
      viewBox="0 0 360 110"
      class="diagram"
      role="img"
      [attr.aria-label]="'Lifecycle state diagram, current state ' + current">
      <defs>
        <marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
          <path d="M0,0 L10,5 L0,10 z" />
        </marker>
      </defs>

      <g class="node" [class.active]="current === 'Draft'" data-testid="node-Draft">
        <circle cx="40" cy="40" r="26"></circle>
        <text x="40" y="44" text-anchor="middle">Draft</text>
      </g>
      <g class="node" [class.active]="current === 'Active'" data-testid="node-Active">
        <circle cx="140" cy="40" r="26"></circle>
        <text x="140" y="44" text-anchor="middle">Active</text>
      </g>
      <g class="node" [class.active]="current === 'WindingDown'" data-testid="node-WindingDown">
        <circle cx="240" cy="40" r="28"></circle>
        <text x="240" y="38" text-anchor="middle">Wind</text>
        <text x="240" y="50" text-anchor="middle">Down</text>
      </g>
      <g class="node" [class.active]="current === 'Retired'" data-testid="node-Retired">
        <circle cx="330" cy="40" r="22"></circle>
        <text x="330" y="44" text-anchor="middle">Retired</text>
      </g>

      <path d="M66,40 L114,40" class="arrow" marker-end="url(#arrow)"></path>
      <path d="M168,40 L210,40" class="arrow" marker-end="url(#arrow)"></path>
      <path d="M270,40 L308,40" class="arrow" marker-end="url(#arrow)"></path>
      <path d="M155,66 C200,100 280,100 320,62" class="arrow" fill="none" marker-end="url(#arrow)"></path>
    </svg>
  `,
  styles: [`
    :host { display: block; }
    .diagram { width: 100%; max-width: 380px; height: auto; }
    .node circle {
      fill: var(--surface);
      stroke: var(--border);
      stroke-width: 1.5;
    }
    .node text {
      fill: var(--text-secondary);
      font-size: 11px;
      font-weight: 500;
      font-family: ui-sans-serif, system-ui, sans-serif;
    }
    .node.active circle {
      fill: var(--primary);
      stroke: var(--primary);
    }
    .node.active text {
      fill: white;
    }
    .arrow {
      stroke: var(--border);
      stroke-width: 1.5;
      fill: none;
    }
    #arrow path { fill: var(--border); }
  `],
})
export class LifecycleDiagramComponent {
  @Input({ required: true }) current!: LifecycleState;
}
