import { markerElement } from './fleet-map';
import { LiveAsset } from './live';

const asset = (status: LiveAsset['status'], type: LiveAsset['type'] = 'Excavator'): LiveAsset => ({
  id: 'a',
  name: 'Excavator 202',
  type,
  assetGroupId: null,
  status,
  lastReceivedAt: null,
  latest: null,
});

describe('markerElement', () => {
  it('draws Asset Status as shape and colour, with the kind letter inside', () => {
    expect(markerElement(asset('Normal'), false).className).toBe('mk mk-normal');
    expect(markerElement(asset('Alerting'), false).className).toBe('mk mk-alerting');
    expect(markerElement(asset('Offline'), false).className).toBe('mk mk-offline');
    expect(markerElement(asset('Normal', 'Generator'), false).textContent).toBe('G');
  });

  it('highlights the selected Asset', () => {
    expect(markerElement(asset('Normal'), true).classList).toContain('mk-sel');
  });
});
