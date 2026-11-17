import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../app.routes';
import { AuthService } from '../auth/auth';
import { adminGuard } from '../auth/signed-in.guard';
import { manageRoutes } from './manage';

describe('Manage', () => {
  it('is for Admins only: Operators never reach the management screens', () => {
    expect(routes.find((route) => route.path === 'manage')!.canActivate).toEqual([adminGuard]);
  });

  it('opens on the Assets screen and links each management screen', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'manage', children: manageRoutes }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: {} },
      ],
    });
    const harness = await RouterTestingHarness.create('/manage');

    expect(TestBed.inject(Router).url).toBe('/manage/assets');
    const links = [...((harness.fixture.nativeElement as HTMLElement).querySelectorAll('nav[aria-label="Management screens"] a'))];
    expect(links.map((link) => [link.textContent!.trim(), link.getAttribute('href')])).toEqual([['Assets', '/manage/assets']]);
  });
});
