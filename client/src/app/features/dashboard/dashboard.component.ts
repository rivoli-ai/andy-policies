// Copyright (c) Rivoli AI 2026. All rights reserved.

import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ApiService, PolicyDto } from '../../shared/services/api.service';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterLink],
  template: `
    <h1>Dashboard</h1>
    <div class="stats">
      <div class="stat-card">
        <div class="stat-value">{{ policies.length }}</div>
        <div class="stat-label">Total Policies</div>
      </div>
      <div class="stat-card">
        <div class="stat-value">{{ activeCount }}</div>
        <div class="stat-label">With active version</div>
      </div>
      <div class="stat-card">
        <div class="stat-value">{{ draftOnlyCount }}</div>
        <div class="stat-label">Draft only</div>
      </div>
    </div>
    <p class="quick-link"><a routerLink="/policies">Browse policies &rarr;</a></p>
  `,
  styles: [`
    h1 { margin-bottom: 24px; }
    .stats { display: flex; gap: 16px; margin-bottom: 24px; }
    .stat-card {
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 24px;
      min-width: 160px;
      text-align: center;
    }
    .stat-value { font-size: 32px; font-weight: 600; color: var(--primary); }
    .stat-label { font-size: 14px; color: var(--text-secondary); margin-top: 4px; }
    .quick-link { font-size: 14px; }
  `],
})
export class DashboardComponent implements OnInit {
  policies: PolicyDto[] = [];

  get activeCount(): number {
    return this.policies.filter((p) => p.activeVersionId !== null).length;
  }

  get draftOnlyCount(): number {
    return this.policies.filter((p) => p.activeVersionId === null).length;
  }

  constructor(private api: ApiService) {}

  ngOnInit(): void {
    // Best-effort: dashboard is informational; if the bundle pinning gate
    // refuses the live read it's not worth surfacing here. Defer richer
    // counts (per-state, per-severity) to a follow-up dashboard story.
    this.api.listPolicies({ take: 100 }).subscribe({
      next: (policies) => (this.policies = policies),
      error: () => (this.policies = []),
    });
  }
}
