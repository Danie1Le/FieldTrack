import { TestBed } from '@angular/core/testing';
import * as L from 'leaflet';
import { FleetMap, markerElement } from './fleet-map';
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

describe('FleetMap', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('centres on an Asset each time it is selected, also when it already was (after a pan)', async () => {
    vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
    const setView = vi.spyOn(L.Map.prototype, 'setView');
    const fixture = TestBed.createComponent(FleetMap);
    const latest = { latitude: 51.5, longitude: -0.12 } as LiveAsset['latest'];
    fixture.componentRef.setInput('assets', [{ ...asset('Normal'), latest }]);
    const centredOnAsset = () => setView.mock.calls.filter(([at]) => L.latLng(at).equals([51.5, -0.12])).length;

    fixture.componentRef.setInput('focus', { id: 'a' });
    await fixture.whenStable();
    fixture.componentRef.setInput('focus', { id: 'a' });
    await fixture.whenStable();

    expect(centredOnAsset()).toBe(2);
  });
});
