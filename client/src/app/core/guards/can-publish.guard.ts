// Copyright (c) Rivoli AI 2026. All rights reserved.

import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { PermissionsService } from '../auth/permissions.service';

/**
 * #68 / P9.3 — gates the `/approvals` route on
 * `andy-policies:policy:publish`. Layered defence: the API itself
 * returns 403 on the inbox endpoint without the permission, but
 * hiding the route entirely keeps the SPA from rendering a UI no
 * one can act on, and avoids a flash of "loading" before the
 * forbidden response.
 *
 * Stack with `authGuard` first so unauthenticated users get the OIDC
 * redirect rather than a forbidden bounce.
 */
export const canPublishGuard: CanActivateFn = () => {
  const perms = inject(PermissionsService);
  const router = inject(Router);

  // The PermissionsService is refreshed at app bootstrap (see
  // AppComponent.ngOnInit). If the user navigates to /approvals
  // before the refresh completes, `canPublish()` will be false and
  // we'll redirect — that's the safe direction. The user can land on
  // /policies and click into /approvals once the refresh settles
  // (~one round trip), or they can be deep-linked back via the
  // toast/banner UX a future iteration may add.
  return perms.canPublish() ? true : router.parseUrl('/policies');
};
