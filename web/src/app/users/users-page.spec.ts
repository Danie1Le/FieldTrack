import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService } from '../auth/auth';
import { User, UsersPage } from './users-page';

const user = (id: string, email: string, displayName: string, role: User['role'], mustChangePassword = false): User => ({
  id,
  email,
  displayName,
  role,
  mustChangePassword,
});

const ada = user('ada', 'ada@example.com', 'Ada', 'Admin');
const otto = user('otto', 'otto@example.com', 'Otto', 'Operator', true);

describe('UsersPage', () => {
  let fixture: ComponentFixture<UsersPage>;
  let api: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: AuthService, useValue: {} }],
    });
    api = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(UsersPage);
    await serve([ada, otto]);
  });

  afterEach(() => {
    api.verify();
    vi.restoreAllMocks();
  });

  const page = () => fixture.nativeElement as HTMLElement;
  const text = (element: Element | null) => element?.textContent!.trim();
  const rows = () =>
    [...page().querySelectorAll('tbody tr')].map((row) => [...row.querySelectorAll('td')].slice(0, 4).map((cell) => text(cell)));
  const revoke = (id: string) => page().querySelector<HTMLButtonElement>(`[data-user="${id}"] button`)!.click();
  const status = () => text(page().querySelector('[role="status"]'));

  /** Answers the page's read of the users, then lets the answer render. */
  async function serve(users: User[]) {
    await vi.waitFor(() => api.expectOne({ method: 'GET', url: '/api/users' }).flush(users));
    await settle();
  }

  async function settle() {
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();
  }

  function type(name: string, value: string) {
    const field = page().querySelector<HTMLInputElement>(`input[name="${name}"]`)!;
    field.value = value;
    field.dispatchEvent(new Event('input'));
  }

  async function create() {
    type('email', 'new@example.com');
    type('displayName', 'Nia');
    page().querySelector<HTMLInputElement>('input[value="Admin"]')!.click();
    type('temporaryPassword', 'Temporary-pass-1');
    page().querySelector('form')!.dispatchEvent(new Event('submit'));
  }

  it('lists every user with their role and whether their password is still temporary', () => {
    expect(rows()).toEqual([
      ['Ada', 'ada@example.com', 'Admin', ''],
      ['Otto', 'otto@example.com', 'Operator', 'Temporary password'],
    ]);
  });

  it('creates a user with a role and a temporary password', async () => {
    await create();

    const request = api.expectOne({ method: 'POST', url: '/api/users' });
    expect(request.request.body).toEqual({
      email: 'new@example.com',
      displayName: 'Nia',
      role: 'Admin',
      temporaryPassword: 'Temporary-pass-1',
    });
    request.flush(user('nia', 'new@example.com', 'Nia', 'Admin', true), { status: 201, statusText: 'Created' });
    await serve([ada, user('nia', 'new@example.com', 'Nia', 'Admin', true), otto]);

    expect(rows()).toContainEqual(['Nia', 'new@example.com', 'Admin', 'Temporary password']);
    expect(status()).toContain('new@example.com');
    expect(page().querySelector<HTMLInputElement>('input[name="temporaryPassword"]')!.value).toBe('');
  });

  it("shows the API's reasons for refusing a user, as text, and keeps the form", async () => {
    await create();

    const reason = '<b>Username is already taken.</b>';
    api.expectOne({ method: 'POST', url: '/api/users' }).flush({ errors: { DuplicateUserName: [reason] } }, { status: 400, statusText: 'Bad Request' });
    await settle();

    const shown = page().querySelector('[role="alert"]')!;
    expect(text(shown)).toBe(reason);
    expect(shown.querySelector('b')).toBeNull();
    expect(page().querySelector<HTMLInputElement>('input[name="email"]')!.value).toBe('new@example.com');
  });

  it("revokes a user's refresh tokens once confirmed and says access ends within 15 minutes", async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);

    revoke('otto');
    api.expectOne({ method: 'DELETE', url: '/api/users/otto/refresh-tokens' }).flush(null, { status: 204, statusText: 'No Content' });
    await settle();

    expect(status()).toContain('Otto');
    expect(status()).toContain('15 minutes');
  });

  it('revokes nothing unless confirmed', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(false);

    revoke('otto');
    await settle();

    api.expectNone('/api/users/otto/refresh-tokens');
  });
});
