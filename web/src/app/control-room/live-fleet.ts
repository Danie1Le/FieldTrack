import { HttpClient } from '@angular/common/http';
import { DestroyRef, Injectable, InjectionToken, inject, signal } from '@angular/core';
import { HubConnectionBuilder } from '@microsoft/signalr';
import { firstValueFrom } from 'rxjs';
import { AuthService } from '../auth/auth';
import { AlertRule, AssetGroup, LiveAsset } from './live';

/** The part of SignalR's HubConnection the Control room uses; tests fake it at this boundary. */
export interface LiveHubConnection {
  on(event: 'AssetsUpdated', handler: (assets: LiveAsset[]) => void): void;
  onreconnecting(handler: () => void): void;
  onreconnected(handler: () => void): void;
  onclose(handler: (error?: Error) => void): void;
  start(): Promise<void>;
  stop(): Promise<void>;
}

/** Wait before each retry after a failed connect, the last one repeating. */
const retryDelays = [1_000, 2_000, 5_000, 10_000, 30_000];

/** Builds the connection to the API's live hub; the token factory runs on every connect and reconnect. */
export const LIVE_HUB = new InjectionToken<(accessToken: () => Promise<string>) => LiveHubConnection>('LIVE_HUB', {
  providedIn: 'root',
  factory: () => (accessTokenFactory) =>
    new HubConnectionBuilder()
      .withUrl('/api/hubs/live', { accessTokenFactory })
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (r) => retryDelays[Math.min(r.previousRetryCount, 4)] })
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

  readonly assets = signal<LiveAsset[]>([]);
  readonly groups = signal<AssetGroup[]>([]);
  readonly rules = signal<AlertRule[]>([]);
  readonly connection = signal<'connecting' | 'live' | 'reconnecting'>('connecting');

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.stopped = true;
      clearTimeout(this.retry);
      void this.hub.stop();
    });
    this.hub.on('AssetsUpdated', (changed) => this.merge(changed));
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
      this.retry = setTimeout(() => void this.connect(), retryDelays[Math.min(this.failedConnects++, 4)]);
    }
  }

  /** Reads the fleet over REST; on failure drops the connection, so the reconnect path loads again. */
  private async load(): Promise<void> {
    try {
      const [assets, groups, rules] = await Promise.all([
        firstValueFrom(this.http.get<LiveAsset[]>('/api/assets/live')),
        firstValueFrom(this.http.get<AssetGroup[]>('/api/asset-groups')),
        firstValueFrom(this.http.get<AlertRule[]>('/api/alert-rules')),
      ]);
      this.assets.set(assets);
      this.groups.set(groups);
      this.rules.set(rules);
      this.failedConnects = 0;
      this.connection.set('live');
    } catch {
      await this.hub.stop();
    }
  }

  /** Applies an AssetsUpdated push: changed Assets replace theirs by id, new ones are added. */
  private merge(changed: LiveAsset[]): void {
    const byId = new Map(changed.map((a) => [a.id, a]));
    const kept = this.assets().map((a) => byId.get(a.id) ?? a);
    const known = new Set(kept.map((a) => a.id));
    this.assets.set([...kept, ...changed.filter((a) => !known.has(a.id))]);
  }
}
