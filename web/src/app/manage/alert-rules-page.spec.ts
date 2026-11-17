import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AssetGroup, Geofence } from '../control-room/live';
import { AlertRulesPage } from './alert-rules-page';
import { Asset, ManagedRule } from './api';
import { page } from './testing';

const assets: Asset[] = [
  { id: 'truck7', name: 'Haul truck 7', type: 'Truck', assetGroupId: null, archived: false },
  { id: 'old', name: 'Old truck', type: 'Truck', assetGroupId: null, archived: true },
];
const groups: AssetGroup[] = [{ id: 'depot', name: 'Depot' }];
const geofences: Geofence[] = [{ id: 'yard', name: 'Yard', kind: 'Inclusion', area: { type: 'Polygon', coordinates: [] } }];

const none = { sensor: null, operator: null, threshold: null, hysteresis: null, geofenceId: null, offlineSeconds: null };

const overheat: ManagedRule = {
  id: 'hot',
  name: 'Engine overheat',
  kind: 'Threshold',
  assetId: 'truck7',
  assetGroupId: null,
  severity: 'Critical',
  enabled: true,
  ...none,
  sensor: 'EngineTempC',
  operator: 'GreaterThan',
  threshold: 105,
  hysteresis: 5,
};
const leftYard: ManagedRule = { ...none, id: 'exit', name: 'Left the yard', kind: 'GeofenceExit', assetId: null, assetGroupId: 'depot', severity: 'Warning', enabled: false, geofenceId: 'yard' };
const silent: ManagedRule = { ...none, id: 'quiet', name: 'Gone quiet', kind: 'Offline', assetId: 'old', assetGroupId: null, severity: 'Warning', enabled: true, offlineSeconds: 60 };

describe('AlertRulesPage', () => {
  let fixture: ComponentFixture<AlertRulesPage>;
  let api: HttpTestingController;
  let view: ReturnType<typeof page>;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AlertRulesPage);
    view = page(fixture);
    await serve([overheat, leftYard, silent]);
  });

  afterEach(() => api.verify());

  /** Answers the page's reads, then lets the answers render. */
  async function serve(rules: ManagedRule[]) {
    await vi.waitFor(() => api.expectOne('/api/alert-rules').flush(rules));
    api.expectOne('/api/assets?includeArchived=true').flush(assets);
    api.expectOne('/api/asset-groups').flush(groups);
    api.expectOne('/api/geofences').flush(geofences);
    await view.settle();
  }

  async function open(button: HTMLButtonElement) {
    button.click();
    await view.settle();
  }

  function save() {
    view.button('Save').click();
  }

  it('lists each rule with its kind, target, condition, Severity and whether it is enabled', () => {
    expect(view.rows('[aria-label="Alert Rules"] tbody tr')).toEqual([
      ['Engine overheat', 'Threshold', 'Haul truck 7', 'Engine temperature (°C) > 105, Hysteresis 5', 'Critical', 'Enabled'],
      ['Left the yard', 'Geofence exit', 'Asset Group Depot', 'Leaves Yard', 'Warning', 'Disabled'],
      ['Gone quiet', 'Offline', 'Old truck', 'No Reading for 60 s', 'Warning', 'Enabled'],
    ]);
  });

  it('creates a Threshold rule on an Asset', async () => {
    await open(view.button('New Alert Rule'));
    view.fill('name', 'Low fuel');
    view.fill('target', 'asset:truck7');
    view.fill('severity', 'Warning');
    view.fill('sensor', 'FuelPct');
    view.fill('operator', 'LessThan');
    view.fill('threshold', '10');
    view.fill('hysteresis', '2.5');
    save();

    const create = api.expectOne({ method: 'POST', url: '/api/alert-rules' });
    expect(create.request.body).toEqual({
      name: 'Low fuel',
      kind: 'Threshold',
      assetId: 'truck7',
      assetGroupId: null,
      severity: 'Warning',
      enabled: true,
      ...none,
      sensor: 'FuelPct',
      operator: 'LessThan',
      threshold: 10,
      hysteresis: 2.5,
    });
    create.flush({ ...overheat, id: 'fuel' });
    await serve([overheat]);
    expect(view.root.querySelector('form')).toBeNull();
  });

  it('offers only Assets in use as targets', async () => {
    await open(view.button('New Alert Rule'));

    const options = [...view.root.querySelectorAll('select[name="target"] option')].map((option) => view.text(option));
    expect(options).toEqual(['Haul truck 7', 'Depot']);
  });

  it('creates Geofence enter and Offline rules on an Asset Group, sending only their own settings', async () => {
    await open(view.button('New Alert Rule'));
    view.fill('name', 'Entered the yard');
    view.fill('kind', 'GeofenceEnter');
    await view.settle();
    view.fill('target', 'group:depot');
    view.fill('geofenceId', 'yard');
    save();
    const enter = api.expectOne({ method: 'POST', url: '/api/alert-rules' });
    expect(enter.request.body).toEqual({ name: 'Entered the yard', kind: 'GeofenceEnter', assetId: null, assetGroupId: 'depot', severity: 'Warning', enabled: true, ...none, geofenceId: 'yard' });
    enter.flush(leftYard);
    await serve([]);

    await open(view.button('New Alert Rule'));
    view.fill('name', 'Silent');
    view.fill('kind', 'Offline');
    await view.settle();
    view.fill('target', 'group:depot');
    view.fill('offlineSeconds', '30');
    save();
    const offline = api.expectOne({ method: 'POST', url: '/api/alert-rules' });
    expect(offline.request.body).toEqual({ name: 'Silent', kind: 'Offline', assetId: null, assetGroupId: 'depot', severity: 'Warning', enabled: true, ...none, offlineSeconds: 30 });
    offline.flush(silent);
    await serve([]);
  });

  it('edits a rule, keeping a target that has since been Archived', async () => {
    await open(view.button('Edit', 'Gone quiet'));
    expect((view.root.querySelector('select[name="target"]') as HTMLSelectElement).value).toBe('asset:old');
    view.fill('offlineSeconds', '90');
    save();

    const edit = api.expectOne({ method: 'PUT', url: '/api/alert-rules/quiet' });
    expect(edit.request.body).toEqual({ name: 'Gone quiet', kind: 'Offline', assetId: 'old', assetGroupId: null, severity: 'Warning', enabled: true, ...none, offlineSeconds: 90 });
    edit.flush({ ...silent, offlineSeconds: 90 });
    await serve([]);
  });

  it('builds the form afresh from each rule it opens, so unsaved edits never reach another rule of the same kind', async () => {
    const twin: ManagedRule = { ...overheat, id: 'warm', name: 'Engine warm' };
    view.button('Enable', 'Left the yard').click();
    api.expectOne({ method: 'PUT', url: '/api/alert-rules/exit' }).flush({ ...leftYard, enabled: true });
    await serve([overheat, twin]);

    await open(view.button('Edit', 'Engine overheat'));
    view.root.querySelector<HTMLInputElement>('[name="enabled"]')!.click();
    view.fill('target', 'group:depot');
    view.fill('severity', 'Warning');
    view.fill('hysteresis', '9');
    await open(view.button('Edit', 'Engine warm'));
    save();

    const edit = api.expectOne({ method: 'PUT', url: '/api/alert-rules/warm' });
    const { id: _, ...unchanged } = twin;
    expect(edit.request.body).toEqual(unchanged);
    edit.flush(twin);
    await serve([]);
  });

  it('enables a rule at once and disables one only once the Admin confirms it in the page', async () => {
    view.button('Enable', 'Left the yard').click();
    const enable = api.expectOne({ method: 'PUT', url: '/api/alert-rules/exit' });
    const { id: _, ...unchanged } = leftYard;
    expect(enable.request.body).toEqual({ ...unchanged, enabled: true });
    enable.flush({ ...leftYard, enabled: true });
    await serve([overheat]);

    await open(view.button('Disable', 'Engine overheat'));
    api.expectNone({ method: 'PUT' });
    expect(view.text(view.root.querySelector('[role="group"]'))).toContain('Rule disabled');
    view.button('Yes, disable').click();
    expect(api.expectOne({ method: 'PUT', url: '/api/alert-rules/hot' }).request.body.enabled).toBe(false);
  });

  it('deletes a rule only once the Admin confirms it, and shows why the API refused', async () => {
    await open(view.button('Delete', 'Engine overheat'));
    api.expectNone({ method: 'DELETE' });
    view.button('Yes, delete').click();

    api.expectOne({ method: 'DELETE', url: '/api/alert-rules/hot' }).flush(
      { detail: 'The Alert Rule has raised Alerts; disable it instead.' },
      { status: 409, statusText: 'Conflict' },
    );
    await serve([overheat, leftYard, silent]);
    expect(view.text(view.root.querySelector('[role="alert"]'))).toBe('The Alert Rule has raised Alerts; disable it instead.');
  });
});
