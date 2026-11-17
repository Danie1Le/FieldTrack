import { DecimalPipe } from '@angular/common';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { AuthService } from '../auth/auth';
import { AlertFeed, activeAlerts } from './alert-feed';
import { breachedSensors, groupByStatus, lastSeen, onMap } from './asset-status';
import { FleetMap } from './fleet-map';
import { LiveAsset } from './live';
import { LiveFleet } from './live-fleet';

/** Below this width the three columns become tabs (Dashboard UX, #20); matches the media query in control-room.css. */
const narrow = '(max-width: 1000px)';

/** The Control room: Assets grouped by Asset Status, the live map and the Alert feed (Dashboard UX prototype, variant B). */
@Component({
  selector: 'app-control-room',
  imports: [AlertFeed, DecimalPipe, FleetMap],
  providers: [LiveFleet],
  templateUrl: './control-room.html',
  styleUrl: './control-room.css',
})
export class ControlRoom {
  protected readonly auth = inject(AuthService);
  protected readonly fleet = inject(LiveFleet);

  protected readonly tab = signal<'map' | 'assets' | 'alerts'>('map');
  protected readonly selected = signal<string | null>(null);
  /** Set on every selection, even of the selected Asset, so the map centres again after a pan. */
  protected readonly focus = signal<{ id: string } | null>(null);
  /** Ticks so "last seen" stays current between pushes. */
  private readonly now = signal(Date.now());

  protected readonly groups = computed(() => groupByStatus(this.fleet.assets()));
  /** Shown on the Alerts tab, which is all a phone sees of the feed until it is opened. */
  protected readonly activeAlerts = computed(() => activeAlerts(this.fleet.alerts()).length);
  private readonly groupNames = computed(() => new Map(this.fleet.groups().map((g) => [g.id, g.name])));

  constructor() {
    this.fleet.start();
    const clock = setInterval(() => this.now.set(Date.now()), 5_000);
    inject(DestroyRef).onDestroy(() => clearInterval(clock));
  }

  protected groupName(asset: LiveAsset): string {
    return asset.assetGroupId ? (this.groupNames().get(asset.assetGroupId) ?? '') : '';
  }

  protected lastSeen(asset: LiveAsset): string {
    return lastSeen(asset.lastReceivedAt, this.now());
  }

  protected breached(asset: LiveAsset) {
    return breachedSensors(asset, this.fleet.rules());
  }

  protected select(id: string): void {
    this.selected.set(id);
    this.focus.set({ id });
  }

  protected selectRow(asset: LiveAsset): void {
    this.select(asset.id);
    // On a phone the map is behind its tab; show it so the selection is visible.
    if (onMap(asset) && matchMedia(narrow).matches) {
      this.tab.set('map');
    }
  }
}
