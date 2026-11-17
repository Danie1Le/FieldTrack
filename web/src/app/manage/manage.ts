import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet, Routes } from '@angular/router';
import { AuthService } from '../auth/auth';
import { AssetsPage } from './assets-page';
import { DevicesPage } from './devices-page';

/** The frame around the management screens (Admins only, #40): their navigation, then the chosen screen. */
@Component({
  selector: 'app-manage',
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <header class="bar">
      <h1>FieldTrack</h1>
      <span class="muted">Manage</span>
      <nav aria-label="Management screens">
        <a class="btn" routerLink="assets" routerLinkActive="current" ariaCurrentWhenActive="page">Assets</a>
        <a class="btn" routerLink="devices" routerLinkActive="current" ariaCurrentWhenActive="page">Devices</a>
      </nav>
      <nav class="links">
        <a class="btn" routerLink="/geofences">Geofences</a>
        <a class="btn" routerLink="/">Control room</a>
        <button type="button" class="btn" (click)="auth.signOut()">Sign out</button>
      </nav>
    </header>
    <router-outlet />
  `,
  styles: `
    :host { display: flex; flex-direction: column; gap: 10px; padding: 12px 16px; min-height: 100%; }
    .bar { display: flex; align-items: center; gap: 14px; flex-wrap: wrap; }
    .bar h1 { font-size: 20px; }
    nav { display: flex; gap: 8px; flex-wrap: wrap; }
    .links { margin-left: auto; }
    a.btn { color: inherit; text-decoration: none; }
    a.current { border-color: var(--accent); color: var(--accent); }
  `,
})
export class Manage {
  protected readonly auth = inject(AuthService);
}

/** The management screens under `/manage`; the app routes guard them with `adminGuard`. */
export const manageRoutes: Routes = [
  {
    path: '',
    component: Manage,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'assets' },
      { path: 'assets', component: AssetsPage, title: 'Assets · FieldTrack' },
      { path: 'devices', component: DevicesPage, title: 'Devices · FieldTrack' },
    ],
  },
];
