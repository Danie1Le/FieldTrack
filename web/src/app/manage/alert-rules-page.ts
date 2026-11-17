import { HttpClient } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AssetGroup, Geofence, Sensor } from '../control-room/live';
import { Asset, ManagedRule } from './api';
import { Changes } from './changes';
import { ConfirmAction } from './confirm-action';
import { Editing } from './editing';

type Kind = ManagedRule['kind'];

const kindLabels: Record<Kind, string> = {
  Threshold: 'Threshold',
  GeofenceExit: 'Geofence exit',
  GeofenceEnter: 'Geofence enter',
  Offline: 'Offline',
};

const sensorLabels: Record<Sensor, string> = {
  SpeedKph: 'Speed (km/h)',
  EngineTempC: 'Engine temperature (°C)',
  FuelPct: 'Fuel (%)',
  VibrationMmS: 'Vibration (mm/s)',
  BatteryV: 'Battery (V)',
};

/** A number field's value, or null when it is empty or not on the form (another kind's setting). */
const numberOf = (value: FormDataEntryValue | null) => (value === null || value === '' ? null : Number(value));

/**
 * Alert Rules (Admins only, spec #21 stories 29-33): create and edit Threshold, Geofence exit and enter, and Offline
 * rules on one Asset or an Asset Group, enable and disable them, and delete those that never raised an Alert.
 */
@Component({
  selector: 'app-alert-rules-page',
  imports: [ConfirmAction],
  templateUrl: './alert-rules-page.html',
  styleUrl: './manage.css',
})
export class AlertRulesPage {
  private readonly http = inject(HttpClient);
  protected readonly changes = new Changes(() => this.load());
  protected readonly kindLabels = kindLabels;
  protected readonly kinds = Object.keys(kindLabels) as Kind[];
  protected readonly sensorLabels = sensorLabels;
  protected readonly sensors = Object.keys(sensorLabels) as Sensor[];

  protected readonly rules = signal<ManagedRule[] | null>(null);
  /** Archived ones too, so a rule that targets one still names it. */
  private readonly assets = signal<Asset[]>([]);
  protected readonly groups = signal<AssetGroup[]>([]);
  protected readonly geofences = signal<Geofence[]>([]);
  /** The rule being edited, or 'new' while one is being created. */
  protected readonly form = new Editing<ManagedRule | 'new'>();
  /** The form's kind, which decides which settings it asks for. */
  protected readonly kind = signal<Kind>('Threshold');

  constructor() {
    void this.load();
  }

  /** Assets a rule can target: those in use, plus the edited rule's own Asset should it have been Archived since. */
  protected targetAssets(rule: ManagedRule | null): Asset[] {
    return this.assets().filter((asset) => !asset.archived || asset.id === rule?.assetId);
  }

  protected target(rule: ManagedRule): string {
    return rule.assetId
      ? (this.assets().find((asset) => asset.id === rule.assetId)?.name ?? 'Unknown Asset')
      : `Asset Group ${this.groups().find((group) => group.id === rule.assetGroupId)?.name ?? '?'}`;
  }

  protected condition(rule: ManagedRule): string {
    switch (rule.kind) {
      case 'Threshold':
        return `${sensorLabels[rule.sensor!]} ${rule.operator === 'LessThan' ? '<' : '>'} ${rule.threshold}, Hysteresis ${rule.hysteresis}`;
      case 'Offline':
        return `No Reading for ${rule.offlineSeconds} s`;
      default: {
        const geofence = this.geofences().find((g) => g.id === rule.geofenceId)?.name ?? 'a Geofence';
        return `${rule.kind === 'GeofenceExit' ? 'Leaves' : 'Enters'} ${geofence}`;
      }
    }
  }

  protected open(rule: ManagedRule | 'new'): void {
    this.changes.problems.set([]);
    this.form.open(rule);
    this.kind.set(rule === 'new' ? 'Threshold' : rule.kind);
  }

  protected cancel(): void {
    this.form.close();
    this.changes.problems.set([]);
  }

  protected save(form: HTMLFormElement): Promise<void> {
    const values = new FormData(form);
    const [targetKind, targetId] = String(values.get('target')).split(':');
    // Only the chosen kind's settings are on the form, so every other one is sent as null.
    const body = {
      name: values.get('name'),
      kind: this.kind(),
      assetId: targetKind === 'asset' ? targetId : null,
      assetGroupId: targetKind === 'group' ? targetId : null,
      severity: values.get('severity'),
      enabled: values.get('enabled') === 'on',
      sensor: values.get('sensor'),
      operator: values.get('operator'),
      threshold: numberOf(values.get('threshold')),
      hysteresis: numberOf(values.get('hysteresis')),
      geofenceId: values.get('geofenceId'),
      offlineSeconds: numberOf(values.get('offlineSeconds')),
    };
    const editing = this.form.item();
    return this.changes.send(
      editing === 'new'
        ? this.http.post('/api/alert-rules', body)
        : this.http.put(`/api/alert-rules/${encodeURIComponent(editing!.id)}`, body),
      () => this.done(),
    );
  }

  protected setEnabled(rule: ManagedRule, enabled: boolean): Promise<void> {
    const { id, ...body } = rule;
    return this.changes.send(this.http.put(`/api/alert-rules/${encodeURIComponent(id)}`, { ...body, enabled }), () => this.done());
  }

  protected remove(rule: ManagedRule): Promise<void> {
    return this.changes.send(this.http.delete(`/api/alert-rules/${encodeURIComponent(rule.id)}`), () => this.done());
  }

  private done(): Promise<void> {
    this.form.close();
    return this.load();
  }

  private async load(): Promise<void> {
    try {
      const [rules, assets, groups, geofences] = await Promise.all([
        firstValueFrom(this.http.get<ManagedRule[]>('/api/alert-rules')),
        firstValueFrom(this.http.get<Asset[]>('/api/assets?includeArchived=true')),
        firstValueFrom(this.http.get<AssetGroup[]>('/api/asset-groups')),
        firstValueFrom(this.http.get<Geofence[]>('/api/geofences')),
      ]);
      this.rules.set(rules);
      this.assets.set(assets);
      this.groups.set(groups);
      this.geofences.set(geofences);
    } catch {
      this.changes.problems.set(['The Alert Rules could not be loaded. Reload the page to try again.']);
    }
  }
}
