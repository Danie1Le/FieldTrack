import { AlertRule } from '../control-room/live';
import { positionAt, ruleLines } from './replay';

const at = (...times: string[]) => times.map((deviceTime) => ({ deviceTime }));

describe('positionAt', () => {
  const readings = at('2026-11-17T10:00:00Z', '2026-11-17T10:00:10Z', '2026-11-17T10:00:20Z');

  it('is the last Reading at or before the time', () => {
    expect(positionAt(readings, Date.parse('2026-11-17T10:00:10Z'))).toBe(1);
    expect(positionAt(readings, Date.parse('2026-11-17T10:00:19Z'))).toBe(1);
    expect(positionAt(readings, Date.parse('2026-11-17T11:00:00Z'))).toBe(2);
  });

  it('is the first Reading before the replay starts, and none without Readings', () => {
    expect(positionAt(readings, Date.parse('2026-11-17T09:00:00Z'))).toBe(0);
    expect(positionAt([], 0)).toBe(-1);
  });
});

describe('ruleLines', () => {
  const rule = (changes: Partial<AlertRule>): AlertRule => ({
    id: 'r',
    name: 'Engine overheat',
    kind: 'Threshold',
    assetId: 'truck',
    assetGroupId: null,
    severity: 'Critical',
    enabled: true,
    sensor: 'EngineTempC',
    operator: 'GreaterThan',
    threshold: 105,
    hysteresis: 5,
    ...changes,
  });
  const truck = { id: 'truck', assetGroupId: 'depot' };

  it('gives the threshold and the clear point of each enabled Threshold rule on the sensor covering the Asset', () => {
    const lines = ruleLines(
      [
        rule({}),
        rule({ name: 'Cold engine', assetId: null, assetGroupId: 'depot', operator: 'LessThan', threshold: 40, hysteresis: 2 }),
        rule({ name: 'Disabled', enabled: false }),
        rule({ name: 'Other Asset', assetId: 'excavator' }),
        rule({ name: 'Other group', assetId: null, assetGroupId: 'quarry' }),
        rule({ name: 'Low fuel', sensor: 'FuelPct' }),
        rule({ name: 'Offline', kind: 'Offline', sensor: null, operator: null, threshold: null, hysteresis: null }),
      ],
      truck,
      'EngineTempC',
    );

    expect(lines).toEqual([
      { name: 'Engine overheat', operator: 'GreaterThan', threshold: 105, clear: 100 },
      { name: 'Cold engine', operator: 'LessThan', threshold: 40, clear: 42 },
    ]);
  });
});
