import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AlertFeed } from './alert-feed';
import { Alert, AlertRule, LiveAsset } from './live';
import { LiveFleet } from './live-fleet';

const alert = (id: string, raisedAt: string, changes: Partial<Alert> = {}): Alert => ({
  id,
  alertRuleId: 'rule-1',
  assetId: 'truck-1',
  severity: 'Critical',
  message: 'Engine temp 108.4 °C, above 105',
  status: 'Open',
  raisedAt,
  resolvedAt: null,
  resolveReason: null,
  acknowledgedBy: null,
  acknowledgedAt: null,
  resolvedBy: null,
  note: null,
  latched: false,
  fromBacklog: false,
  ...changes,
});

const resolved = (id: string, changes: Partial<Alert>): Alert =>
  alert(id, '2026-11-17T09:00:00Z', { status: 'Resolved', resolvedAt: '2026-11-17T09:10:00Z', resolveReason: 'Cleared', ...changes });

describe('AlertFeed', () => {
  let fleet: {
    alerts: ReturnType<typeof signal<Alert[]>>;
    assets: ReturnType<typeof signal<LiveAsset[]>>;
    rules: ReturnType<typeof signal<AlertRule[]>>;
    connection: ReturnType<typeof signal<string>>;
    acknowledge: ReturnType<typeof vi.fn>;
    resolve: ReturnType<typeof vi.fn>;
  };
  let page: HTMLElement;
  let fixture: ReturnType<typeof TestBed.createComponent<AlertFeed>>;

  beforeEach(async () => {
    fleet = {
      alerts: signal<Alert[]>([]),
      assets: signal<LiveAsset[]>([
        { id: 'truck-1', name: 'T-101', type: 'Truck', assetGroupId: null, status: 'Alerting', lastReceivedAt: null, latest: null },
      ]),
      rules: signal<AlertRule[]>([
        {
          id: 'rule-1', name: 'Engine overheat', kind: 'Threshold', assetId: 'truck-1', assetGroupId: null, severity: 'Critical',
          enabled: true, sensor: 'EngineTempC', operator: 'GreaterThan', threshold: 105, hysteresis: 5,
        },
      ]),
      connection: signal('live'),
      acknowledge: vi.fn(async () => {}),
      resolve: vi.fn(async () => {}),
    };
    TestBed.configureTestingModule({ providers: [{ provide: LiveFleet, useValue: fleet }] });
    fixture = TestBed.createComponent(AlertFeed);
    page = fixture.nativeElement;
    await fixture.whenStable();
  });

  async function show(alerts: Alert[]) {
    fleet.alerts.set(alerts);
    await fixture.whenStable();
  }

  const items = () => [...page.querySelectorAll<HTMLElement>('li.alert')];
  const button = (item: HTMLElement, label: string) =>
    [...item.querySelectorAll('button')].find((b) => b.textContent!.trim() === label);

  async function click(element: HTMLElement | undefined) {
    element!.click();
    await fixture.whenStable();
  }

  it('lists Active Alerts newest first and offers Acknowledge only on Open ones', async () => {
    await show([
      alert('older', '2026-11-17T10:00:00Z'),
      resolved('done', {}),
      alert('newest', '2026-11-17T10:20:00Z', { status: 'Acknowledged', acknowledgedBy: 'Sam' }),
      alert('newer', '2026-11-17T10:10:00Z'),
    ]);

    expect(items().map((i) => i.dataset['alert'])).toEqual(['newest', 'newer', 'older']);
    expect(items().map((i) => button(i, 'Acknowledge') !== undefined)).toEqual([false, true, true]);
    expect(items()[0].textContent).toContain('Acknowledged by Sam');
    expect(items()[0].textContent).toContain('T-101');
    expect(items()[0].textContent).toContain('Engine overheat');

    await click(button(items()[1], 'Acknowledge'));
    expect(fleet.acknowledge).toHaveBeenCalledWith('newer');
  });

  it('Resolves with a note after warning up front that the condition still holds', async () => {
    await show([alert('hot', '2026-11-17T10:00:00Z', { latched: true })]);

    await click(button(items()[0], 'Resolve'));
    expect(items()[0].querySelector('form [role=note]')!.textContent!.trim()).toBe('Still breaching: no new Alert until it clears.');
    const note = items()[0].querySelector('textarea')!;
    note.value = 'Crew on the way';
    note.dispatchEvent(new Event('input'));
    await click(button(items()[0], 'Confirm'));

    expect(fleet.resolve).toHaveBeenCalledWith('hot', 'Crew on the way');
    expect(items()[0].querySelector('form')).toBeNull();
  });

  it('gives no warning when the condition has cleared, and Cancel closes the form without resolving', async () => {
    await show([alert('cooling', '2026-11-17T10:00:00Z', { latched: false })]);

    await click(button(items()[0], 'Resolve'));
    expect(items()[0].querySelector('[role=note]')).toBeNull();
    await click(button(items()[0], 'Cancel'));

    expect(items()[0].querySelector('form')).toBeNull();
    expect(fleet.resolve).not.toHaveBeenCalled();
  });

  it('keeps the form open and says why when the Resolve is refused', async () => {
    fleet.resolve.mockRejectedValue(new HttpErrorResponse({ status: 409, statusText: 'Conflict' }));
    await show([alert('raced', '2026-11-17T10:00:00Z')]);

    await click(button(items()[0], 'Resolve'));
    await click(button(items()[0], 'Confirm'));

    expect(items()[0].querySelector('form')).not.toBeNull();
    expect(items()[0].querySelector('[role=alert]')!.textContent).toContain('already Resolved');
  });

  it('lists Resolved Alerts on History with their reason, the From backlog tag and the latch note', async () => {
    await show([
      alert('active', '2026-11-17T10:00:00Z'),
      resolved('backlog', { raisedAt: '2026-11-17T09:00:00Z', fromBacklog: true }),
      resolved('by-hand', {
        raisedAt: '2026-11-17T09:30:00Z', resolveReason: 'Manual', resolvedBy: 'Sam', note: 'Refuel booked', latched: true,
      }),
      resolved('rule', { raisedAt: '2026-11-17T08:00:00Z', resolveReason: 'RuleChanged' }),
    ]);

    await click([...page.querySelectorAll('button')].find((b) => b.textContent!.includes('History')));

    expect(items().map((i) => i.dataset['alert'])).toEqual(['by-hand', 'backlog', 'rule']);
    const [byHand, backlog, rule] = items();
    expect(byHand.textContent).toContain('Manual');
    expect(byHand.textContent).toContain('Sam');
    expect(byHand.textContent).toContain('Refuel booked');
    expect(byHand.querySelector('.latch')).not.toBeNull();
    expect(byHand.querySelector('.tag')).toBeNull();
    expect(backlog.textContent).toContain('Cleared');
    expect(backlog.querySelector('.tag')!.textContent).toBe('From backlog');
    expect(backlog.querySelector('.latch')).toBeNull();
    expect(rule.textContent).toContain('Rule changed');
    expect(rule.querySelector('button')).toBeNull();
  });

  it('shows server text as text', async () => {
    await show([
      resolved('x', {
        message: '<img src=x onerror=alert(1)>', resolveReason: 'Manual', resolvedBy: '<b>Eve</b>', note: '<script>1</script>',
      }),
    ]);
    await click([...page.querySelectorAll('button')].find((b) => b.textContent!.includes('History')));

    expect(items()[0].textContent).toContain('<img src=x onerror=alert(1)>');
    expect(items()[0].querySelector('img, b, script')).toBeNull();
  });
});
