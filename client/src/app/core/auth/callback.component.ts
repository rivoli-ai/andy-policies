// Copyright (c) Rivoli AI 2026. All rights reserved.

import { Component, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';

@Component({
  selector: 'app-callback',
  standalone: true,
  template: '<p>Authenticating...</p>',
})
export class CallbackComponent implements OnInit {
  constructor(
    private oidcService: OidcSecurityService,
    private router: Router
  ) {}

  ngOnInit(): void {
    this.oidcService.checkAuth().subscribe(({ isAuthenticated }) => {
      this.router.navigate([isAuthenticated ? '/dashboard' : '/']);
    });
  }
}
