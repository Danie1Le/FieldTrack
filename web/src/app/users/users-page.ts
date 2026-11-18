import { HttpClient } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import { AuthService } from '../auth/auth';
import { ConfirmAction } from '../manage/confirm-action';
import { problemsOf } from '../problems';

/** A user as `GET /users` returns them (Admins only: the list exposes accounts, ADR 0002). */
export interface User {
  id: string;
  email: string;
  displayName: string;
  role: 'Admin' | 'Operator';
  mustChangePassword: boolean;
}

// ADR 0002: revoking leaves access tokens already issued valid until they expire.
const revokeEffect = 'They are signed out when their access token expires, within 15 minutes.';

/**
 * The users page (Admins only, spec #21 stories 73 and 76): create an Admin or Operator with a temporary password they
 * replace at first sign-in, and revoke a user's refresh tokens.
 */
@Component({
  selector: 'app-users-page',
  imports: [ConfirmAction, RouterLink],
  templateUrl: './users-page.html',
  styleUrl: './users-page.css',
})
export class UsersPage {
  private readonly http = inject(HttpClient);
  protected readonly auth = inject(AuthService);

  protected readonly users = signal<User[] | null>(null);
  protected readonly busy = signal(false);
  protected readonly problems = signal<string[]>([]);
  /** What the last change did, for people. */
  protected readonly done = signal('');

  constructor() {
    void this.load();
  }

  protected async create(form: HTMLFormElement): Promise<void> {
    const fields = new FormData(form);
    const body = Object.fromEntries(['email', 'displayName', 'role', 'temporaryPassword'].map((name) => [name, fields.get(name)]));
    if (await this.change(this.http.post('/api/users', body), `Created ${body['email']}. They choose their own password at first sign-in.`)) {
      form.reset();
      await this.load();
    }
  }

  protected revokeQuestion(user: User): string {
    return `Revoke the sessions of ${user.displayName} (${user.email})? ${revokeEffect}`;
  }

  protected async revoke(user: User): Promise<void> {
    await this.change(this.http.delete(`/api/users/${encodeURIComponent(user.id)}/refresh-tokens`), `Revoked the sessions of ${user.displayName}. ${revokeEffect}`);
  }

  /** Sends a change and says what it did, else why it was refused; resolves whether it went through. */
  private async change(request: Observable<unknown>, done: string): Promise<boolean> {
    this.busy.set(true);
    this.problems.set([]);
    this.done.set('');
    try {
      await firstValueFrom(request);
      this.done.set(done);
      return true;
    } catch (error) {
      this.problems.set(problemsOf(error));
      return false;
    } finally {
      this.busy.set(false);
    }
  }

  private async load(): Promise<void> {
    try {
      this.users.set(await firstValueFrom(this.http.get<User[]>('/api/users')));
    } catch {
      this.problems.set(['The users could not be loaded. Reload the page to try again.']);
    }
  }
}
