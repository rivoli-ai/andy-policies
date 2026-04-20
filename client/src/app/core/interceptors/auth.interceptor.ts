// Copyright (c) Rivoli AI 2026. All rights reserved.

import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { switchMap } from 'rxjs';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const oidcService = inject(OidcSecurityService);

  if (req.url.startsWith('/api')) {
    return oidcService.getAccessToken().pipe(
      switchMap((token) => {
        if (token) {
          const authReq = req.clone({
            setHeaders: { Authorization: `Bearer ${token}` },
          });
          return next(authReq);
        }
        return next(req);
      })
    );
  }

  return next(req);
};
