import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, input, output } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { AuthService } from '../auth/auth';
import { AlertRule, GeoJsonPolygon, Geofence } from '../control-room/live';
import { GeofenceDraft, GeofenceEditor } from './geofence-editor';
import { GeofencesPage } from './geofences-page';

/** Stands in for the Leaflet-Geoman map: a test emits what drawing and editing on the map would. */
@Component({ selector: 'app-geofence-editor', template: '' })
class FakeEditor {
  readonly geofences = input<Geofence[]>([]);
  readonly draft = input<GeofenceDraft | null>(null);
  readonly kind = input<Geofence['kind']>('Inclusion');
  readonly drawn = output<GeoJsonPolygon>();
  readonly changed = output<GeoJsonPolygon>();
}

const square = (lon: number): GeoJsonPolygon => ({
  type: 'Polygon',
  coordinates: [[[lon, 51.5], [lon + 0.01, 51.5], [lon + 0.01, 51.51], [lon, 51.51], [lon, 51.5]]],
});

const geofence = (id: string, name: string, kind: Geofence['kind'] = 'Inclusion'): Geofence => ({ id, name, kind, area: square(-0.13) });

const rule = (name: string, geofenceId: string | null): AlertRule => ({
  id: `rule-${name}`,
  name,
  kind: geofenceId ? 'GeofenceExit' : 'Threshold',
  assetId: 'truck-1',
  assetGroupId: null,
  severity: 'Warning',
  enabled: true,
  sensor: null,
  operator: null,
  threshold: null,
  hysteresis: null,
  geofenceId,
});

describe('GeofencesPage', () => {
  let fixture: ComponentFixture<GeofencesPage>;
  let api: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), { provide: AuthService, useValue: {} }],
    });
    TestBed.overrideComponent(GeofencesPage, { remove: { imports: [GeofenceEditor] }, add: { imports: [FakeEditor] } });
    api = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(GeofencesPage);
    await serve([geofence('depot', 'Depot'), geofence('quarry', 'Quarry', 'Exclusion')], [rule('Left depot', 'depot'), rule('Overheat', null)]);
  });

  afterEach(() => {
    api.verify();
    vi.restoreAllMocks();
  });

  const page = () => fixture.nativeElement as HTMLElement;
  const editor = () => fixture.debugElement.query(By.directive(FakeEditor)).componentInstance as FakeEditor;
  const text = (element: Element | null) => element?.textContent!.trim();
  const items = () => [...page().querySelectorAll('[data-geofence]')].map((item) => ['.name', '.kind', '.rules'].map((part) => text(item.querySelector(part))));
  const button = (label: string) => [...page().querySelectorAll('button')].find((b) => text(b) === label)!;
  const select = (id: string) => page().querySelector<HTMLButtonElement>(`[data-geofence="${id}"] button`)!.click();

  /** Answers the page's reads, then lets the answers render. */
  async function serve(geofences: Geofence[], rules: AlertRule[] = []) {
    await vi.waitFor(() => api.expectOne('/api/geofences').flush(geofences));
    api.expectOne('/api/alert-rules').flush(rules);
    await settle();
  }

  async function settle() {
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();
  }

  function type(label: string, value: string) {
    const field = page().querySelector<HTMLInputElement>(`input[name="${label}"]`)!;
    field.value = value;
    field.dispatchEvent(new Event('input'));
  }

  it('lists the Geofences with the Alert Rules that name them', () => {
    expect(items()).toEqual([
      ['Depot', 'Inclusion', 'Left depot'],
      ['Quarry', 'Exclusion', 'No Alert Rule names it'],
    ]);
  });

  it('creates a Geofence from a drawn area with its name and kind', async () => {
    editor().drawn.emit(square(1));
    await settle();
    type('name', 'Yard');
    page().querySelector<HTMLInputElement>('input[value="Exclusion"]')!.click();
    button('Save').click();

    const create = api.expectOne({ method: 'POST', url: '/api/geofences' });
    expect(create.request.body).toEqual({ name: 'Yard', kind: 'Exclusion', area: square(1) });
    create.flush(geofence('yard', 'Yard', 'Exclusion'));
    await serve([geofence('depot', 'Depot'), geofence('yard', 'Yard', 'Exclusion')]);

    expect(items()).toContainEqual(['Yard', 'Exclusion', 'No Alert Rule names it']);
    expect(page().querySelector('form')).toBeNull();
  });

  it('saves a new area and name for the selected Geofence', async () => {
    select('depot');
    await settle();
    expect(editor().draft()).toEqual({ id: 'depot', area: square(-0.13) });

    editor().changed.emit(square(2));
    type('name', 'Main depot');
    button('Save').click();

    const edit = api.expectOne({ method: 'PUT', url: '/api/geofences/depot' });
    expect(edit.request.body).toEqual({ name: 'Main depot', kind: 'Inclusion', area: square(2) });
    edit.flush(geofence('depot', 'Main depot'));
    await serve([geofence('depot', 'Main depot')]);
  });

  it('shows why the API refused a change, as text', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    select('depot');
    await settle();

    button('Delete').click();
    const detail = '<b>An Alert Rule names the Geofence; delete or change that rule first.</b>';
    api.expectOne({ method: 'DELETE', url: '/api/geofences/depot' }).flush({ detail }, { status: 409, statusText: 'Conflict' });
    await settle();

    const shown = page().querySelector('[role="alert"]')!;
    expect(text(shown)).toBe(detail);
    expect(shown.querySelector('b')).toBeNull();
  });

  it('shows the API validation messages for a refused area', async () => {
    editor().drawn.emit(square(1));
    await settle();
    type('name', 'Yard');
    button('Save').click();

    api.expectOne({ method: 'POST', url: '/api/geofences' }).flush(
      { errors: { Area: ['The area may have at most 1000 positions.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle();

    expect(text(page().querySelector('[role="alert"]'))).toBe('The area may have at most 1000 positions.');
    expect(page().querySelector('form')).not.toBeNull();
  });
});
