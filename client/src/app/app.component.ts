// Copyright (c) Rivoli AI 2026. All rights reserved.

import { Component, OnInit } from '@angular/core';
import { RouterOutlet, RouterLink, RouterLinkActive } from '@angular/router';
import { CommonModule } from '@angular/common';
import { OidcSecurityService } from 'angular-auth-oidc-client';

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

  constructor(private oidcService: OidcSecurityService) {}

  ngOnInit(): void {
    this.oidcService.checkAuth().subscribe(({ isAuthenticated, userData }) => {
      this.isAuthenticated = isAuthenticated;
      this.userName = userData?.name || userData?.email || '';
    });
  }

  login(): void {
    this.oidcService.authorize();
  }

  logout(): void {
    this.oidcService.logoff().subscribe();
  }
}
