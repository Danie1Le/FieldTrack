import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
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
  it('counts the Active Alerts on the Alerts tab', async () => {
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
    const alerts = signal([alert('a', 'Open'), alert('b', 'Acknowledged'), alert('c', 'Resolved')]);
    const fleet = { start: () => {}, assets: signal([]), groups: signal([]), rules: signal([]), alerts, connection: signal('live') };
    TestBed.configureTestingModule({ providers: [{ provide: AuthService, useValue: {} }] });
    TestBed.overrideComponent(ControlRoom, { set: { providers: [{ provide: LiveFleet, useValue: fleet }] } });
    const fixture = TestBed.createComponent(ControlRoom);
    const tab = () => [...(fixture.nativeElement as HTMLElement).querySelectorAll('nav button')].at(-1)!.textContent!.trim();

    await fixture.whenStable();
    expect(tab()).toBe('Alerts 2');

    alerts.update((list) => [...list, alert('d', 'Open')]);
    await fixture.whenStable();
    expect(tab()).toBe('Alerts 3');
  });
});
