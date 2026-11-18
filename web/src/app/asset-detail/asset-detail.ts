import { DatePipe, DecimalPipe } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { Alert, AlertRule, AssetType, Reading, reasonLabels } from '../control-room/live';
import { maxDrawn, positionAt, ruleLines } from './replay';
import { ReplayMap } from './replay-map';
import { SensorChart } from './sensor-chart';

/** An Asset as `GET /assets/{id}` returns it (Archived ones too). */
interface Asset {
  id: string;
  name: string;
  type: AssetType;
  assetGroupId: string | null;
  archived: boolean;
}

const day = 86_400_000;
/** How often a playing replay moves on, in real milliseconds. */
const tick = 100;

/** A time as a `datetime-local` input shows it: local time to the minute. */
function localInput(time: number): string {
  const local = new Date(time - new Date(time).getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}

/**
 * Why the API refused a request, for people: a validation problem's messages (such as a range over 31 days, #26), else
 * its detail or title. Shown as text, never as HTML.
 */
function refusal(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 404) return 'No Asset has this id.';
    const problem = error.error as { title?: string; detail?: string; errors?: Record<string, string[]> } | null;
    const messages = Object.values(problem?.errors ?? {}).flat();
    if (messages.length > 0) return messages.join(' ');
    if (problem?.detail) return problem.detail;
    if (problem?.title) return problem.title;
  }

  return 'The history could not be read. Try again.';
}

/**
 * One Asset's page (Dashboard UX, #20): a route replay over a chosen range of Device time (default the last 24 h) with
 * play, pause, scrub and speed, engine temp and fuel charts whose cursor follows the replay, and the Asset's Alerts.
 */
@Component({
  selector: 'app-asset-detail',
  imports: [DatePipe, DecimalPipe, ReplayMap, RouterLink, SensorChart],
  templateUrl: './asset-detail.html',
  styleUrl: './asset-detail.css',
})
export class AssetDetail {
  /** The Asset's id, from the route. */
  readonly id = input.required<string>();

  private readonly http = inject(HttpClient);
  private player?: ReturnType<typeof setInterval>;
  /** Count reads, so only the latest one's answer is shown. */
  private reads = 0;
  private assetReads = 0;

  protected readonly asset = signal<Asset | null>(null);
  protected readonly rules = signal<AlertRule[]>([]);
  protected readonly alerts = signal<Alert[]>([]);
  protected readonly readings = signal<Reading[]>([]);
  protected readonly loading = signal(true);
  protected readonly problem = signal<string | null>(null);
  protected readonly from = signal(Date.now() - day);
  protected readonly to = signal(Date.now());

  /** Replay time: a Device time inside the range's Readings. */
  protected readonly time = signal(0);
  protected readonly playing = signal(false);
  /** Seconds of Device time per real second. */
  protected readonly speed = signal(300);
  protected readonly speeds = [60, 300, 1200, 3600];

  protected readonly start = computed(() => Date.parse(this.readings()[0]?.deviceTime ?? '') || 0);
  protected readonly end = computed(() => Date.parse(this.readings().at(-1)?.deviceTime ?? '') || 0);
  protected readonly position = computed(() => positionAt(this.readings(), this.time()));
  protected readonly current = computed(() => this.readings()[this.position()] ?? null);
  protected readonly temperatureLines = computed(() => {
    const asset = this.asset();
    return asset ? ruleLines(this.rules(), asset, 'EngineTempC') : [];
  });
  protected readonly reasonLabels = reasonLabels;
  protected readonly localInput = localInput;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.pause());
    // Again for another id: the router reuses this page when only the id changes.
    effect(() => {
      const id = this.id();
      untracked(() => {
        void this.loadAsset(id);
        void this.loadHistory();
      });
    });
  }

  /** Reads the history of the range typed into the form. */
  protected show(from: string, to: string): void {
    this.from.set(new Date(from).getTime());
    this.to.set(new Date(to).getTime());
    void this.loadHistory();
  }

  /**
   * Exports the range typed into the form as CSV (#42): the API issues a single-use URL, and the browser downloads it
   * with its own download UI, so a month of Readings streams to disk instead of into the page.
   */
  protected async export(from: string, to: string): Promise<void> {
    this.problem.set(null);
    const range = { from: new Date(from).toISOString(), to: new Date(to).toISOString() };
    try {
      const { url } = await firstValueFrom(
        this.http.post<{ url: string }>(`/api/assets/${encodeURIComponent(this.id())}/exports`, range),
      );
      const link = document.createElement('a');
      link.href = `/api${url}`;
      // A refused download then fails in the download UI instead of replacing the page.
      link.download = '';
      link.click();
    } catch (error) {
      this.problem.set(refusal(error));
    }
  }

  protected toggle(): void {
    if (this.playing()) {
      this.pause();
      return;
    }

    if (this.time() >= this.end()) {
      this.time.set(this.start());
    }

    this.playing.set(true);
    this.player = setInterval(() => {
      const next = Math.min(this.end(), this.time() + (this.speed() * tick));
      this.time.set(next);
      if (next >= this.end()) {
        this.pause();
      }
    }, tick);
  }

  protected scrub(time: number): void {
    this.time.set(time);
  }

  private pause(): void {
    clearInterval(this.player);
    this.playing.set(false);
  }

  private async loadAsset(id: string): Promise<void> {
    const read = ++this.assetReads;
    this.asset.set(null);
    this.rules.set([]);
    this.alerts.set([]);
    try {
      const [asset, rules, alerts] = await Promise.all([
        firstValueFrom(this.http.get<Asset>(`/api/assets/${encodeURIComponent(id)}`)),
        firstValueFrom(this.http.get<AlertRule[]>('/api/alert-rules')),
        firstValueFrom(this.http.get<Alert[]>('/api/alerts', { params: { assetId: id } })),
      ]);
      if (read === this.assetReads) {
        this.asset.set(asset);
        this.rules.set(rules);
        this.alerts.set(alerts);
      }
    } catch (error) {
      if (read === this.assetReads) {
        this.problem.set(refusal(error));
      }
    }
  }

  /**
   * Reads the range's Readings by Device time (the Asset history endpoint, #26), thinned server-side to what the map
   * draws (#126), and shows the latest one.
   */
  private async loadHistory(): Promise<void> {
    const read = ++this.reads;
    this.pause();
    this.loading.set(true);
    this.problem.set(null);
    const params = { from: new Date(this.from()).toISOString(), to: new Date(this.to()).toISOString(), thinTo: maxDrawn };
    try {
      const readings = await firstValueFrom(
        this.http.get<Reading[]>(`/api/assets/${encodeURIComponent(this.id())}/readings`, { params }),
      );
      if (read === this.reads) {
        this.readings.set(readings);
        this.time.set(this.end());
      }
    } catch (error) {
      if (read === this.reads) {
        this.readings.set([]);
        this.problem.set(refusal(error));
      }
    } finally {
      if (read === this.reads) {
        this.loading.set(false);
      }
    }
  }
}
