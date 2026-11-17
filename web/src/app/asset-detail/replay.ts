import { AlertRule, Sensor } from '../control-room/live';

/**
 * The Reading the replay shows at a time: the last one at or before it (the first one before the replay starts), or
 * -1 without Readings. Readings come ordered by Device time, so this is a binary search.
 */
export function positionAt(readings: readonly { deviceTime: string }[], time: number): number {
  let [low, high] = [0, readings.length - 1];
  while (low < high) {
    const middle = Math.ceil((low + high) / 2);
    if (Date.parse(readings[middle].deviceTime) <= time) {
      low = middle;
    } else {
      high = middle - 1;
    }
  }

  return high;
}

/**
 * At most `max` evenly spaced items of `items[0..upTo]`, always ending with `items[upTo]` (default the last). A screen
 * cannot show more points than that, and projecting 100,000 of them on every replay tick would cost most of a tick.
 * The spacing depends on the whole list, so a route and the trail driven along it share their vertices.
 */
export function thin<T>(items: readonly T[], max: number, upTo = items.length - 1): T[] {
  const stride = Math.max(1, Math.ceil(items.length / max));
  const thinned: T[] = [];
  for (let i = 0; i < upTo; i += stride) {
    thinned.push(items[i]);
  }

  if (upTo >= 0) {
    thinned.push(items[upTo]);
  }

  return thinned;
}

/** One Threshold rule drawn across a sensor chart: where it breaches, and where an Alert clears again. */
export interface RuleLine {
  name: string;
  operator: 'GreaterThan' | 'LessThan';
  threshold: number;
  clear: number;
}

/**
 * The lines of every enabled Threshold rule on the sensor that covers the Asset (by Asset or Asset Group). Hysteresis
 * is a deadband (#10): `>` clears below threshold minus Hysteresis, `<` above threshold plus Hysteresis.
 */
export function ruleLines(
  rules: readonly AlertRule[],
  asset: { id: string; assetGroupId: string | null },
  sensor: Sensor,
): RuleLine[] {
  return rules
    .filter(
      (rule) =>
        rule.enabled &&
        rule.kind === 'Threshold' &&
        rule.sensor === sensor &&
        (rule.assetId === asset.id || (rule.assetGroupId !== null && rule.assetGroupId === asset.assetGroupId)),
    )
    .map((rule) => {
      const [operator, threshold, hysteresis] = [rule.operator!, rule.threshold!, rule.hysteresis ?? 0];
      return {
        name: rule.name,
        operator,
        threshold,
        clear: operator === 'GreaterThan' ? threshold - hysteresis : threshold + hysteresis,
      };
    });
}
