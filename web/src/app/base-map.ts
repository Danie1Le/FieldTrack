import { DestroyRef, inject } from '@angular/core';
import * as L from 'leaflet';

/**
 * The map every Leaflet view shares: OpenStreetMap tiles with attribution, zoom control bottom left, re-measured
 * whenever `host` is shown or resized (`onResize` runs after each re-measure). Call it where `inject` works; the
 * map is removed with the component.
 */
export function baseMap(host: HTMLElement, onResize?: () => void): L.Map {
  const map = L.map(host, { zoomControl: false }).setView([52.2, -1.1], 6);
  L.control.zoom({ position: 'bottomleft' }).addTo(map);
  L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
    maxZoom: 19,
    attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
  }).addTo(map);
  const resized = new ResizeObserver(() => {
    map.invalidateSize();
    onResize?.();
  });
  resized.observe(host);
  inject(DestroyRef).onDestroy(() => {
    resized.disconnect();
    map.remove();
  });
  return map;
}
