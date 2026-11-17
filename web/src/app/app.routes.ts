import { Routes } from '@angular/router';
import { SignIn } from './auth/sign-in';
import { adminGuard, signedInGuard } from './auth/signed-in.guard';

export const routes: Routes = [
  { path: 'sign-in', component: SignIn, title: 'Sign in · FieldTrack' },
  {
    path: '',
    // Lazy: Leaflet and SignalR load only for a signed-in user, keeping the sign-in page inside the initial budget.
    loadComponent: () => import('./control-room/control-room').then((m) => m.ControlRoom),
    canActivate: [signedInGuard],
    title: 'Control room · FieldTrack',
  },
  {
    path: 'geofences',
    // Lazy, and Leaflet-Geoman lazier still: only Admins on this page load the drawing toolbar.
    loadComponent: () => import('./geofences/geofences-page').then((m) => m.GeofencesPage),
    canActivate: [adminGuard],
    title: 'Geofences · FieldTrack',
  },
  { path: '**', redirectTo: '' },
];
