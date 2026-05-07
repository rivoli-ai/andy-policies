// Copyright (c) Rivoli AI 2026. All rights reserved.

import { CommonModule } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  Input,
  computed,
  signal,
} from '@angular/core';
import { Rfc6902Op } from '../services/api.service';

interface RenderedOp {
  op: Rfc6902Op['op'];
  path: string;
  marker: '+' | '-' | '~' | '↦';
  /** Cached display string for `value` / `from`; truncated when long. */
  detail: string;
  /** True when `detail` was truncated and an Expand affordance should show. */
  truncated: boolean;
  /** Full value text for the expanded variant. */
  fullDetail: string;
}

const TRUNCATE_AT = 200;

/**
 * P9.7 (rivoli-ai/andy-policies#89) — renders an array of RFC 6902
 * operations as a colour-coded list. Reusable: P9.8 (bundle explorer)
 * also consumes RFC 6902 patches for bundle diffs and feeds them into
 * the same component.
 *
 * Marker conventions:
 * - `+` add
 * - `-` remove
 * - `~` replace
 * - `↦` move / copy (path-to-path)
 *
 * Long values truncate at 200 chars with an inline Expand toggle so a
 * single 50KB field doesn't blow up the timeline DOM.
 */
@Component({
  selector: 'app-rfc6902-diff-view',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="diff-view" data-testid="diff-view">
      <p *ngIf="ops.length === 0 && !parseError()" class="empty">No field-level changes.</p>
      <p *ngIf="parseError()" class="parse-error" data-testid="parse-error">
        Could not render diff (unexpected payload shape).
      </p>
      <ol *ngIf="ops.length > 0" class="ops">
        <li
          *ngFor="let op of rendered(); let i = index"
          class="op op-{{ op.op }}"
          [attr.data-testid]="'op-' + i">
          <span class="marker" [attr.aria-label]="op.op">{{ op.marker }}</span>
          <code class="path">{{ op.path }}</code>
          <span *ngIf="op.detail" class="detail">
            <ng-container *ngIf="!isExpanded(i); else expandedTpl">
              {{ op.detail }}
              <button
                *ngIf="op.truncated"
                type="button"
                class="expand-btn"
                (click)="toggle(i)"
                [attr.data-testid]="'expand-' + i">
                Expand
              </button>
            </ng-container>
            <ng-template #expandedTpl>
              <pre class="full">{{ op.fullDetail }}</pre>
              <button
                type="button"
                class="expand-btn"
                (click)="toggle(i)"
                [attr.data-testid]="'collapse-' + i">
                Collapse
              </button>
            </ng-template>
          </span>
        </li>
      </ol>
    </div>
  `,
  styles: [`
    .diff-view {
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: 12px;
    }
    .empty, .parse-error {
      margin: 0;
      color: var(--text-secondary);
      font-style: italic;
      font-size: 12px;
    }
    .parse-error { color: var(--error); font-style: normal; }
    .ops { margin: 0; padding: 0; list-style: none; display: flex; flex-direction: column; gap: 2px; }
    .op {
      display: flex;
      align-items: baseline;
      gap: 8px;
      padding: 2px 4px;
      border-radius: 3px;
    }
    .op-add { background: rgba(40, 167, 69, 0.07); }
    .op-remove { background: rgba(220, 53, 69, 0.07); }
    .op-replace { background: rgba(255, 193, 7, 0.07); }

    .marker {
      width: 14px;
      text-align: center;
      font-weight: 700;
      flex-shrink: 0;
    }
    .op-add .marker { color: var(--success); }
    .op-remove .marker { color: var(--error); }
    .op-replace .marker { color: #a85d00; }

    .path {
      background: var(--background);
      padding: 0 4px;
      border-radius: 2px;
      flex-shrink: 0;
    }

    .detail {
      flex: 1;
      word-break: break-word;
      color: var(--text-secondary);
    }

    .full {
      margin: 4px 0 0;
      padding: 6px 8px;
      background: var(--background);
      border: 1px solid var(--border);
      border-radius: 3px;
      white-space: pre-wrap;
      font-size: 11px;
    }

    .expand-btn {
      background: transparent;
      border: none;
      color: var(--primary);
      font: inherit;
      font-size: 11px;
      cursor: pointer;
      padding: 0 4px;
    }

    .expand-btn:hover { text-decoration: underline; }
  `],
})
export class Rfc6902DiffViewComponent {
  ops: Rfc6902Op[] = [];
  readonly parseError = signal(false);

  @Input({ required: true }) set fieldDiff(v: Rfc6902Op[] | string | null | undefined) {
    this.parseError.set(false);
    if (v == null) {
      this.ops = [];
      return;
    }
    if (Array.isArray(v)) {
      // Already parsed by the server (the actual API shape).
      this.ops = v;
      return;
    }
    if (typeof v === 'string') {
      // Defensive — some downstream consumers (P9.8 bundle diff) might
      // receive a string blob; try to parse and fall back to error.
      try {
        const parsed = JSON.parse(v);
        if (Array.isArray(parsed)) {
          this.ops = parsed as Rfc6902Op[];
          return;
        }
      } catch {
        /* fallthrough */
      }
      this.parseError.set(true);
      this.ops = [];
      return;
    }
    this.parseError.set(true);
    this.ops = [];
  }

  private readonly expandedSet = signal<ReadonlySet<number>>(new Set());

  /** Recompute on each `ops` mutation; the parent re-binds `[fieldDiff]`
   *  on every row, so this is naturally re-evaluated. */
  readonly rendered = computed<RenderedOp[]>(() =>
    this.ops.map(op => Rfc6902DiffViewComponent.render(op)),
  );

  isExpanded(index: number): boolean {
    return this.expandedSet().has(index);
  }

  toggle(index: number): void {
    const next = new Set(this.expandedSet());
    if (next.has(index)) next.delete(index); else next.add(index);
    this.expandedSet.set(next);
  }

  private static render(op: Rfc6902Op): RenderedOp {
    let marker: RenderedOp['marker'];
    switch (op.op) {
      case 'add': marker = '+'; break;
      case 'remove': marker = '-'; break;
      case 'replace': marker = '~'; break;
      default: marker = '↦';
    }
    const full = Rfc6902DiffViewComponent.formatDetail(op);
    const truncated = full.length > TRUNCATE_AT;
    return {
      op: op.op,
      path: op.path,
      marker,
      detail: truncated ? full.substring(0, TRUNCATE_AT) + '…' : full,
      truncated,
      fullDetail: full,
    };
  }

  private static formatDetail(op: Rfc6902Op): string {
    if (op.op === 'remove') return '';
    if (op.op === 'move' || op.op === 'copy') return `from ${op.from ?? '?'}`;
    if (op.op === 'test' || op.op === 'add' || op.op === 'replace') {
      if (op.value === undefined) return '';
      try {
        return `→ ${JSON.stringify(op.value)}`;
      } catch {
        return '→ <unrenderable value>';
      }
    }
    return '';
  }
}
