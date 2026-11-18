import { AlertRule, Sensor, covers } from '../control-room/live';

/**
 * The most vertices the route or the trail is drawn with, and so the most Readings the page asks the history endpoint
 * for: it thins a busy range to this many server-side (#126). About a point per pixel of a wide chart, a minute apart
 * over 24 h; a narrower range shows every Reading. Each point costs the server a PostGIS conversion, and the API load
 * test (#47) holds this read to p95 < 150 ms.
 */
export const maxDrawn = 1500;

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

/** One Threshold rule drawn across a sensor chart: where it breaches, and where an Alert clears again. */
export interface RuleLine {
  name: string;
  operator: 'GreaterThan' | 'LessThan';
  threshold: number;
  clear: number;
}

/**
 * The lines of every enabled Threshold rule on the sensor that covers the Asset (by Asset or Asset Group). With
 * Hysteresis (#10), `>` clears below threshold minus Hysteresis, `<` above threshold plus Hysteresis.
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
        covers(rule, asset),
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
