import { inject } from '@angular/core';
import { CanActivateFn, CanMatchFn, Router } from '@angular/router';
import { AuthService } from './auth';

/** Lets signed-in users through; after a reload it first restores the session from this tab's refresh token. */
export const signedInGuard: CanActivateFn = async () => {
  const auth = inject(AuthService);
  const signIn = inject(Router).parseUrl('/sign-in');
  return auth.signedIn() || (await auth.refresh().catch(() => false)) || signIn;
};

/**
 * Lets Admins through, restoring the session like {@link signedInGuard}; an Operator goes to the Control room. Also a
 * `canMatch` guard, so a lazy area's code is not even downloaded for an Operator.
 */
export const adminGuard: CanActivateFn & CanMatchFn = async () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  if (!auth.signedIn() && !(await auth.refresh().catch(() => false))) {
    return router.parseUrl('/sign-in');
  }

  return auth.isAdmin() || router.parseUrl('/');
};
