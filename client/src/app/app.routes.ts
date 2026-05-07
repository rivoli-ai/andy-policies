// Copyright (c) Rivoli AI 2026. All rights reserved.

import { Routes } from '@angular/router';
import { authGuard } from './core/guards/auth.guard';

export const routes: Routes = [
  {
    path: '',
    redirectTo: 'policies',
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
    path: 'policies',
    loadComponent: () =>
      import('./features/policies/policies-list.component').then(
        (m) => m.PoliciesListComponent
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
