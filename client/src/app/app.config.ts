// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideAuth } from 'angular-auth-oidc-client';
import { routes } from './app.routes';
import { authInterceptor } from './core/interceptors/auth.interceptor';
import { environment } from '../environments/environment';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes),
    provideHttpClient(withInterceptors([authInterceptor])),
    provideAuth({
      config: {
        authority: environment.authAuthority,
        redirectUrl: window.location.origin + '/callback',
        postLogoutRedirectUri: window.location.origin,
        clientId: environment.authClientId,
        scope: 'openid profile email',
        responseType: 'code',
        silentRenew: true,
        useRefreshToken: true,
        autoUserInfo: true,
      },
    }),
  ],
};
