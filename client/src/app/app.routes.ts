// Copyright (c) Rivoli AI 2026. All rights reserved.

import { Routes } from '@angular/router';
import { authGuard } from './core/guards/auth.guard';
import { canPublishGuard } from './core/guards/can-publish.guard';

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
    path: 'policies/new',
    loadComponent: () =>
      import('./features/policies/policy-editor.component').then(
        (m) => m.PolicyEditorComponent
      ),
    canActivate: [authGuard],
  },
  {
    path: 'policies/:id',
    loadComponent: () =>
      import('./features/policies/policy-detail.component').then(
        (m) => m.PolicyDetailComponent
      ),
    canActivate: [authGuard],
  },
  {
    path: 'policies/:id/versions/:vId/edit',
    loadComponent: () =>
      import('./features/policies/policy-editor.component').then(
        (m) => m.PolicyEditorComponent
      ),
    canActivate: [authGuard],
  },
  {
    path: 'approvals',
    loadComponent: () =>
      import('./features/policies/approvals-inbox.component').then(
        (m) => m.ApprovalsInboxComponent
      ),
    canActivate: [authGuard, canPublishGuard],
  },
  {
    path: 'overrides',
    loadComponent: () =>
      import('./features/overrides/overrides-manager.component').then(
        (m) => m.OverridesManagerComponent
      ),
    canActivate: [authGuard],
  },
  {
    path: 'audit',
    loadComponent: () =>
      import('./features/audit/audit-timeline.component').then(
        (m) => m.AuditTimelineComponent
      ),
    canActivate: [authGuard],
  },
  {
    path: 'bundles',
    loadComponent: () =>
      import('./features/bundles/bundles-list.component').then(
        (m) => m.BundlesListComponent
      ),
    canActivate: [authGuard],
  },
  // Order matters: /bundles/diff must beat /bundles/:bundleId.
  {
    path: 'bundles/diff',
    loadComponent: () =>
      import('./features/bundles/bundle-diff.component').then(
        (m) => m.BundleDiffComponent
      ),
    canActivate: [authGuard],
  },
  {
    path: 'bundles/:bundleId',
    loadComponent: () =>
      import('./features/bundles/bundle-detail.component').then(
        (m) => m.BundleDetailComponent
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
