// Copyright (c) Rivoli AI 2026. All rights reserved.

import { Component, OnInit, inject } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive } from '@angular/router';
import { CommonModule } from '@angular/common';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { PermissionsService } from './core/auth/permissions.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, CommonModule],
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.scss'],
})
export class AppComponent implements OnInit {
  title = 'Andy Policies';
  isAuthenticated = false;
  userName = '';

  // Public so the template can `*ngIf="permissions.canPublish()"` on
  // the Approvals nav link — keeps the link out of the DOM for users
  // who can't act on it (matches the "buttons hidden when can't" rule
  // from the P9.3 acceptance criteria).
  readonly permissions = inject(PermissionsService);
  private readonly oidcService = inject(OidcSecurityService);

  ngOnInit(): void {
    this.oidcService.checkAuth().subscribe(({ isAuthenticated, userData }) => {
      this.isAuthenticated = isAuthenticated;
      this.userName = userData?.name || userData?.email || '';
      // Refresh the permission allow-set on every successful auth check.
      // The PermissionsService caches it for the rest of the SPA session;
      // we re-fetch on logoff/login to pick up role changes that landed
      // out-of-band. Failures are swallowed — the guards / nav defaults
      // resolve to "no extra permissions", which is the safe direction.
      if (isAuthenticated) {
        this.permissions.refresh().subscribe({ error: () => { /* ignored */ } });
      }
    });
  }

  login(): void {
    this.oidcService.authorize();
  }

  logout(): void {
    this.oidcService.logoff().subscribe();
  }
}
