import * as L from 'leaflet';
import { polygonOf } from './geofence-editor';

describe('polygonOf', () => {
  it('wraps a polygon drawn on a repeated copy of the world into -180..180', () => {
    const east = L.polygon([[-17.7, 181], [-17.7, 183], [-18.2, 183]]);

    const longitudes = polygonOf(east).coordinates[0].map(([lon]) => lon);

    expect(longitudes).toEqual([-179, -177, -177, -179]);
  });

  it('leaves a polygon inside the world as it is, longitude first and the ring closed', () => {
    const { coordinates } = polygonOf(L.polygon([[51.5, -0.13], [51.5, -0.12], [51.51, -0.12]]));

    expect(coordinates[0][0]).toEqual([-0.13, 51.5]);
    expect(coordinates[0].at(-1)).toEqual(coordinates[0][0]);
  });
});
