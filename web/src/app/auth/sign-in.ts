import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from './auth';

/**
 * The sign-in page. A temporary password (the seeded first Admin's, #61) gets a second step that replaces it, which
 * is how that first sign-in completes.
 */
@Component({
  selector: 'app-sign-in',
  templateUrl: './sign-in.html',
  styleUrl: './sign-in.css',
})
export class SignIn {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly step = signal<'signIn' | 'changePassword'>('signIn');
  protected readonly busy = signal(false);
  /** Messages to show; they may come from the API, so the template renders them as text. */
  protected readonly errors = signal<string[]>([]);
  private email = '';
  private password = '';

  protected async signIn(email: string, password: string): Promise<void> {
    await this.attempt(async () => {
      const outcome = await this.auth.signIn(email, password);
      switch (outcome) {
        case 'signedIn':
          await this.router.navigateByUrl('/');
          break;
        case 'passwordChangeRequired':
          this.email = email;
          this.password = password;
          this.step.set('changePassword');
          break;
        case 'refused':
          this.errors.set(['Wrong email or password, or the account is locked for a while after repeated failures.']);
          break;
        case 'tooManyAttempts':
          this.errors.set(['Too many attempts. Wait a minute and try again.']);
          break;
      }
    });
  }

  protected async changePassword(newPassword: string, confirmation: string): Promise<void> {
    if (newPassword !== confirmation) {
      this.errors.set(['The two passwords differ.']);
      return;
    }

    await this.attempt(async () => {
      try {
        await this.auth.changePassword(this.email, this.password, newPassword);
        this.password = '';
        await this.router.navigateByUrl('/');
      } catch (error) {
        if (!(error instanceof HttpErrorResponse)) {
          throw error;
        }

        this.errors.set(changePasswordErrors(error));
      }
    });
  }

  private async attempt(action: () => Promise<void>): Promise<void> {
    this.errors.set([]);
    this.busy.set(true);
    try {
      await action();
    } catch {
      this.errors.set(['Something went wrong. Try again.']);
    } finally {
      this.busy.set(false);
    }
  }
}

/** What to tell the user about a refused password change: the API's validation messages, or what the status means. */
function changePasswordErrors(error: HttpErrorResponse): string[] {
  switch (error.status) {
    case 400:
      // A validation problem: { errors: { NewPassword: ["Passwords must have at least one digit ('0'-'9')."] } }.
      return Object.values<string[]>(error.error?.errors ?? {}).flat();
    case 401:
      return ['The current password was refused. Sign in again.'];
    case 429:
      return ['Too many attempts. Wait a minute and try again.'];
    default:
      return [error.error?.title ?? 'The password could not be changed.'];
  }
}
