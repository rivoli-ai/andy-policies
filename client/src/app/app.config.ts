// Copyright (c) Rivoli AI 2026. All rights reserved.

import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideAuth } from 'angular-auth-oidc-client';
import { provideMonacoEditor } from 'ngx-monaco-editor-v2';
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
    // #192 — Monaco editor wraps the rules-JSON editor in PolicyEditor.
    // Assets are copied into /assets/monaco/vs by angular.json's assets
    // glob, so the AMD loader resolves modules from there at runtime.
    // The editor itself is only instantiated when /policies/new or
    // /policies/:id/versions/:vId/edit lazy-loads PolicyEditorComponent —
    // the initial bundle stays unaffected.
    provideMonacoEditor({
      baseUrl: '/assets/monaco/vs',
    }),
  ],
};
