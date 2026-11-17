import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth';

/** Lets signed-in users through; after a reload it first restores the session from this tab's refresh token. */
export const signedInGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const signIn = inject(Router).parseUrl('/sign-in');
  return auth.signedIn() || (await auth.refresh().catch(() => false)) || signIn;
};

/** Lets Admins through, restoring the session like {@link signedInGuard}; an Operator goes to the Control room. */
export const adminGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (!auth.signedIn() && !(await auth.refresh().catch(() => false))) {
    return router.parseUrl('/sign-in');
  }

  return auth.isAdmin() || router.parseUrl('/');
};
