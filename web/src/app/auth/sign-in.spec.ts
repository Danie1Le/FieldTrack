import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { SignIn } from './sign-in';

describe('SignIn', () => {
  let api: HttpTestingController;
  let page: HTMLElement;
  let fixture: ReturnType<typeof TestBed.createComponent<SignIn>>;

  beforeEach(async () => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: '', children: [] }]), provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(SignIn);
    page = fixture.nativeElement;
    await fixture.whenStable();
  });

  async function submit(fields: Record<string, string>) {
    for (const [id, value] of Object.entries(fields)) {
      page.querySelector<HTMLInputElement>(`#${id}`)!.value = value;
    }
    page.querySelector('form')!.dispatchEvent(new Event('submit'));
  }

  it('replaces a temporary password, then opens the Control room', async () => {
    await submit({ email: 'admin@example.com', password: 'Temporary1!' });
    api.expectOne('/api/auth/sign-in').flush({ title: 'Password change required' }, { status: 403, statusText: 'Forbidden' });
    await fixture.whenStable();

    await submit({ 'new-password': 'Chosen-Password-1', confirmation: 'Chosen-Password-1' });
    const change = api.expectOne('/api/auth/change-password');
    expect(change.request.body).toEqual({
      email: 'admin@example.com',
      currentPassword: 'Temporary1!',
      newPassword: 'Chosen-Password-1',
    });
    change.flush({ accessToken: 'a', accessTokenExpiresAt: new Date(Date.now() + 900_000).toISOString(), refreshToken: 'r' });

    await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/'));
  });

  it("shows the API's validation messages as text", async () => {
    await submit({ email: 'admin@example.com', password: 'Temporary1!' });
    api.expectOne('/api/auth/sign-in').flush(null, { status: 403, statusText: 'Forbidden' });
    await fixture.whenStable();

    await submit({ 'new-password': 'short', confirmation: 'short' });
    api.expectOne('/api/auth/change-password').flush(
      { errors: { NewPassword: ['<img src=x onerror=alert(1)> too short'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    await fixture.whenStable();

    const error = page.querySelector('[role=alert] li')!;
    expect(error.textContent).toBe('<img src=x onerror=alert(1)> too short');
    expect(error.querySelector('img')).toBeNull();
  });

  async function refusePasswordChange(body: object | null, status: number, statusText: string) {
    await submit({ email: 'admin@example.com', password: 'Temporary1!' });
    api.expectOne('/api/auth/sign-in').flush(null, { status: 403, statusText: 'Forbidden' });
    await fixture.whenStable();

    await submit({ 'new-password': 'Chosen-Password-1', confirmation: 'Chosen-Password-1' });
    api.expectOne('/api/auth/change-password').flush(body, { status, statusText });
    await fixture.whenStable();
  }

  it('says the password could not be changed when a 400 carries no validation messages', async () => {
    await refusePasswordChange({ title: 'Bad Request' }, 400, 'Bad Request');

    expect(page.querySelector('[role=alert]')!.textContent).toContain('The password could not be changed.');
  });

  it('goes back to sign-in when the current password is refused', async () => {
    await refusePasswordChange(null, 401, 'Unauthorized');

    expect(page.querySelector('#email')).not.toBeNull();
    expect(page.querySelector('#new-password')).toBeNull();
    expect(page.querySelector('[role=alert]')!.textContent).toContain('Sign in again.');
  });

  it("shows the Demo Operator's credentials in demo mode and signs in with them", async () => {
    api.expectOne('/api/auth/demo-operator').flush({ email: 'demo@example.com', password: '<b>Demo-pass-1</b>' });
    await new Promise((resolve) => setTimeout(resolve)); // lets AuthService.demoOperator() resolve
    await fixture.whenStable();

    const demo = page.querySelector('.demo')!;
    expect(demo.textContent).toContain('demo@example.com');
    expect(demo.textContent).toContain('<b>Demo-pass-1</b>');
    expect(demo.querySelector('b')).toBeNull();

    demo.querySelector('button')!.click();
    const signIn = api.expectOne('/api/auth/sign-in');
    expect(signIn.request.body).toEqual({ email: 'demo@example.com', password: '<b>Demo-pass-1</b>' });
    signIn.flush({ accessToken: 'a', accessTokenExpiresAt: new Date(Date.now() + 900_000).toISOString(), refreshToken: 'r' });

    await vi.waitFor(() => expect(TestBed.inject(Router).url).toBe('/'));
  });

  it('offers no Demo Operator outside demo mode', async () => {
    api.expectOne('/api/auth/demo-operator').flush(null, { status: 404, statusText: 'Not Found' });
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();

    expect(page.querySelector('.demo')).toBeNull();
  });
});
