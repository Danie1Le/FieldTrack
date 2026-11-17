import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { Alert, ResolveReason } from './live';
import { LiveFleet } from './live-fleet';

/** Newest first by Device time raised, ties by id: the API's order, so a pushed Alert lands where a reload would put it. */
const newestFirst = (a: Alert, b: Alert) =>
  Date.parse(b.raisedAt) - Date.parse(a.raisedAt) || (a.id < b.id ? 1 : a.id > b.id ? -1 : 0);

/** The Active Alerts, newest first: the feed's main list. */
export function activeAlerts(alerts: readonly Alert[]): Alert[] {
  return alerts.filter((a) => a.status !== 'Resolved').sort(newestFirst);
}

/** The Resolved Alerts, newest first: the History tab. */
export function resolvedAlerts(alerts: readonly Alert[]): Alert[] {
  return alerts.filter((a) => a.status === 'Resolved').sort(newestFirst);
}

const reasonLabels: Record<ResolveReason, string> = {
  Cleared: 'Cleared',
  Manual: 'Manual',
  RuleChanged: 'Rule changed',
  RuleDisabled: 'Rule disabled',
  AssetArchived: 'Asset archived',
  DeviceDetached: 'Device detached',
};

/**
 * The Alert feed (Dashboard UX, #20): Active Alerts newest first with Acknowledge (Open only) and Resolve, which opens
 * an inline form with an optional note; Resolved Alerts on a History tab. Everything shown comes from LiveFleet, so it
 * updates live.
 */
@Component({
  selector: 'app-alert-feed',
  imports: [DatePipe],
  templateUrl: './alert-feed.html',
  styleUrl: './alert-feed.css',
})
export class AlertFeed {
  protected readonly fleet = inject(LiveFleet);

  protected readonly view = signal<'active' | 'history'>('active');
  protected readonly active = computed(() => activeAlerts(this.fleet.alerts()));
  protected readonly history = computed(() => resolvedAlerts(this.fleet.alerts()));
  /** The Alert whose Resolve form is open; one at a time. */
  protected readonly resolving = signal<string | null>(null);
  protected readonly note = signal('');
  /** Alerts with an action in flight (several can be at once). */
  protected readonly busy = signal<ReadonlySet<string>>(new Set());
  /** Why an Alert's last action failed, by Alert id. */
  protected readonly failures = signal<ReadonlyMap<string, string>>(new Map());
  protected readonly reasonLabels = reasonLabels;

  private readonly assetNames = computed(() => new Map(this.fleet.assets().map((a) => [a.id, a.name])));
  private readonly ruleNames = computed(() => new Map(this.fleet.rules().map((r) => [r.id, r.name])));

  /** Archived Assets are not in the live list, so their Alerts fall back to a generic name. */
  protected assetName(alert: Alert): string {
    return this.assetNames().get(alert.assetId) ?? 'Archived Asset';
  }

  protected ruleName(alert: Alert): string {
    return this.ruleNames().get(alert.alertRuleId) ?? '';
  }

  protected async acknowledge(alert: Alert): Promise<void> {
    await this.act(alert, () => this.fleet.acknowledge(alert.id), 'It is no longer Open.');
  }

  protected openResolve(alert: Alert): void {
    this.resolving.set(alert.id);
    this.note.set('');
    this.setFailure(alert.id, null);
  }

  protected cancelResolve(alert: Alert): void {
    this.resolving.set(null);
    this.setFailure(alert.id, null);
  }

  protected async confirmResolve(alert: Alert): Promise<void> {
    if (await this.act(alert, () => this.fleet.resolve(alert.id, this.note()), 'It is already Resolved.')) {
      this.resolving.set(null);
    }
  }

  /** Runs an Alert action; on failure shows why under the Alert and answers false. */
  private async act(alert: Alert, action: () => Promise<void>, conflict: string): Promise<boolean> {
    this.busy.update((ids) => new Set(ids).add(alert.id));
    this.setFailure(alert.id, null);
    try {
      await action();
      return true;
    } catch (error) {
      const status = error instanceof HttpErrorResponse ? error.status : 0;
      this.setFailure(alert.id, status === 409 ? conflict : 'The Alert could not be updated. Try again.');
      return false;
    } finally {
      this.busy.update((ids) => new Set([...ids].filter((id) => id !== alert.id)));
    }
  }

  private setFailure(alertId: string, text: string | null): void {
    this.failures.update((held) => {
      const next = new Map(held);
      if (text === null) {
        next.delete(alertId);
      } else {
        next.set(alertId, text);
      }
      return next;
    });
  }
}
