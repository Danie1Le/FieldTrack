import { Component, DestroyRef, ElementRef, afterRenderEffect, inject, input } from '@angular/core';
import * as L from 'leaflet';
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
  private readonly map = L.map(this.host, { zoomControl: false }).setView([52.2, -1.1], 6);
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
  private fitted = false;

  constructor() {
    L.control.zoom({ position: 'bottomleft' }).addTo(this.map);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19,
      attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    }).addTo(this.map);
    this.route.addTo(this.map);
    this.trail.addTo(this.map);
    this.dot.addTo(this.map);
    // On a phone the page reflows; re-measure on every resize.
    const resized = new ResizeObserver(() => {
      this.map.invalidateSize();
      this.fitOnce();
    });
    resized.observe(this.host);
    inject(DestroyRef).onDestroy(() => {
      resized.disconnect();
      this.map.remove();
    });

    afterRenderEffect(() => {
      this.route.setLatLngs(this.readings().map(at));
      this.fitted = false;
      this.fitOnce();
    });
    afterRenderEffect(() => {
      const shown = this.readings().slice(0, this.position() + 1);
      this.trail.setLatLngs(shown.map(at));
      if (shown.length > 0) {
        this.dot.setLatLng(at(shown[shown.length - 1]));
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

