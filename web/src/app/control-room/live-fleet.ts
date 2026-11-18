import { HttpClient } from '@angular/common/http';
import { DestroyRef, Injectable, InjectionToken, WritableSignal, inject, signal } from '@angular/core';
import { HubConnectionBuilder } from '@microsoft/signalr';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../auth/auth';
import { Alert, AlertRule, AlertStatus, AssetGroup, Geofence, LiveAsset } from './live';

/** The part of SignalR's HubConnection the Control room uses; tests fake it at this boundary. */
export interface LiveHubConnection {
  on(event: 'AssetsUpdated', handler: (assets: LiveAsset[]) => void): void;
  on(event: 'AssetsRemoved', handler: (assetIds: string[]) => void): void;
  on(event: 'AlertRaised' | 'AlertUpdated', handler: (alert: Alert) => void): void;
  on(event: 'GeofencesChanged' | 'AlertRulesChanged' | 'AssetGroupsChanged', handler: () => void): void;
  onreconnecting(handler: () => void): void;
  onreconnected(handler: () => void): void;
  onclose(handler: (error?: Error) => void): void;
  start(): Promise<void>;
  stop(): Promise<void>;
}

/** Wait before each retry after a failed connect, the last one repeating. */
const retryDelays = [1_000, 2_000, 5_000, 10_000, 30_000];
const retryDelay = (attempt: number) => retryDelays[Math.min(attempt, retryDelays.length - 1)];

/** An Alert's status only moves forward (a Resolved Alert never reopens), so a copy behind the one held is stale. */
const progress: Record<AlertStatus, number> = { Open: 0, Acknowledged: 1, Resolved: 2 };

/** Builds the connection to the API's live hub; the token factory runs on every connect and reconnect. */
export const LIVE_HUB = new InjectionToken<(accessToken: () => Promise<string>) => LiveHubConnection>('LIVE_HUB', {
  providedIn: 'root',
  factory: () => (accessTokenFactory) =>
    new HubConnectionBuilder()
      .withUrl('/api/hubs/live', { accessTokenFactory })
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (r) => retryDelay(r.previousRetryCount) })
      .build(),
});

/**
 * The fleet as the Control room shows it, kept live over SignalR. Pushes carry no delivery guarantee (ADR 0005), so
 * every (re)connect reloads the fleet over REST. SignalR's automatic reconnect covers a dropped connection; when the
 * connection closes for good (the API closes it when its access token expires, ADR 0002, or reconnecting gave up)
 * this starts a new one, which asks for a fresh token.
 */
@Injectable()
export class LiveFleet {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly hub = inject(LIVE_HUB)(async () => {
    const token = await this.auth.accessToken();
    if (!token) {
      throw new Error('Signed out');
    }

    return token;
  });
  private stopped = false;
  private failedConnects = 0;
  private retry?: ReturnType<typeof setTimeout>;
  /** One buffer per running load(): pushes that arrive meanwhile, replayed over its snapshot, which may be older than them. */
  private readonly pushedDuringLoads = new Set<(() => void)[]>();

  readonly assets = signal<LiveAsset[]>([]);
  /** Active and Resolved Alerts, unordered; the Alert feed sorts them. */
  readonly alerts = signal<Alert[]>([]);
  readonly groups = signal<AssetGroup[]>([]);
  readonly rules = signal<AlertRule[]>([]);
  readonly geofences = signal<Geofence[]>([]);
  readonly connection = signal<'connecting' | 'live' | 'reconnecting'>('connecting');

  private readonly loadGroups = this.newestRead('/api/asset-groups', this.groups);
  private readonly loadRules = this.newestRead('/api/alert-rules', this.rules);
  private readonly loadGeofences = this.newestRead('/api/geofences', this.geofences);

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.stopped = true;
      clearTimeout(this.retry);
      void this.hub.stop();
    });
    this.hub.on('AssetsUpdated', (changed) => this.pushed(() => this.merge(changed)));
    this.hub.on('AssetsRemoved', (removed) => this.pushed(() => this.remove(removed)));
    this.hub.on('AlertRaised', (alert) => this.pushed(() => this.mergeAlerts([alert])));
    this.hub.on('AlertUpdated', (alert) => this.pushed(() => this.mergeAlerts([alert])));
    // A failed reload drops the connection, so the reconnect path loads again.
    const reloadOn = (event: 'GeofencesChanged' | 'AlertRulesChanged' | 'AssetGroupsChanged', load: () => Promise<void>) =>
      this.hub.on(event, () => void load().catch(() => this.hub.stop()));
    reloadOn('GeofencesChanged', this.loadGeofences);
    reloadOn('AlertRulesChanged', this.loadRules);
    reloadOn('AssetGroupsChanged', this.loadGroups);
    this.hub.onreconnecting(() => this.connection.set('reconnecting'));
    this.hub.onreconnected(() => void this.load());
    this.hub.onclose(() => {
      this.connection.set('reconnecting');
      this.connectLater();
    });
  }

  /** Connects and loads the fleet; retries until the page closes. */
  start(): void {
    void this.connect();
  }

  /** Acknowledges an Open Alert as the signed-in user; rejects with the API's error. */
  async acknowledge(alertId: string): Promise<void> {
    this.answered(await firstValueFrom(this.http.post<Alert>(`/api/alerts/${encodeURIComponent(alertId)}/acknowledge`, null)));
  }

  /** Resolves an Active Alert by hand with an optional note; rejects with the API's error. */
  async resolve(alertId: string, note: string): Promise<void> {
    this.answered(await firstValueFrom(this.http.post<Alert>(`/api/alerts/${encodeURIComponent(alertId)}/resolve`, { note })));
  }

  private async connect(): Promise<void> {
    if (this.stopped) {
      return;
    }

    try {
      await this.hub.start();
    } catch {
      this.connectLater();
      return;
    }

    await this.load();
  }

  private connectLater(): void {
    if (!this.stopped) {
      this.retry = setTimeout(() => void this.connect(), retryDelay(this.failedConnects++));
    }
  }

  /** Reads the fleet over REST; on failure drops the connection, so the reconnect path loads again. */
  private async load(): Promise<void> {
    const pushed: (() => void)[] = [];
    this.pushedDuringLoads.add(pushed);
    try {
      const [assets, active, resolved] = await Promise.all([
        firstValueFrom(this.http.get<LiveAsset[]>('/api/assets/live')),
        // Two reads, so a long History cannot crowd Active Alerts out of the API's newest-first limit.
        firstValueFrom(this.http.get<Alert[]>('/api/alerts?active=true')),
        firstValueFrom(this.http.get<Alert[]>('/api/alerts?active=false')),
        this.loadGroups(),
        this.loadRules(),
        this.loadGeofences(),
      ]);
      this.assets.set(assets);
      this.alerts.set([...active, ...resolved]);
      pushed.forEach((apply) => apply());
      this.failedConnects = 0;
      this.connection.set('live');
    } catch {
      await this.hub.stop();
    } finally {
      this.pushedDuringLoads.delete(pushed);
    }
  }

  /**
   * Reads a list that its push (GeofencesChanged and the like) only names. The push carries no change to replay, so
   * reads are ordered instead: a read answering after a newer one is dropped, as it may predate the change that the
   * newer one was sent for.
   */
  private newestRead<T>(url: string, into: WritableSignal<T[]>): () => Promise<void> {
    let reads = 0;
    return async () => {
      const read = ++reads;
      const items = await firstValueFrom(this.http.get<T[]>(url));
      if (read === reads) {
        into.set(items);
      }
    };
  }

  /** Applies a push now, and again over the snapshot of every load running meanwhile. */
  private pushed(apply: () => void): void {
    if (this.pushedDuringLoads.size === 0) {
      apply();
    }
    this.pushedDuringLoads.forEach((pushed) => pushed.push(apply));
  }

  /** An action's answer: shown now, and replayed over the snapshot of every load running meanwhile (it may predate the action). */
  private answered(alert: Alert): void {
    const apply = () => this.mergeAlerts([alert]);
    apply();
    this.pushedDuringLoads.forEach((pushed) => pushed.push(apply));
  }

  /** Applies Alerts from a push or an Alert action: each replaces its older copy by id, new ones are added. */
  private mergeAlerts(changed: Alert[]): void {
    const byId = new Map(this.alerts().map((a) => [a.id, a]));
    for (const alert of changed) {
      const held = byId.get(alert.id);
      if (!held || progress[alert.status] >= progress[held.status]) {
        byId.set(alert.id, alert);
      }
    }
    this.alerts.set([...byId.values()]);
  }

  /** Applies an AssetsRemoved push: Archived or deleted Assets leave the map and the list. */
  private remove(removed: string[]): void {
    const gone = new Set(removed);
    this.assets.set(this.assets().filter((a) => !gone.has(a.id)));
  }

  /** Applies an AssetsUpdated push: changed Assets replace theirs by id, new ones are added. */
  private merge(changed: LiveAsset[]): void {
    const byId = new Map(changed.map((a) => [a.id, a]));
    const kept = this.assets().map((a) => byId.get(a.id) ?? a);
    const known = new Set(kept.map((a) => a.id));
    this.assets.set([...kept, ...changed.filter((a) => !known.has(a.id))]);
  }
}
