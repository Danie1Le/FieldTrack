import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthService } from '../auth/auth';
import { LIVE_HUB, LiveFleet, LiveHubConnection } from './live-fleet';
import { LiveAsset } from './live';

/** Stands in for SignalR's HubConnection: records connects and lets a test fire the client's events. */
class FakeHub implements LiveHubConnection {
  readonly tokens: string[] = [];
  failNextStart = false;
  private readonly handlers = new Map<string, (...args: never[]) => void>();

  constructor(private readonly accessToken: () => Promise<string>) {}

  on(event: string, handler: (assets: LiveAsset[]) => void) {
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

  /** Answers the reads a (re)connect makes over REST. */
  async function serveSnapshot(assets: LiveAsset[]) {
    await vi.waitFor(() => api.expectOne('/api/assets/live'), { timeout: 5_000 }).then((r) => r.flush(assets));
    api.expectOne('/api/asset-groups').flush([]);
    api.expectOne('/api/alert-rules').flush([]);
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
    const snapshot = await vi.waitFor(() => api.expectOne('/api/assets/live'), { timeout: 5_000 });

    hub.fire('AssetsUpdated', [truck('Offline')]);
    snapshot.flush([truck('Normal')]);
    api.expectOne('/api/asset-groups').flush([]);
    api.expectOne('/api/alert-rules').flush([]);
    await vi.waitFor(() => expect(fleet.connection()).toBe('live'));

    expect(fleet.assets()[0].status).toBe('Offline');
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

  it('stops for good when the page closes', async () => {
    fleet.start();
    await serveSnapshot([truck('Normal')]);

    TestBed.resetTestingModule();
    await vi.advanceTimersByTimeAsync(60_000);

    expect(hub.tokens).toEqual(['token-1']);
  });
});
