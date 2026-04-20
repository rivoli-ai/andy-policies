// Copyright (c) Rivoli AI 2026. All rights reserved.

import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';

export interface Item {
  id: string;
  name: string;
  description: string | null;
  status: string;
  createdBy: string;
  createdAt: string;
  updatedAt: string | null;
}

export interface CreateItemRequest {
  name: string;
  description?: string;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private baseUrl = environment.apiUrl;

  constructor(private http: HttpClient) {}

  getItems(): Observable<Item[]> {
    return this.http.get<Item[]>(`${this.baseUrl}/items`);
  }

  getItem(id: string): Observable<Item> {
    return this.http.get<Item>(`${this.baseUrl}/items/${id}`);
  }

  createItem(request: CreateItemRequest): Observable<Item> {
    return this.http.post<Item>(`${this.baseUrl}/items`, request);
  }

  updateItem(id: string, request: CreateItemRequest): Observable<Item> {
    return this.http.put<Item>(`${this.baseUrl}/items/${id}`, request);
  }

  deleteItem(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/items/${id}`);
  }
}
