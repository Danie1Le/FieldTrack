import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService, refreshTokenKey } from './auth';
import { authInterceptor } from './auth.interceptor';

const minute = 60_000;

describe('AuthService', () => {
  let auth: AuthService;
  let api: HttpTestingController;

  beforeEach(() => {
    vi.useFakeTimers({ now: new Date('2026-11-17T10:00:00Z') });
    sessionStorage.clear();
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'sign-in', children: [] }]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    auth = TestBed.inject(AuthService);
    api = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    api.verify();
    vi.useRealTimers();
  });

  /** The API's token response for an access token that lives 15 minutes from now. */
  const tokens = (n: number) => ({
    accessToken: `access-${n}`,
    accessTokenExpiresAt: new Date(Date.now() + 15 * minute).toISOString(),
    refreshToken: `refresh-${n}`,
  });

  async function signIn() {
    const outcome = auth.signIn('ops@example.com', 'pw');
    api.expectOne('/api/auth/sign-in').flush(tokens(1));
    expect(await outcome).toBe('signedIn');
  }

  it('keeps the access token in memory and only the refresh token in sessionStorage', async () => {
    await signIn();

    expect(await auth.accessToken()).toBe('access-1');
    expect(sessionStorage.getItem(refreshTokenKey)).toBe('refresh-1');
    const stored = [...Object.values(sessionStorage), ...Object.values(localStorage)];
    expect(stored).not.toContain('access-1');
  });

  it('asks for a new password when the API says the password is temporary', async () => {
    const outcome = auth.signIn('admin@example.com', 'temporary');
    api.expectOne('/api/auth/sign-in').flush({ title: 'Password change required' }, { status: 403, statusText: 'Forbidden' });

    expect(await outcome).toBe('passwordChangeRequired');
    expect(auth.signedIn()).toBe(false);
  });

  it('refreshes silently a minute before the access token expires', async () => {
    await signIn();

    vi.advanceTimersByTime(14 * minute);
    const refresh = api.expectOne('/api/auth/refresh');
    expect(refresh.request.body).toEqual({ refreshToken: 'refresh-1' });
    refresh.flush(tokens(2));
    await vi.waitFor(() => expect(sessionStorage.getItem(refreshTokenKey)).toBe('refresh-2'));

    expect(await auth.accessToken()).toBe('access-2');
  });

  it('restores the session after a reload from the refresh token', async () => {
    sessionStorage.setItem(refreshTokenKey, 'refresh-1');

    const token = auth.accessToken();
    api.expectOne('/api/auth/refresh').flush(tokens(2));

    expect(await token).toBe('access-2');
    expect(auth.signedIn()).toBe(true);
  });

  it('ends the session and goes to sign-in when the API refuses the refresh token', async () => {
    await signIn();

    vi.advanceTimersByTime(14 * minute);
    api.expectOne('/api/auth/refresh').flush(null, { status: 401, statusText: 'Unauthorized' });
    await vi.waitFor(() => expect(auth.signedIn()).toBe(false));

    expect(sessionStorage.getItem(refreshTokenKey)).toBeNull();
    await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/sign-in'));
  });

  it('sends the access token and retries once with a refreshed one after a 401', async () => {
    await signIn();
    const http = TestBed.inject(HttpClient);

    const response = firstValueFrom(http.get('/api/assets/live'));
    await vi.waitFor(() => api.expectOne('/api/assets/live'), { interval: 1 }).then((first) => {
      expect(first.request.headers.get('Authorization')).toBe('Bearer access-1');
      first.flush(null, { status: 401, statusText: 'Unauthorized' });
    });
    await vi.waitFor(() => api.expectOne('/api/auth/refresh'), { interval: 1 }).then((refresh) => refresh.flush(tokens(2)));
    const retry = await vi.waitFor(() => api.expectOne('/api/assets/live'), { interval: 1 });
    expect(retry.request.headers.get('Authorization')).toBe('Bearer access-2');
    retry.flush([]);

    expect(await response).toEqual([]);
  });

  it('spends the refresh token once when two calls get a 401 together', async () => {
    await signIn();
    const http = TestBed.inject(HttpClient);

    const assets = firstValueFrom(http.get('/api/assets'));
    const rules = firstValueFrom(http.get('/api/alert-rules'));
    for (const url of ['/api/assets', '/api/alert-rules']) {
      const first = await vi.waitFor(() => api.expectOne(url), { interval: 1 });
      first.flush(null, { status: 401, statusText: 'Unauthorized' });
    }
    // expectOne fails on a second spend, which the API would treat as a replay (ADR 0002).
    await vi.waitFor(() => api.expectOne('/api/auth/refresh'), { interval: 1 }).then((refresh) => refresh.flush(tokens(2)));
    for (const url of ['/api/assets', '/api/alert-rules']) {
      const retry = await vi.waitFor(() => api.expectOne(url), { interval: 1 });
      expect(retry.request.headers.get('Authorization')).toBe('Bearer access-2');
      retry.flush([]);
    }

    expect(await assets).toEqual([]);
    expect(await rules).toEqual([]);
  });

  it('retries only once: a 401 on the retry is the error, with no second refresh', async () => {
    await signIn();
    const http = TestBed.inject(HttpClient);

    const response = firstValueFrom(http.get('/api/assets/live'));
    await vi.waitFor(() => api.expectOne('/api/assets/live'), { interval: 1 }).then((first) =>
      first.flush(null, { status: 401, statusText: 'Unauthorized' }),
    );
    await vi.waitFor(() => api.expectOne('/api/auth/refresh'), { interval: 1 }).then((refresh) => refresh.flush(tokens(2)));
    await vi.waitFor(() => api.expectOne('/api/assets/live'), { interval: 1 }).then((retry) =>
      retry.flush(null, { status: 401, statusText: 'Unauthorized' }),
    );

    await expect(response).rejects.toMatchObject({ status: 401 });
  });

  it('does not bring the session back when a refresh in flight answers after sign out', async () => {
    await signIn();

    const refreshed = auth.refresh();
    const request = api.expectOne('/api/auth/refresh');
    auth.signOut();
    request.flush(tokens(2));

    expect(await refreshed).toBe(false);
    expect(auth.signedIn()).toBe(false);
    expect(sessionStorage.getItem(refreshTokenKey)).toBeNull();
    // No silent refresh was re-armed: api.verify() fails on any request this would send.
    vi.advanceTimersByTime(30 * minute);
  });
});
