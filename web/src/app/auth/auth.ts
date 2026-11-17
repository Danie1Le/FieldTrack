import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';

/** Tokens as the API's sign-in, refresh and change-password endpoints return them (ADR 0002). */
interface TokenResponse {
  accessToken: string;
  accessTokenExpiresAt: string;
  refreshToken: string;
}

/** What a sign-in attempt came to; anything else is an unexpected error and throws. */
export type SignInOutcome = 'signedIn' | 'passwordChangeRequired' | 'refused' | 'tooManyAttempts';

/** Where this tab keeps its refresh token, so a reload stays signed in but a new tab signs in again. */
export const refreshTokenKey = 'fieldtrack.refreshToken';

/** How long before the access token expires the silent refresh runs, so requests and hub connects carry a live token. */
const refreshAhead = 60_000;

/**
 * Floor for the silent refresh delay. shortcut: expiry times come from the server's clock, so a browser clock far
 * ahead would see every token as expired; the floor turns that into a refresh every 30 s instead of a refresh storm.
 */
const minRefreshDelay = 30_000;

/**
 * The signed-in session. Spec #21: the access token lives in memory only; the refresh token lives in sessionStorage
 * and is spent shortly before the access token expires (silent refresh).
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly access = signal<{ token: string; expiresAt: number } | null>(null);
  private refreshing: Promise<boolean> | null = null;
  private silentRefresh?: ReturnType<typeof setTimeout>;

  /** True while this tab holds an access token. */
  readonly signedIn = computed(() => this.access() !== null);

  /** Signs in with email and password. */
  async signIn(email: string, password: string): Promise<SignInOutcome> {
    try {
      this.keep(await firstValueFrom(this.http.post<TokenResponse>('/api/auth/sign-in', { email, password })));
      return 'signedIn';
    } catch (error) {
      switch (error instanceof HttpErrorResponse ? error.status : undefined) {
        case 401:
          return 'refused';
        case 403:
          // #61: a temporary password (the seeded first Admin's) must be replaced before any tokens are issued.
          return 'passwordChangeRequired';
        case 429:
          return 'tooManyAttempts';
        default:
          throw error;
      }
    }
  }

  /** Replaces the password and signs in; this is how a first sign-in with a temporary password completes. Throws the API's error. */
  async changePassword(email: string, currentPassword: string, newPassword: string): Promise<void> {
    this.keep(
      await firstValueFrom(
        this.http.post<TokenResponse>('/api/auth/change-password', { email, currentPassword, newPassword }),
      ),
    );
  }

  /** The access token to send, refreshed first when there is none or it has expired; null once the session has ended. */
  async accessToken(): Promise<string | null> {
    const access = this.access();
    if (!access || access.expiresAt <= Date.now()) {
      await this.refresh();
    }

    return this.access()?.token ?? null;
  }

  /**
   * Spends the refresh token for new tokens. Resolves false when there is none or the API refused it (the session has
   * then ended); throws on other failures, keeping the session. One spend at a time: the API rotates refresh tokens,
   * so spending the same one twice would be refused as a replay (ADR 0002).
   */
  refresh(): Promise<boolean> {
    this.refreshing ??= this.spendRefreshToken().finally(() => (this.refreshing = null));
    return this.refreshing;
  }

  /** Forgets both tokens and, when a session was open, goes to the sign-in page. */
  signOut(): void {
    const wasSignedIn = this.signedIn();
    clearTimeout(this.silentRefresh);
    this.access.set(null);
    sessionStorage.removeItem(refreshTokenKey);
    if (wasSignedIn) {
      void this.router.navigateByUrl('/sign-in');
    }
  }

  private async spendRefreshToken(): Promise<boolean> {
    const refreshToken = sessionStorage.getItem(refreshTokenKey);
    if (!refreshToken) {
      return false;
    }

    try {
      this.keep(await firstValueFrom(this.http.post<TokenResponse>('/api/auth/refresh', { refreshToken })));
      return true;
    } catch (error) {
      if (error instanceof HttpErrorResponse && error.status === 401) {
        this.signOut();
        return false;
      }

      throw error;
    }
  }

  private keep(tokens: TokenResponse): void {
    const expiresAt = Date.parse(tokens.accessTokenExpiresAt);
    this.access.set({ token: tokens.accessToken, expiresAt });
    sessionStorage.setItem(refreshTokenKey, tokens.refreshToken);
    clearTimeout(this.silentRefresh);
    // A failed silent refresh is retried by the next accessToken() call once the token has expired.
    this.silentRefresh = setTimeout(
      () => this.refresh().catch(() => undefined),
      Math.max(minRefreshDelay, expiresAt - Date.now() - refreshAhead),
    );
  }
}
