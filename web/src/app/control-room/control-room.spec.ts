import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService } from '../auth/auth';
import { ControlRoom } from './control-room';
import { Alert } from './live';
import { LiveFleet } from './live-fleet';

const alert = (id: string, status: Alert['status']): Alert => ({
  id,
  alertRuleId: 'rule-1',
  assetId: 'truck-1',
  severity: 'Warning',
  message: 'Fuel 9.5 %, below 10',
  status,
  raisedAt: '2026-11-17T10:00:00Z',
  resolvedAt: null,
  resolveReason: status === 'Resolved' ? 'Cleared' : null,
  acknowledgedBy: status === 'Acknowledged' ? 'Sam' : null,
  acknowledgedAt: null,
  resolvedBy: null,
  note: null,
  latched: false,
  fromBacklog: false,
});

describe('ControlRoom', () => {
  const alerts = signal<Alert[]>([]);
  const admin = signal(false);

  beforeEach(() => {
    admin.set(false);
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
    const fleet = { start: () => {}, assets: signal([]), groups: signal([]), rules: signal([]), geofences: signal([]), alerts, connection: signal('live') };
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: { isAdmin: admin } }] });
    TestBed.overrideComponent(ControlRoom, { set: { providers: [{ provide: LiveFleet, useValue: fleet }] } });
  });

  afterEach(() => vi.unstubAllGlobals());

  it('counts the Active Alerts on the Alerts tab', async () => {
    alerts.set([alert('a', 'Open'), alert('b', 'Acknowledged'), alert('c', 'Resolved')]);
    const fixture = TestBed.createComponent(ControlRoom);
    const tab = () => [...(fixture.nativeElement as HTMLElement).querySelectorAll('nav button')].at(-1)!.textContent!.trim();

    await fixture.whenStable();
    expect(tab()).toBe('Alerts 2');

    alerts.update((list) => [...list, alert('d', 'Open')]);
    await fixture.whenStable();
    expect(tab()).toBe('Alerts 3');
  });

  it('offers the Geofences page to Admins only', async () => {
    const fixture = TestBed.createComponent(ControlRoom);
    const link = () => (fixture.nativeElement as HTMLElement).querySelector('a[href="/geofences"]');

    await fixture.whenStable();
    expect(link()).toBeNull();

    admin.set(true);
    await fixture.whenStable();
    expect(link()!.textContent!.trim()).toBe('Geofences');
  });

  it('offers the users page to Admins only', async () => {
    const fixture = TestBed.createComponent(ControlRoom);
    const link = () => (fixture.nativeElement as HTMLElement).querySelector('a[href="/users"]');

    await fixture.whenStable();
    expect(link()).toBeNull();

    admin.set(true);
    await fixture.whenStable();
    expect(link()!.textContent!.trim()).toBe('Users');
  });
});
