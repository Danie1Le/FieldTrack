import { breachedSensors, groupByStatus, kindLetter, lastSeen, onMap } from './asset-status';
import { AlertRule, LiveAsset, Reading } from './live';

const reading = (sensors: Partial<Reading> = {}): Reading => ({
  deviceSerialNumber: 'FT-1',
  boot: 1,
  sequenceNumber: 1,
  deviceTime: '2026-11-17T10:00:00Z',
  receivedTime: '2026-11-17T10:00:01Z',
  latitude: 52.2,
  longitude: -1.1,
  speedKph: null,
  engineTempC: null,
  fuelPct: null,
  vibrationMmS: null,
  batteryV: null,
  ...sensors,
});

const asset = (name: string, status: LiveAsset['status'], changes: Partial<LiveAsset> = {}): LiveAsset => ({
  id: name,
  name,
  type: 'Truck',
  assetGroupId: null,
  status,
  lastReceivedAt: null,
  latest: reading(),
  ...changes,
});

describe('groupByStatus', () => {
  it('lists Alerting, Offline, No device, then Normal, each by name, leaving out empty groups', () => {
    const groups = groupByStatus([
      asset('Truck 2', 'Normal'),
      asset('Generator 1', 'NoDevice'),
      asset('Truck 1', 'Normal'),
      asset('Excavator 1', 'Alerting'),
    ]);

    expect(groups.map((g) => [g.label, g.assets.map((a) => a.name)])).toEqual([
      ['Alerting', ['Excavator 1']],
      ['No device', ['Generator 1']],
      ['Normal', ['Truck 1', 'Truck 2']],
    ]);
  });
});

describe('onMap', () => {
  it('shows Assets with a position and keeps No device Assets off the map', () => {
    expect(onMap(asset('a', 'Normal'))).toBe(true);
    expect(onMap(asset('b', 'Offline'))).toBe(true);
    expect(onMap(asset('c', 'Normal', { latest: null }))).toBe(false);
    // An Asset whose Device was detached keeps its last Reading, but it has no live position.
    expect(onMap(asset('d', 'NoDevice'))).toBe(false);
  });
});

describe('kindLetter', () => {
  it('marks each Asset Type with its initial', () => {
    expect(['Truck', 'Excavator', 'Generator', 'Sensor'].map((t) => kindLetter(t as LiveAsset['type']))).toEqual([
      'T',
      'E',
      'G',
      'S',
    ]);
  });
});

describe('breachedSensors', () => {
  const rule = (changes: Partial<AlertRule>): AlertRule => ({
    id: 'r',
    name: 'Engine overheat',
    kind: 'Threshold',
    assetId: null,
    assetGroupId: null,
    severity: 'Critical',
    enabled: true,
    sensor: 'EngineTempC',
    operator: 'GreaterThan',
    threshold: 105,
    hysteresis: 5,
    ...changes,
  });

  it('flags latest values past an enabled Threshold rule that covers the Asset, by Asset or Asset Group', () => {
    const truck = asset('t', 'Alerting', { assetGroupId: 'haulage', latest: reading({ engineTempC: 108.4, fuelPct: 9 }) });

    const breached = breachedSensors(truck, [
      rule({ assetId: 't' }),
      rule({ assetGroupId: 'haulage', sensor: 'FuelPct', operator: 'LessThan', threshold: 10 }),
    ]);

    expect([...breached].sort()).toEqual(['EngineTempC', 'FuelPct']);
  });

  it('ignores rules that are disabled, cover other Assets, or are not breached', () => {
    const truck = asset('t', 'Normal', { assetGroupId: 'haulage', latest: reading({ engineTempC: 108.4, fuelPct: 50 }) });

    const breached = breachedSensors(truck, [
      rule({ assetId: 't', enabled: false }),
      rule({ assetId: 'other' }),
      rule({ assetGroupId: 'other-group' }),
      rule({ assetId: 't', sensor: 'FuelPct', operator: 'LessThan', threshold: 10 }),
      rule({ assetId: 't', threshold: 110 }),
    ]);

    expect(breached.size).toBe(0);
  });

  it('flags nothing for a sensor the Device did not report', () => {
    expect(breachedSensors(asset('t', 'Normal'), [rule({ assetId: 't', operator: 'LessThan' })]).size).toBe(0);
  });
});

describe('lastSeen', () => {
  const now = Date.parse('2026-11-17T10:00:00Z');

  it('says how long ago the Device was heard from', () => {
    expect(lastSeen('2026-11-17T09:59:48Z', now)).toBe('12 s ago');
    expect(lastSeen('2026-11-17T09:55:00Z', now)).toBe('5 min ago');
    expect(lastSeen('2026-11-17T07:00:00Z', now)).toBe('3 h ago');
    expect(lastSeen('2026-11-14T10:00:00Z', now)).toBe('3 d ago');
    expect(lastSeen(null, now)).toBe('—');
  });
});
