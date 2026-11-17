/** Shapes the API sends to the Control room: `GET /assets/live`, the hub's `AssetsUpdated`, `GET /alert-rules`. */

/** Asset Status (GLOSSARY): No device > Alerting > Offline > Normal, computed by the API. */
export type AssetStatus = 'NoDevice' | 'Alerting' | 'Offline' | 'Normal';

export type AssetType = 'Truck' | 'Excavator' | 'Generator' | 'Sensor';

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

/** An Alert Rule (`AlertRuleResponse`); the Control room reads Threshold rules to highlight out-of-range values. */
export interface AlertRule {
  id: string;
  name: string;
  kind: string;
  assetId: string | null;
  assetGroupId: string | null;
  severity: string;
  enabled: boolean;
  sensor: Sensor | null;
  operator: 'GreaterThan' | 'LessThan' | null;
  threshold: number | null;
  hysteresis: number | null;
}
