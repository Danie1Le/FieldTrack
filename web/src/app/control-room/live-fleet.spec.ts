import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthService } from '../auth/auth';
import { LIVE_HUB, LiveFleet, LiveHubConnection } from './live-fleet';
import { Alert, AlertRule, Geofence, LiveAsset } from './live';

/** Stands in for SignalR's HubConnection: records connects and lets a test fire the client's events. */
class FakeHub implements LiveHubConnection {
  readonly tokens: string[] = [];
  failNextStart = false;
  private readonly handlers = new Map<string, (...args: never[]) => void>();

  constructor(private readonly accessToken: () => Promise<string>) {}

  on(event: string, handler: (pushed: never) => void) {
    this.handlers.set(event, handler);
  }
  onreconnecting(handler: () => void) {
    this.handlers.set('reconnecting', handler);
  }
  onreconnected(handler: () => void) {
    this.handlers.set('reconnected', handler);
  }
  onclose(handler: (error?: Error) => void) {
    this.handlers.set('close', handler);
  }

  async start() {
    // Like the real client, every connect asks for the token anew.
    this.tokens.push(await this.accessToken());
    if (this.failNextStart) {
      this.failNextStart = false;
      throw new Error('Failed to complete negotiation with the server');
    }
  }

  async stop() {
    this.fire('close');
  }

  fire(event: string, ...args: unknown[]) {
    (this.handlers.get(event) as (...a: unknown[]) => void)(...args);
  }
}

const truck = (status: LiveAsset['status'], id = 'truck-1'): LiveAsset => ({
  id,
  name: `Truck ${id}`,
  type: 'Truck',
  assetGroupId: null,
  status,
  lastReceivedAt: null,
  latest: null,
});

const overheat = (status: Alert['status'], id = 'alert-1', raisedAt = '2026-11-17T10:00:00Z'): Alert => ({
  id,
  alertRuleId: 'rule-1',
  assetId: 'truck-1',
  severity: 'Critical',
  message: 'Engine temp 108.4 °C, above 105',
  status,
  raisedAt,
  resolvedAt: status === 'Resolved' ? '2026-11-17T10:05:00Z' : null,
  resolveReason: status === 'Resolved' ? 'Cleared' : null,
  acknowledgedBy: null,
  acknowledgedAt: null,
  resolvedBy: null,
  note: null,
  latched: false,
  fromBacklog: false,
});

const depot = (name: string): Geofence => ({
  id: 'geofence-1',
  name,
  kind: 'Inclusion',
  area: { type: 'Polygon', coordinates: [[[-0.13, 51.5], [-0.12, 51.5], [-0.12, 51.51], [-0.13, 51.5]]] },
});

describe('LiveFleet', () => {
  let hub: FakeHub;
  let api: HttpTestingController;
  let fleet: LiveFleet;
  let tokens: string[];

  beforeEach(() => {
    vi.useFakeTimers();
    tokens = ['token-1', 'token-2', 'token-3'];
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        LiveFleet,
        { provide: AuthService, useValue: { accessToken: async () => tokens.shift() ?? null } },
        { provide: LIVE_HUB, useValue: (accessToken: () => Promise<string>) => (hub = new FakeHub(accessToken)) },
      ],
    });
    api = TestBed.inject(HttpTestingController);
    fleet = TestBed.inject(LiveFleet);
  });

  afterEach(() => {
    api.verify();
    vi.useRealTimers();
  });

  /** The reads a (re)connect makes over REST, the Assets first. */
  async function snapshotReads() {
    const assets = await vi.waitFor(() => api.expectOne('/api/assets/live'), { timeout: 5_000 });
    return {
      assets,
      others: [
        api.expectOne('/api/asset-groups'),
        api.expectOne('/api/alert-rules'),
        api.expectOne('/api/alerts?active=true'),
        api.expectOne('/api/alerts?active=false'),
        api.expectOne('/api/geofences'),
      ],
    };
  }

  /** Answers the reads a (re)connect makes over REST. */
  async function serveSnapshot(assets: LiveAsset[], active: Alert[] = [], resolved: Alert[] = []) {
    const reads = await snapshotReads();
    reads.assets.flush(assets);
    reads.others.forEach((read, i) => read.flush(i === 2 ? active : i === 3 ? resolved : []));
    await vi.waitFor(() => expect(fleet.connection()).toBe('live'));
  }

  it('loads the fleet once connected, then applies pushed changes', async () => {
    fleet.start();
    await serveSnapshot([truck('Normal'), truck('Normal', 'truck-2')]);

    hub.fire('AssetsUpdated', [truck('Alerting'), truck('Normal', 'truck-3')]);

    expect(fleet.assets().map((a) => [a.id, a.status])).toEqual([
      ['truck-1', 'Alerting'],
      ['truck-2', 'Normal'],
      ['truck-3', 'Normal'],
    ]);
  });

  it('keeps a push that arrives while the fleet loads over the older snapshot', async () => {
    fleet.start();
    const snapshot = await snapshotReads();

    hub.fire('AssetsUpdated', [truck('Offline')]);
    snapshot.assets.flush([truck('Normal')]);
    snapshot.others.forEach((read) => read.flush([]));
    await vi.waitFor(() => expect(fleet.connection()).toBe('live'));

    expect(fleet.assets()[0].status).toBe('Offline');
  });

  it('drops an Asset that was Archived or deleted, also over a snapshot loading meanwhile', async () => {
    fleet.start();
    await serveSnapshot([truck('Normal'), truck('Normal', 'truck-2')]);

    hub.fire('AssetsRemoved', ['truck-1']);
    expect(fleet.assets().map((a) => a.id)).toEqual(['truck-2']);

    hub.fire('reconnecting');
    hub.fire('reconnected');
    const snapshot = await snapshotReads();
    hub.fire('AssetsRemoved', ['truck-2']);
    snapshot.assets.flush([truck('Normal', 'truck-2'), truck('Normal', 'truck-3')]);
    snapshot.others.forEach((read) => read.flush([]));
    await vi.waitFor(() => expect(fleet.connection()).toBe('live'));

    expect(fleet.assets().map((a) => a.id)).toEqual(['truck-3']);
  });

  it('reloads the Alert Rules and Asset Groups when either changes', async () => {
    fleet.start();
    await serveSnapshot([truck('Normal')]);
    const rule: AlertRule = {
      id: 'rule-1',
      name: 'Engine overheat',
      kind: 'Threshold',
      assetId: 'truck-1',
      assetGroupId: null,
      severity: 'Critical',
      enabled: true,
      sensor: 'EngineTempC',
      operator: 'GreaterThan',
      threshold: 95,
      hysteresis: 5,
      geofenceId: null,
    };

    hub.fire('AlertRulesChanged');
    api.expectOne('/api/alert-rules').flush([rule]);
    hub.fire('AssetGroupsChanged');
    api.expectOne('/api/asset-groups').flush([{ id: 'group-1', name: 'North yard' }]);

    await vi.waitFor(() => expect(fleet.rules().map((r) => r.threshold)).toEqual([95]));
    await vi.waitFor(() => expect(fleet.groups().map((g) => g.name)).toEqual(['North yard']));
  });

  it('lets two overlapping loads each finish without stopping the hub', async () => {
    fleet.start();
    const first = await snapshotReads();

    hub.fire('reconnected');
    const second = await snapshotReads();

    first.assets.flush([truck('Normal')]);
    first.others.forEach((read) => read.flush([]));
    await vi.waitFor(() => expect(fleet.connection()).toBe('live'));
    second.assets.flush([truck('Offline')]);
    second.others.forEach((read) => read.flush([]));

    await vi.waitFor(() => expect(fleet.assets()[0].status).toBe('Offline'));
    expect(fleet.connection()).toBe('live');
  });

  it('loads Active and Resolved Alerts once connected, then applies raised and updated Alerts', async () => {
    fleet.start();
    await serveSnapshot([truck('Alerting')], [overheat('Open')], [overheat('Resolved', 'alert-0')]);

    hub.fire('AlertRaised', overheat('Open', 'alert-2'));
    hub.fire('AlertUpdated', overheat('Acknowledged'));

    expect(fleet.alerts().map((a) => [a.id, a.status])).toEqual([
      ['alert-1', 'Acknowledged'],
      ['alert-0', 'Resolved'],
      ['alert-2', 'Open'],
    ]);
  });

  it('never moves an Alert back to an earlier status, whatever order its copies arrive in', async () => {
    fleet.start();
    const snapshot = await snapshotReads();

    // Pushed while the snapshot loads; the snapshot was read before it and replays under it.
    hub.fire('AlertUpdated', overheat('Resolved'));
    snapshot.assets.flush([]);
    snapshot.others.forEach((read, i) => read.flush(i === 2 ? [overheat('Acknowledged')] : []));
    await vi.waitFor(() => expect(fleet.connection()).toBe('live'));
    hub.fire('AlertUpdated', overheat('Open'));

    expect(fleet.alerts().map((a) => a.status)).toEqual(['Resolved']);
  });

  it('Acknowledges and Resolves through the API and shows the answer before its push arrives', async () => {
    fleet.start();
    await serveSnapshot([], [overheat('Open'), overheat('Open', 'alert-2')]);

    const acknowledged = fleet.acknowledge('alert-1');
    api.expectOne({ method: 'POST', url: '/api/alerts/alert-1/acknowledge' }).flush(overheat('Acknowledged'));
    await acknowledged;
    const resolved = fleet.resolve('alert-2', 'Refuel booked');
    const resolve = api.expectOne({ method: 'POST', url: '/api/alerts/alert-2/resolve' });
    expect(resolve.request.body).toEqual({ note: 'Refuel booked' });
    resolve.flush(overheat('Resolved', 'alert-2'));
    await resolved;

    expect(fleet.alerts().map((a) => a.status)).toEqual(['Acknowledged', 'Resolved']);
  });

  it('keeps an action answer over a snapshot a running load read before the action committed', async () => {
    fleet.start();
    await serveSnapshot([], [overheat('Open')]);
    hub.fire('reconnected');
    const stale = await snapshotReads();

    const acknowledged = fleet.acknowledge('alert-1');
    api.expectOne({ method: 'POST', url: '/api/alerts/alert-1/acknowledge' }).flush(overheat('Acknowledged'));
    await acknowledged;
    stale.assets.flush([]);
    stale.others.forEach((read, i) => read.flush(i === 2 ? [overheat('Open')] : []));
    await vi.advanceTimersByTimeAsync(0);

    expect(fleet.alerts().map((a) => a.status)).toEqual(['Acknowledged']);
  });

  it('reloads the fleet over REST after an automatic reconnect, recovering missed pushes', async () => {
    fleet.start();
    await serveSnapshot([truck('Normal')]);

    hub.fire('reconnecting');
    expect(fleet.connection()).toBe('reconnecting');
    hub.fire('reconnected');
    await serveSnapshot([truck('Offline')]);

    expect(fleet.assets()[0].status).toBe('Offline');
  });

  it('connects again with a fresh access token after the hub closes the connection at token expiry', async () => {
    fleet.start();
    await serveSnapshot([truck('Normal')]);

    hub.fire('close', new Error('Server returned an error on close: Connection closed with an error.'));
    expect(fleet.connection()).toBe('reconnecting');
    await serveSnapshot([truck('Normal')]);

    expect(hub.tokens).toEqual(['token-1', 'token-2']);
  });

  it('keeps trying when a connect fails', async () => {
    fleet.start();
    await serveSnapshot([truck('Normal')]);
    hub.failNextStart = true;

    hub.fire('close');
    await serveSnapshot([truck('Normal')]);

    expect(hub.tokens).toEqual(['token-1', 'token-2', 'token-3']);
  });

  it('loads the Geofences once connected and reloads them when a Geofence changes', async () => {
    fleet.start();
    const reads = await snapshotReads();
    reads.assets.flush([]);
    reads.others.forEach((read, i) => read.flush(i === 4 ? [depot('Depot')] : []));
    await vi.waitFor(() => expect(fleet.connection()).toBe('live'));

    hub.fire('GeofencesChanged');
    api.expectOne('/api/geofences').flush([depot('Yard')]);

    await vi.waitFor(() => expect(fleet.geofences().map((g) => g.name)).toEqual(['Yard']));
  });

  it('keeps the newest Geofence read when two reloads answer out of order', async () => {
    fleet.start();
    await serveSnapshot([]);

    hub.fire('GeofencesChanged');
    hub.fire('GeofencesChanged');
    const [older, newer] = api.match('/api/geofences');
    newer.flush([depot('Yard')]);
    older.flush([depot('Depot')]);
    await vi.advanceTimersByTimeAsync(0);

    expect(fleet.geofences().map((g) => g.name)).toEqual(['Yard']);
  });

  it('stops for good when the page closes', async () => {
    fleet.start();
    await serveSnapshot([truck('Normal')]);

    TestBed.resetTestingModule();
    await vi.advanceTimersByTimeAsync(60_000);

    expect(hub.tokens).toEqual(['token-1']);
  });
});
