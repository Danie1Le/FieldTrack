import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, input } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { AlertRule, Reading } from '../control-room/live';
import { AssetDetail } from './asset-detail';
import { ReplayMap } from './replay-map';
import { RuleLine } from './replay';
import { SensorChart } from './sensor-chart';

// Leaflet and Chart.js need a real layout and canvas; the page is tested through what it hands them.
@Component({ selector: 'app-replay-map', template: '' })
class FakeMap {
  readonly readings = input.required<Reading[]>();
  readonly position = input.required<number>();
}

@Component({ selector: 'app-sensor-chart', template: '' })
class FakeChart {
  readonly readings = input.required<Reading[]>();
  readonly sensor = input.required<string>();
  readonly lines = input<RuleLine[]>([]);
  readonly cursor = input<number | null>(null);
}

const now = Date.parse('2026-11-17T12:00:00Z');
const reading = (deviceTime: string, engineTempC: number): Reading => ({
  deviceSerialNumber: 'TRK-007',
  boot: 1,
  sequenceNumber: 1,
  deviceTime,
  receivedTime: deviceTime,
  latitude: 52.2,
  longitude: -1.1,
  speedKph: null,
  engineTempC,
  fuelPct: 50,
  vibrationMmS: null,
  batteryV: null,
});
const readings = [
  reading('2026-11-17T11:00:00Z', 90),
  reading('2026-11-17T11:01:00Z', 95),
  reading('2026-11-17T11:02:00Z', 108),
];
const overheat: AlertRule = {
  id: 'rule-1',
  name: 'Engine overheat',
  kind: 'Threshold',
  assetId: 'truck-7',
  assetGroupId: null,
  severity: 'Critical',
  enabled: true,
  sensor: 'EngineTempC',
  operator: 'GreaterThan',
  threshold: 105,
  hysteresis: 5,
};
const alert = {
  id: 'alert-1',
  alertRuleId: 'rule-1',
  assetId: 'truck-7',
  severity: 'Critical',
  message: 'Engine temp 108 °C, above 105 <b>(clears below 100)</b>',
  status: 'Open',
  raisedAt: '2026-11-17T11:02:00Z',
  resolvedAt: null,
  resolveReason: null,
};

describe('AssetDetail', () => {
  let api: HttpTestingController;
  let page: HTMLElement;
  let fixture: ReturnType<typeof TestBed.createComponent<AssetDetail>>;

  beforeEach(async () => {
    vi.useFakeTimers({ now, toFake: ['Date', 'setInterval', 'clearInterval'] });
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    TestBed.overrideComponent(AssetDetail, {
      remove: { imports: [ReplayMap, SensorChart] },
      add: { imports: [FakeMap, FakeChart] },
    });
    api = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AssetDetail);
    fixture.componentRef.setInput('id', 'truck-7');
    page = fixture.nativeElement;
    await fixture.whenStable();
  });

  afterEach(() => vi.useRealTimers());

  async function load(history: Reading[] = readings) {
    api.expectOne('/api/assets/truck-7').flush({ id: 'truck-7', name: 'Haul truck 7', type: 'Truck', assetGroupId: null, archived: false });
    api.expectOne('/api/alert-rules').flush([overheat]);
    api.expectOne('/api/alerts?assetId=truck-7').flush([alert]);
    api.expectOne((r) => r.url === '/api/assets/truck-7/readings').flush(history);
    await settled();
  }

  /** Lets the page's awaited reads finish, then renders. */
  async function settled() {
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();
  }

  const map = () => fixture.debugElement.query(By.directive(FakeMap)).componentInstance as FakeMap;
  const charts = () => fixture.debugElement.queryAll(By.directive(FakeChart)).map((c) => c.componentInstance as FakeChart);
  const button = (name: string) => [...page.querySelectorAll('button')].find((b) => b.textContent!.trim() === name)!;
  const slider = () => page.querySelector<HTMLInputElement>('input[type=range]')!;

  it('reads the last 24 h of Readings by Device time and lists the Asset and its Alerts as text', async () => {
    await load();

    api.verify();
    expect(page.querySelector('h1')!.textContent).toContain('Haul truck 7');
    expect(page.textContent).toContain('Engine temp 108 °C, above 105 <b>(clears below 100)</b>');
    expect(page.querySelector('b')).toBeNull();
  });

  it('asks the history endpoint for [now - 24 h, now) by Device time, thinned to what the map draws', async () => {
    const request = api.expectOne((r) => r.url === '/api/assets/truck-7/readings');

    expect(request.request.params.get('from')).toBe('2026-11-16T12:00:00.000Z');
    expect(request.request.params.get('to')).toBe('2026-11-17T12:00:00.000Z');
    expect(request.request.params.get('thinTo')).toBe('3000');
  });

  it('draws the threshold and clear lines on the engine temp chart only', async () => {
    await load();

    expect(charts().map((c) => [c.sensor(), c.lines()])).toEqual([
      ['EngineTempC', [{ name: 'Engine overheat', operator: 'GreaterThan', threshold: 105, clear: 100 }]],
      ['FuelPct', []],
    ]);
  });

  it('plays from the start at the chosen speed, pauses, and scrubs, with the charts cursor following', async () => {
    await load();
    expect(map().position()).toBe(2);

    button('Play').click();
    await fixture.whenStable();
    expect(map().position()).toBe(0);
    vi.advanceTimersByTime(300); // 300 x speed: 90 s of Device time
    await fixture.whenStable();
    expect(map().position()).toBe(1);
    expect(charts()[0].cursor()).toBe(Date.parse('2026-11-17T11:01:30Z'));

    button('Pause').click();
    vi.advanceTimersByTime(1_000);
    await fixture.whenStable();
    expect(map().position()).toBe(1);

    slider().value = String(Date.parse('2026-11-17T11:02:00Z'));
    slider().dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(map().position()).toBe(2);
    expect(page.textContent).toContain('108.0 °C');
  });

  it('stops at the end of the range', async () => {
    await load();
    button('Play').click();

    vi.advanceTimersByTime(10_000);
    await fixture.whenStable();

    expect(map().position()).toBe(2);
    expect(button('Play')).toBeTruthy();
  });

  it('reads another range when one is chosen, and shows a refusal as text', async () => {
    await load();
    page.querySelector<HTMLInputElement>('#from')!.value = '2026-10-01T00:00';
    page.querySelector('form')!.dispatchEvent(new Event('submit'));

    const request = api.expectOne((r) => r.url === '/api/assets/truck-7/readings');
    expect(Date.parse(request.request.params.get('from')!)).toBe(new Date(2026, 9, 1).getTime());
    request.flush(
      { title: 'One or more validation errors occurred.', errors: { to: ['The range must span at most <i>31</i> days.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    await fixture.whenStable();

    expect(page.querySelector('[role=alert]')!.textContent).toContain('The range must span at most <i>31</i> days.');
  });

  it('clears the previous Asset and its Alerts when the page is reused for another id', async () => {
    await load();
    expect(page.querySelector('h1')!.textContent).toContain('Haul truck 7');

    fixture.componentRef.setInput('id', 'truck-8');
    await settled();

    expect(page.querySelector('h1')!.textContent).toBe('Asset');
    expect(page.textContent).not.toContain('Engine temp 108');
  });

  it('drops the answer for a previous id that arrives after the answer for the new id', async () => {
    const stale = [
      api.expectOne('/api/assets/truck-7'),
      api.expectOne('/api/alerts?assetId=truck-7'),
    ];
    api.expectOne('/api/alert-rules').flush([]);
    api.expectOne((r) => r.url === '/api/assets/truck-7/readings').flush([]);
    fixture.componentRef.setInput('id', 'truck-8');
    await settled();

    api.expectOne('/api/assets/truck-8').flush({ id: 'truck-8', name: 'Loader 8', type: 'Loader', assetGroupId: null, archived: false });
    api.expectOne('/api/alert-rules').flush([]);
    api.expectOne('/api/alerts?assetId=truck-8').flush([]);
    api.expectOne((r) => r.url === '/api/assets/truck-8/readings').flush([]);
    await settled();
    stale[0].flush({ id: 'truck-7', name: 'Haul truck 7', type: 'Truck', assetGroupId: null, archived: false });
    stale[1].flush([alert]);
    await settled();

    expect(page.querySelector('h1')!.textContent).toContain('Loader 8');
    expect(page.textContent).not.toContain('Engine temp 108');
  });

  it('says so when the range has no Readings', async () => {
    await load([]);

    expect(page.textContent).toContain('No Readings in this range.');
    expect(page.querySelector('app-replay-map')).toBeNull();
  });
});
