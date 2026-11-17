import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { AuthService } from './auth';
import { adminGuard } from './signed-in.guard';

describe('adminGuard', () => {
  function run(session: { signedIn: boolean; restores: boolean; admin: boolean }) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: AuthService,
          useValue: {
            signedIn: () => session.signedIn,
            refresh: async () => session.restores,
            isAdmin: () => session.admin,
          },
        },
      ],
    });
    return TestBed.runInInjectionContext(() => adminGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot)) as Promise<
      boolean | UrlTree
    >;
  }

  const url = (result: boolean | UrlTree) => (result instanceof UrlTree ? TestBed.inject(Router).serializeUrl(result) : result);

  it('lets an Admin through, also after a reload restores the session', async () => {
    expect(await run({ signedIn: true, restores: false, admin: true })).toBe(true);
    TestBed.resetTestingModule();
    expect(await run({ signedIn: false, restores: true, admin: true })).toBe(true);
  });

  it('sends an Operator to the Control room and anyone signed out to sign-in', async () => {
    expect(url(await run({ signedIn: true, restores: false, admin: false }))).toBe('/');
    TestBed.resetTestingModule();
    expect(url(await run({ signedIn: false, restores: false, admin: false }))).toBe('/sign-in');
  });
});
