// Copyright (c) Rivoli AI 2026. All rights reserved.

import { Routes } from '@angular/router';
import { authGuard } from './core/guards/auth.guard';

export const routes: Routes = [
  {
    path: '',
    redirectTo: 'dashboard',
    pathMatch: 'full',
  },
  {
    path: 'dashboard',
    loadComponent: () =>
      import('./features/dashboard/dashboard.component').then(
        (m) => m.DashboardComponent
      ),
    canActivate: [authGuard],
  },
  {
    path: 'items',
    loadComponent: () =>
      import('./features/items/items.component').then(
        (m) => m.ItemsComponent
      ),
    canActivate: [authGuard],
  },
  {
    path: 'help',
    loadComponent: () =>
      import('./features/help/help.component').then(
        (m) => m.HelpComponent
      ),
  },
  {
    path: 'callback',
    loadComponent: () =>
      import('./core/auth/callback.component').then(
        (m) => m.CallbackComponent
      ),
  },
];
