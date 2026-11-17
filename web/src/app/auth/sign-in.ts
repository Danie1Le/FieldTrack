import { Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService, DemoOperator } from './auth';

const tooManyAttempts = 'Too many attempts. Wait a minute and try again.';

/**
 * The sign-in page. A temporary password (the seeded first Admin's, or one an Admin set when creating the user, #61)
 * gets a second step that replaces it, which is how a first sign-in completes. In demo mode it shows the Demo Operator's credentials (spec #21 story 78).
 */
@Component({
  selector: 'app-sign-in',
  templateUrl: './sign-in.html',
  styleUrl: './sign-in.css',
})
export class SignIn {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  /** The Demo Operator's credentials in demo mode, else null. Public by design: reset on every deploy (ADR 0002). */
  protected readonly demo = signal<DemoOperator | null>(null);
  protected readonly step = signal<'signIn' | 'changePassword'>('signIn');
  protected readonly busy = signal(false);
  /** Messages to show; they may come from the API, so the template renders them as text. */
  protected readonly errors = signal<string[]>([]);
  private email = '';
  private password = '';

  constructor() {
    void this.auth.demoOperator().then((demo) => this.demo.set(demo));
  }

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
          this.errors.set([tooManyAttempts]);
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
      const outcome = await this.auth.changePassword(this.email, this.password, newPassword);
      switch (outcome) {
        case 'changed':
          this.password = '';
          await this.router.navigateByUrl('/');
          break;
        case 'refused':
          // Locked meanwhile, or the temporary password was changed elsewhere: only a new sign-in can continue.
          this.email = '';
          this.password = '';
          this.step.set('signIn');
          this.errors.set(['The current password was refused. Sign in again.']);
          break;
        case 'tooManyAttempts':
          this.errors.set([tooManyAttempts]);
          break;
        default:
          this.errors.set(outcome.invalid.length ? outcome.invalid : ['The password could not be changed.']);
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
