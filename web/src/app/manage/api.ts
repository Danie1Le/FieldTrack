/** Shapes the management screens read and send, besides those the Control room already uses (`live.ts`). */

import { AssetType } from '../control-room/live';

/** An Asset (`AssetResponse`). Lists leave out Archived ones unless asked. */
export interface Asset {
  id: string;
  name: string;
  type: AssetType;
  assetGroupId: string | null;
  archived: boolean;
}
