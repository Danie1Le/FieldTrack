import { Component, ElementRef, afterRenderEffect, inject, input, output, untracked } from '@angular/core';
import type {} from '@geoman-io/leaflet-geoman-free';
import * as L from 'leaflet';
import { baseMap } from '../base-map';
import { GeoJsonPolygon, Geofence } from '../control-room/live';
import { dashOf, geofenceLayer } from './geofence-layer';

/**
 * A Leaflet polygon as the API takes it; Leaflet writes GeoJSON longitude first, rings closed. Longitudes are wrapped
 * into -180..180: the map repeats the world, and Leaflet and Geoman do not wrap what is drawn on a copy.
 */
export function polygonOf(layer: L.Polygon): GeoJsonPolygon {
  const { coordinates } = layer.toGeoJSON().geometry as GeoJsonPolygon;
  return {
    type: 'Polygon',
    coordinates: coordinates.map((ring) => ring.map(([lon, lat]) => [Math.abs(lon) > 180 ? L.latLng(lat, lon).wrap().lng : lon, lat])),
  };
}

/** What is being edited: a saved Geofence (its id), or a just-drawn area (id null), and the area editing starts from. */
export interface GeofenceDraft {
  id: string | null;
  area: GeoJsonPolygon;
}

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
  /** A new object each time editing starts; its area is where editing starts, later edits are only emitted. */
  readonly draft = input<GeofenceDraft | null>(null);
  /** The draft's kind as the form has it, so the edited shape is outlined as it will be saved. */
  readonly kind = input<Geofence['kind']>('Inclusion');
  /** A new area was drawn with the toolbar. */
  readonly drawn = output<GeoJsonPolygon>();
  /** The draft's area was edited on the map. */
  readonly changed = output<GeoJsonPolygon>();

  private readonly host: HTMLElement = inject(ElementRef).nativeElement;
  private readonly map = baseMap(this.host);
  private editing: L.Polygon | null = null;
  private readonly areas = L.layerGroup().addTo(this.map);
  private fitted = false;

  constructor() {
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
    afterRenderEffect(() => this.editing?.setStyle({ dashArray: dashOf(this.kind()) }));
  }

  private draw(geofences: Geofence[], draft: GeofenceDraft | null): void {
    this.map.invalidateSize();
    this.areas.clearLayers();
    this.editing = null;
    geofences.filter((g) => g.id !== draft?.id).forEach((g) => this.areas.addLayer(geofenceLayer(g)));
    if (draft) {
      const editing = L.polygon(L.GeoJSON.coordsToLatLngs(draft.area.coordinates, 1), {
        className: 'gf',
        weight: 2,
        dashArray: dashOf(untracked(this.kind)),
      });
      this.editing = editing;
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
