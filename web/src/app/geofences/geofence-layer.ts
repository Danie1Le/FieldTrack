import * as L from 'leaflet';
import { Geofence } from '../control-room/live';

/** The outline pattern that tells a kind without colour: solid for an Inclusion, dashed for an Exclusion. */
export const dashOf = (kind: Geofence['kind']) => (kind === 'Exclusion' ? '8 6' : undefined);

/**
 * A Geofence as a map shape (spec #21 story 59): an Inclusion's outline is solid, an Exclusion's dashed, so the kind
 * reads without colour. Its name shows on hover. Colours live in styles.css (`.gf`), where the palette is.
 */
export function geofenceLayer(geofence: Geofence): L.Polygon {
  const name = document.createElement('span');
  // Text, not HTML: the name is whatever an Admin typed.
  name.textContent = geofence.name;
  return L.polygon(L.GeoJSON.coordsToLatLngs(geofence.area.coordinates, 1), {
    className: 'gf',
    weight: 2,
    dashArray: dashOf(geofence.kind),
  }).bindTooltip(name, { sticky: true });
}
