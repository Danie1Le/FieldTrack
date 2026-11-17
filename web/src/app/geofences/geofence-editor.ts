import { Component, DestroyRef, ElementRef, afterRenderEffect, inject, input, output } from '@angular/core';
import type {} from '@geoman-io/leaflet-geoman-free';
import * as L from 'leaflet';
import { GeoJsonPolygon, Geofence } from '../control-room/live';
import { geofenceLayer } from './geofence-layer';

/** A Leaflet polygon as the API takes it; Leaflet writes GeoJSON longitude first, rings closed. */
const polygonOf = (layer: L.Polygon) => layer.toGeoJSON().geometry as GeoJsonPolygon;

/**
 * The Geofences page's map: every Geofence, and the Leaflet-Geoman toolbar to draw a new one. The Geofence being edited
 * is drawn with its vertices draggable in place of its saved shape. Geoman must be loaded before this is created (the
 * `/geofences` route does it): it equips only maps and layers created after it loads.
 */
@Component({
  selector: 'app-geofence-editor',
  template: '',
  styles: ':host { display: block; }',
})
export class GeofenceEditor {
  readonly geofences = input<Geofence[]>([]);
  /**
   * What is being edited: a saved Geofence (its id), or a just-drawn area (id null). A new object each time editing
   * starts; the area is where editing starts, later edits are only emitted.
   */
  readonly draft = input<{ id: string | null; area: GeoJsonPolygon } | null>(null);
  /** A new area was drawn with the toolbar. */
  readonly drawn = output<GeoJsonPolygon>();
  /** The draft's area was edited on the map. */
  readonly changed = output<GeoJsonPolygon>();

  private readonly host: HTMLElement = inject(ElementRef).nativeElement;
  private readonly map = L.map(this.host, { zoomControl: false }).setView([52.2, -1.1], 6);
  private readonly areas = L.layerGroup().addTo(this.map);
  private fitted = false;

  constructor() {
    L.control.zoom({ position: 'bottomleft' }).addTo(this.map);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19,
      attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
    }).addTo(this.map);
    const resized = new ResizeObserver(() => this.map.invalidateSize());
    resized.observe(this.host);
    inject(DestroyRef).onDestroy(() => {
      resized.disconnect();
      this.map.remove();
    });

    // The API refuses crossing edges too; Geoman stops them while drawing.
    this.map.pm.setGlobalOptions({ allowSelfIntersection: false });
    this.map.pm.addControls({
      position: 'topleft',
      drawMarker: false,
      drawCircleMarker: false,
      drawPolyline: false,
      drawCircle: false,
      drawText: false,
      editMode: false,
      dragMode: false,
      cutPolygon: false,
      removalMode: false,
      rotateMode: false,
    });
    this.map.on('pm:create', ({ layer }) => {
      // The page decides what is drawn: the new area comes back as the draft.
      layer.remove();
      this.drawn.emit(polygonOf(layer as L.Polygon));
    });

    afterRenderEffect(() => this.draw(this.geofences(), this.draft()));
  }

  private draw(geofences: Geofence[], draft: { id: string | null; area: GeoJsonPolygon } | null): void {
    this.map.invalidateSize();
    this.areas.clearLayers();
    geofences.filter((g) => g.id !== draft?.id).forEach((g) => this.areas.addLayer(geofenceLayer(g)));
    if (draft) {
      const editing = L.polygon(L.GeoJSON.coordsToLatLngs(draft.area.coordinates, 1), { className: 'gf gf-editing' });
      this.areas.addLayer(editing);
      editing.pm.enable({ allowSelfIntersection: false });
      editing.on('pm:edit', () => this.changed.emit(polygonOf(editing)));
      this.map.fitBounds(editing.getBounds(), { padding: [48, 48], maxZoom: 16 });
    } else if (!this.fitted && geofences.length > 0) {
      // Frame every Geofence once; after that the user's view stays.
      this.map.fitBounds(L.featureGroup(this.areas.getLayers()).getBounds(), { padding: [24, 24] });
      this.fitted = true;
    }
  }
}
