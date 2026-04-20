// Copyright (c) Rivoli AI 2026. All rights reserved.

import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService, Item, CreateItemRequest } from '../../shared/services/api.service';

@Component({
  selector: 'app-items',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <h1>Items</h1>

    <div class="create-form">
      <input [(ngModel)]="newName" placeholder="Item name" class="input" />
      <input [(ngModel)]="newDescription" placeholder="Description (optional)" class="input" />
      <button class="btn-primary" (click)="create()" [disabled]="!newName">Create</button>
    </div>

    <table class="items-table" *ngIf="items.length > 0">
      <thead>
        <tr>
          <th>Name</th>
          <th>Description</th>
          <th>Status</th>
          <th>Created</th>
          <th>Actions</th>
        </tr>
      </thead>
      <tbody>
        <tr *ngFor="let item of items">
          <td>{{ item.name }}</td>
          <td>{{ item.description || '-' }}</td>
          <td><span class="badge" [class]="'badge-' + item.status.toLowerCase()">{{ item.status }}</span></td>
          <td>{{ item.createdAt | date:'short' }}</td>
          <td><button class="btn-secondary btn-sm" (click)="deleteItem(item.id)">Delete</button></td>
        </tr>
      </tbody>
    </table>

    <p *ngIf="items.length === 0" class="empty">No items yet. Create one above.</p>
  `,
  styles: [`
    h1 { margin-bottom: 24px; }
    .create-form { display: flex; gap: 8px; margin-bottom: 24px; }
    .input {
      padding: 8px 12px; border: 1px solid var(--border); border-radius: 4px;
      font-size: 14px; flex: 1;
    }
    .items-table { width: 100%; border-collapse: collapse; background: var(--surface); border-radius: 8px; overflow: hidden; }
    th, td { padding: 12px 16px; text-align: left; border-bottom: 1px solid var(--border); font-size: 14px; }
    th { background: var(--background); font-weight: 600; color: var(--text-secondary); }
    .badge { padding: 2px 8px; border-radius: 12px; font-size: 12px; font-weight: 500; }
    .badge-draft { background: #fce8e6; color: var(--error); }
    .badge-active { background: #e6f4ea; color: var(--success); }
    .badge-archived { background: var(--background); color: var(--text-secondary); }
    .btn-sm { padding: 4px 8px; font-size: 12px; }
    .empty { color: var(--text-secondary); font-size: 14px; }
  `],
})
export class ItemsComponent implements OnInit {
  items: Item[] = [];
  newName = '';
  newDescription = '';

  constructor(private api: ApiService) {}

  ngOnInit(): void {
    this.loadItems();
  }

  loadItems(): void {
    this.api.getItems().subscribe((items) => (this.items = items));
  }

  create(): void {
    const request: CreateItemRequest = {
      name: this.newName,
      description: this.newDescription || undefined,
    };
    this.api.createItem(request).subscribe(() => {
      this.newName = '';
      this.newDescription = '';
      this.loadItems();
    });
  }

  deleteItem(id: string): void {
    this.api.deleteItem(id).subscribe(() => this.loadItems());
  }
}
