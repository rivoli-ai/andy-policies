// Copyright (c) Rivoli AI 2026. All rights reserved.

import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { map } from 'rxjs';

export const authGuard: CanActivateFn = () => {
  const oidcService = inject(OidcSecurityService);
  const router = inject(Router);

  return oidcService.isAuthenticated$.pipe(
    map(({ isAuthenticated }) => {
      if (!isAuthenticated) {
        oidcService.authorize();
        return false;
      }
      return true;
    })
  );
};
