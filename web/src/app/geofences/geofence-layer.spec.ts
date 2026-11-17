import * as L from 'leaflet';
import { Geofence } from '../control-room/live';
import { geofenceLayer } from './geofence-layer';

const geofence = (kind: Geofence['kind'], name = 'Depot'): Geofence => ({
  id: 'geofence-1',
  name,
  kind,
  area: { type: 'Polygon', coordinates: [[[-0.13, 51.5], [-0.12, 51.5], [-0.12, 51.51], [-0.13, 51.5]]] },
});

describe('geofenceLayer', () => {
  it('draws an Inclusion solid and an Exclusion dashed', () => {
    expect(geofenceLayer(geofence('Inclusion')).options.dashArray).toBeUndefined();
    expect(geofenceLayer(geofence('Exclusion')).options.dashArray).toBeTruthy();
  });

  it('reads the area as GeoJSON, longitude first', () => {
    const [outer] = geofenceLayer(geofence('Inclusion')).getLatLngs() as L.LatLng[][];

    expect(outer[0].equals([51.5, -0.13])).toBe(true);
  });

  it('names the Geofence in its tooltip as text', () => {
    const name = '<img src=x onerror=alert(1)>';
    const content = geofenceLayer(geofence('Inclusion', name)).getTooltip()!.getContent() as HTMLElement;

    expect(content.textContent).toBe(name);
    expect(content.children).toHaveLength(0);
  });
});
