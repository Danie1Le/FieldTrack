/**
 * Shapes the API sends to the Control room: `GET /assets/live`, `GET /alerts`, `GET /alert-rules`, `GET /geofences`
 * and the hub's pushes.
 */

/** Asset Status (GLOSSARY): No device > Alerting > Offline > Normal, computed by the API. */
export type AssetStatus = 'NoDevice' | 'Alerting' | 'Offline' | 'Normal';

export const assetTypes = ['Truck', 'Excavator', 'Generator', 'Sensor'] as const;

export type AssetType = (typeof assetTypes)[number];

export type Sensor = 'SpeedKph' | 'EngineTempC' | 'FuelPct' | 'VibrationMmS' | 'BatteryV';

/** A stored Reading (`ReadingResponse`). */
export interface Reading {
  deviceSerialNumber: string;
  boot: number;
  sequenceNumber: number;
  deviceTime: string;
  receivedTime: string;
  latitude: number;
  longitude: number;
  speedKph: number | null;
  engineTempC: number | null;
  fuelPct: number | null;
  vibrationMmS: number | null;
  batteryV: number | null;
}

/** Each Sensor's value in a Reading; null when the Reading does not carry it. */
export const sensorValue: Record<Sensor, (reading: Reading) => number | null> = {
  SpeedKph: (r) => r.speedKph,
  EngineTempC: (r) => r.engineTempC,
  FuelPct: (r) => r.fuelPct,
  VibrationMmS: (r) => r.vibrationMmS,
  BatteryV: (r) => r.batteryV,
};

/** An Asset as the dashboard shows it live (`LiveAssetResponse`). */
export interface LiveAsset {
  id: string;
  name: string;
  type: AssetType;
  assetGroupId: string | null;
  status: AssetStatus;
  /** When the attached Device's latest Reading was received ("last seen"). */
  lastReceivedAt: string | null;
  /** The Reading with the latest Device time, from any Device the Asset has had. */
  latest: Reading | null;
}

/** An Asset Group (`AssetGroupResponse`). */
export interface AssetGroup {
  id: string;
  name: string;
}

/** Open and Acknowledged Alerts are Active; Resolved is final. */
export type AlertStatus = 'Open' | 'Acknowledged' | 'Resolved';

export type ResolveReason = 'Cleared' | 'Manual' | 'RuleChanged' | 'RuleDisabled' | 'AssetArchived' | 'DeviceDetached';

/** How a Resolve reason reads on screen; shared by the Alert feed and the Asset page. */
export const reasonLabels: Record<ResolveReason, string> = {
  Cleared: 'Cleared',
  Manual: 'Manual',
  RuleChanged: 'Rule changed',
  RuleDisabled: 'Rule disabled',
  AssetArchived: 'Asset archived',
  DeviceDetached: 'Device detached',
};

/** An Alert (`AlertResponse`), from `GET /alerts`, the Alert actions and the hub's `AlertRaised` / `AlertUpdated`. */
export interface Alert {
  id: string;
  alertRuleId: string;
  assetId: string;
  severity: 'Warning' | 'Critical';
  /** What happened, for people. */
  message: string;
  status: AlertStatus;
  /** Device time of the Reading that raised it. */
  raisedAt: string;
  resolvedAt: string | null;
  resolveReason: ResolveReason | null;
  /** Display names, never emails. */
  acknowledgedBy: string | null;
  acknowledgedAt: string | null;
  /** Null when the platform Resolved it. */
  resolvedBy: string | null;
  note: string | null;
  /**
   * The Latch. Active: the condition still holds, so Resolving by hand latches the rule. Resolved: a user Resolved it
   * while the condition held.
   */
  latched: boolean;
  /**
   * Its condition Cleared, by Device time, more than 10 s before the platform created it, so nobody could have seen it
   * while it held.
   */
  fromBacklog: boolean;
}

/** An Alert Rule (`AlertRuleResponse`); the Control room reads Threshold rules to highlight out-of-range values. */
export interface AlertRule {
  id: string;
  name: string;
  /** The API's `AlertRuleKind` names. */
  kind: 'Threshold' | 'Offline' | 'GeofenceExit' | 'GeofenceEnter';
  assetId: string | null;
  assetGroupId: string | null;
  severity: 'Warning' | 'Critical';
  enabled: boolean;
  sensor: Sensor | null;
  operator: 'GreaterThan' | 'LessThan' | null;
  threshold: number | null;
  hysteresis: number | null;
  /** The Geofence a Geofence rule watches. */
  geofenceId: string | null;
}

/** A GeoJSON Polygon (RFC 7946): rings of `[longitude, latitude]` positions, each closed, the outer ring first. */
export interface GeoJsonPolygon {
  type: 'Polygon';
  coordinates: number[][][];
}

/** A Geofence (`GeofenceResponse`): a named area that affects Assets only through an Alert Rule that names it. */
export interface Geofence {
  id: string;
  name: string;
  /** Inclusion: Assets are meant to stay inside; Exclusion: meant to stay out. The map draws Exclusion dashed. */
  kind: 'Inclusion' | 'Exclusion';
  area: GeoJsonPolygon;
}

/** Whether an Alert Rule covers the Asset: it names the Asset, or the Asset Group the Asset is in. */
export function covers(rule: AlertRule, asset: { id: string; assetGroupId: string | null }): boolean {
  return rule.assetId === asset.id || (rule.assetGroupId !== null && rule.assetGroupId === asset.assetGroupId);
}
