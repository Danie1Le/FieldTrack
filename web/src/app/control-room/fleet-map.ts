import { Component, DestroyRef, ElementRef, afterRenderEffect, inject, input, output, untracked } from '@angular/core';
import * as L from 'leaflet';
import { kindLetter, onMap, statusLabels } from './asset-status';
import { LiveAsset } from './live';

/**
 * A marker's face: colour plus shape so Asset Status reads without colour vision (green circle Normal, red diamond
 * Alerting, hollow amber ring Offline), with the Asset Type's letter inside. Styles live in styles.css because Leaflet,
 * not Angular, puts the element on the page.
 */
export function markerElement(asset: LiveAsset, selected: boolean): HTMLElement {
  const marker = document.createElement('div');
  marker.className = `mk mk-${asset.status.toLowerCase()}${selected ? ' mk-sel' : ''}`;
  const letter = document.createElement('span');
  letter.textContent = kindLetter(asset.type);
  marker.append(letter);
  return marker;
}

/** The live map: one marker per Asset with a position; selecting an Asset highlights and centres its marker. */
@Component({
  selector: 'app-fleet-map',
  template: '',
  styles: ':host { display: block; }',
})
export class FleetMap {
  readonly assets = input.required<LiveAsset[]>();
  readonly selected = input<string | null>(null);
  /** Centres on this Asset; a new object each time, so selecting the same Asset again centres it again. */
  readonly focus = input<{ id: string } | null>(null);
  readonly select = output<string>();

  private readonly host: HTMLElement = inject(ElementRef).nativeElement;
  private readonly markers = new Map<string, L.Marker>();
  private readonly map = L.map(this.host, { zoomControl: false }).setView([52.2, -1.1], 6);
  private fitted = false;

  constructor() {
    L.control.zoom({ position: 'bottomleft' }).addTo(this.map);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19,
      attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    }).addTo(this.map);
    // On narrow screens the map sits behind a tab: re-measure whenever it is shown or resized.
    const resized = new ResizeObserver(() => {
      this.map.invalidateSize();
      this.fitOnce();
    });
    resized.observe(this.host);
    inject(DestroyRef).onDestroy(() => {
      resized.disconnect();
      this.map.remove();
    });

    afterRenderEffect(() => this.draw(this.assets(), this.selected()));
    afterRenderEffect(() => {
      const id = this.focus()?.id;
      const marker = id && untracked(() => this.markers.get(id));
      if (marker) {
        this.map.setView(marker.getLatLng(), Math.max(this.map.getZoom(), 14));
      }
    });
  }

  private draw(assets: LiveAsset[], selected: string | null): void {
    const shown = assets.filter(onMap);
    for (const asset of shown) {
      const position = L.latLng(asset.latest!.latitude, asset.latest!.longitude);
      const html = markerElement(asset, asset.id === selected);
      const icon = L.divIcon({ className: '', iconSize: [22, 22], iconAnchor: [11, 11], html });
      // Leaflet sets title as the marker's title attribute, so the Asset's name stays text.
      const title = `${asset.name} · ${statusLabels[asset.status]}`;
      const marker = this.markers.get(asset.id);
      if (marker) {
        marker.setLatLng(position);
        // Replace the icon only when its face or title changed, so a moving marker keeps keyboard focus.
        const face = (marker.getIcon() as L.DivIcon).options.html as HTMLElement;
        if (marker.options.title !== title || face.outerHTML !== html.outerHTML) {
          marker.options.title = title;
          marker.setIcon(icon);
        }
      } else {
        const created = L.marker(position, { icon, title, keyboard: true }).on('click', () => this.select.emit(asset.id));
        this.markers.set(asset.id, created.addTo(this.map));
      }
    }

    const ids = new Set(shown.map((a) => a.id));
    for (const [id, marker] of this.markers) {
      if (!ids.has(id)) {
        marker.remove();
        this.markers.delete(id);
      }
    }

    this.fitOnce();
  }

  /** Frames the fleet the first time there is something to show on a visible map; then the user's view stays. */
  private fitOnce(): void {
    if (this.fitted || this.markers.size === 0 || this.host.clientWidth === 0) {
      return;
    }

    const positions = [...this.markers.values()].map((m) => m.getLatLng());
    this.map.fitBounds(L.latLngBounds(positions), { padding: [24, 24], maxZoom: 14 });
    this.fitted = true;
  }
}
