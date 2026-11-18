import { Component, ElementRef, afterRenderEffect, computed, inject, input } from '@angular/core';
import * as L from 'leaflet';
import { baseMap } from '../base-map';
import { Reading } from '../control-room/live';

/** A palette colour (styles.css); read from the root, since this host is not on the page yet when the layers are made. */
const colour = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

const at = (reading: Reading) => L.latLng(reading.latitude, reading.longitude);

/**
 * The replay map: the whole route faint, the trail driven so far solid, and a dot at the Reading the replay is at.
 * Read-only, like the live map.
 */
@Component({
  selector: 'app-replay-map',
  template: '',
  styles: ':host { display: block; }',
})
export class ReplayMap {
  /** The range's Readings, ordered by Device time. */
  readonly readings = input.required<Reading[]>();
  /** Index of the Reading the replay is at. */
  readonly position = input.required<number>();

  private readonly host: HTMLElement = inject(ElementRef).nativeElement;
  // On a phone the page reflows; the base map re-measures on every resize.
  private readonly map = baseMap(this.host, () => this.fitOnce());
  private readonly route = L.polyline([], {
    color: colour('--muted'),
    weight: 3,
    opacity: 0.8,
    dashArray: '4 6',
    interactive: false,
  });
  private readonly trail = L.polyline([], { color: colour('--accent'), weight: 4, interactive: false });
  private readonly dot = L.circleMarker([0, 0], {
    radius: 7,
    color: colour('--panel'),
    weight: 2,
    fillColor: colour('--accent'),
    fillOpacity: 1,
    interactive: false,
  });
  /** Made once per range, not per replay tick. */
  private readonly points = computed(() => this.readings().map(at));
  private fitted = false;

  constructor() {
    this.route.addTo(this.map);
    this.trail.addTo(this.map);
    this.dot.addTo(this.map);

    afterRenderEffect(() => {
      this.route.setLatLngs(this.points());
      this.fitted = false;
      this.fitOnce();
    });
    afterRenderEffect(() => {
      const [points, position] = [this.points(), this.position()];
      this.trail.setLatLngs(points.slice(0, position + 1));
      if (position >= 0) {
        this.dot.setLatLng(points[position]);
      }
    });
  }

  /** Frames the route once per range, when the map has a size. */
  private fitOnce(): void {
    if (this.fitted || this.readings().length === 0 || this.host.clientWidth === 0) {
      return;
    }

    // Leaflet keeps the size it measured first (none: the map is made before the page lays it out).
    this.map.invalidateSize();
    this.map.fitBounds(this.route.getBounds(), { padding: [20, 20], maxZoom: 15 });
    this.fitted = true;
  }
}

