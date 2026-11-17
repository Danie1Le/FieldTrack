/** Shapes the management screens read and send, besides those the Control room already uses (`live.ts`). */

import { AlertRule, AssetType } from '../control-room/live';

/** An Asset (`AssetResponse`). Lists leave out Archived ones unless asked. */
export interface Asset {
  id: string;
  name: string;
  type: AssetType;
  assetGroupId: string | null;
  archived: boolean;
}

/** A Device (`DeviceResponse`). Lists leave out Archived ones unless asked. */
export interface Device {
  id: string;
  serialNumber: string;
  archived: boolean;
  /** Received time of its latest stored Readings, or null before any. */
  lastReceivedAt: string | null;
}

/** An Attachment (`AttachmentResponse`): a Device fitted to an Asset, open while `detachedAt` is null. */
export interface Attachment {
  id: string;
  deviceId: string;
  assetId: string;
  attachedAt: string;
  detachedAt: string | null;
}

/** An Alert Rule as the management screen reads it (`AlertRuleResponse`), with the Offline setting the Control room skips. */
export type ManagedRule = AlertRule & {
  /** Offline rules: how many seconds without a Reading raise the Alert. */
  offlineSeconds: number | null;
};
