import { AlertRule, AssetStatus, AssetType, LiveAsset, Reading, Sensor } from './live';

/** The Asset list's groups, top to bottom: what needs attention first (Dashboard UX, #20). */
const groupOrder: AssetStatus[] = ['Alerting', 'Offline', 'NoDevice', 'Normal'];

export const statusLabels: Record<AssetStatus, string> = {
  Alerting: 'Alerting',
  Offline: 'Offline',
  NoDevice: 'No device',
  Normal: 'Normal',
};

/** Assets of one Asset Status. */
export interface StatusGroup {
  status: AssetStatus;
  label: string;
  assets: LiveAsset[];
}

/** The Assets grouped by Asset Status in list order, each group by name; empty groups are left out. */
export function groupByStatus(assets: readonly LiveAsset[]): StatusGroup[] {
  return groupOrder
    .map((status) => ({
      status,
      label: statusLabels[status],
      assets: assets.filter((a) => a.status === status).sort((a, b) => a.name.localeCompare(b.name)),
    }))
    .filter((group) => group.assets.length > 0);
}

/** Whether the Asset has a marker: only Assets with a Device and a position (No device Assets are listed only). */
export function onMap(asset: LiveAsset): boolean {
  return asset.status !== 'NoDevice' && asset.latest !== null;
}

/** The letter inside a marker that tells the Asset Type apart. */
export function kindLetter(type: AssetType): string {
  return type[0];
}

const sensorValue: Record<Sensor, (reading: Reading) => number | null> = {
  SpeedKph: (r) => r.speedKph,
  EngineTempC: (r) => r.engineTempC,
  FuelPct: (r) => r.fuelPct,
  VibrationMmS: (r) => r.vibrationMmS,
  BatteryV: (r) => r.batteryV,
};

/**
 * The sensors whose latest value is past an enabled Threshold rule covering the Asset (by Asset or Asset Group): the
 * values the list highlights. The threshold alone decides; hysteresis only delays clearing an Alert.
 */
export function breachedSensors(asset: LiveAsset, rules: readonly AlertRule[]): Set<Sensor> {
  const breached = new Set<Sensor>();
  for (const rule of rules) {
    const covers = rule.assetId === asset.id || (rule.assetGroupId !== null && rule.assetGroupId === asset.assetGroupId);
    if (!rule.enabled || !covers || rule.kind !== 'Threshold' || !rule.sensor || rule.threshold === null || !asset.latest) {
      continue;
    }

    const value = sensorValue[rule.sensor](asset.latest);
    if (value !== null && (rule.operator === 'GreaterThan' ? value > rule.threshold : value < rule.threshold)) {
      breached.add(rule.sensor);
    }
  }

  return breached;
}

/** How long ago the Device was last heard from, for the list's "Last seen" column. */
export function lastSeen(receivedAt: string | null, now: number): string {
  if (receivedAt === null) {
    return '—';
  }

  const seconds = Math.max(0, Math.round((now - Date.parse(receivedAt)) / 1000));
  if (seconds < 60) return `${seconds} s ago`;
  if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
  if (seconds < 86_400) return `${Math.round(seconds / 3600)} h ago`;
  return `${Math.round(seconds / 86_400)} d ago`;
}
