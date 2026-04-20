// Copyright (c) Rivoli AI 2026. All rights reserved.

import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ApiService, Item } from '../../shared/services/api.service';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterLink],
  template: `
    <h1>Dashboard</h1>
    <div class="stats">
      <div class="stat-card">
        <div class="stat-value">{{ items.length }}</div>
        <div class="stat-label">Total Items</div>
      </div>
      <div class="stat-card">
        <div class="stat-value">{{ activeCount }}</div>
        <div class="stat-label">Active</div>
      </div>
      <div class="stat-card">
        <div class="stat-value">{{ draftCount }}</div>
        <div class="stat-label">Draft</div>
      </div>
    </div>
    <p class="quick-link"><a routerLink="/items">Manage Items &rarr;</a></p>
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
  items: Item[] = [];

  get activeCount(): number {
    return this.items.filter((i) => i.status === 'Active').length;
  }

  get draftCount(): number {
    return this.items.filter((i) => i.status === 'Draft').length;
  }

  constructor(private api: ApiService) {}

  ngOnInit(): void {
    this.api.getItems().subscribe((items) => (this.items = items));
  }
}
