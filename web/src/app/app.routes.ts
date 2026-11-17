import { Routes } from '@angular/router';
import { SignIn } from './auth/sign-in';
import { signedInGuard } from './auth/signed-in.guard';
import { ControlRoom } from './control-room/control-room';

export const routes: Routes = [
  { path: 'sign-in', component: SignIn, title: 'Sign in · FieldTrack' },
  { path: '', component: ControlRoom, canActivate: [signedInGuard], title: 'Control room · FieldTrack' },
  { path: '**', redirectTo: '' },
];
