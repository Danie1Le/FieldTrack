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
    path: 'assets/:id',
    // Lazy: Chart.js loads only when an Asset is opened (initial bundle budget).
    loadComponent: () => import('./asset-detail/asset-detail').then((m) => m.AssetDetail),
    canActivate: [signedInGuard],
    title: 'Asset · FieldTrack',
  },
  {
    path: 'geofences',
    // Leaflet-Geoman patches the global `L` that Leaflet sets as it loads, and equips only maps created after that:
    // load Leaflet, then Geoman, then the page. Lazy, so only Admins on this page download the drawing toolbar.
    loadComponent: () =>
      import('leaflet')
        .then(() => import('@geoman-io/leaflet-geoman-free'))
        .then(() => import('./geofences/geofences-page'))
        .then((m) => m.GeofencesPage),
    canActivate: [adminGuard],
    title: 'Geofences · FieldTrack',
  },
  {
    path: 'users',
    loadComponent: () => import('./users/users-page').then((m) => m.UsersPage),
    canActivate: [adminGuard],
    title: 'Users · FieldTrack',
  },
  { path: '**', redirectTo: '' },
];
