import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AssetGroup } from '../control-room/live';
import { Asset } from './api';
import { AssetsPage } from './assets-page';
import { page } from './testing';

const asset = (id: string, name: string, type: Asset['type'], assetGroupId: string | null = null): Asset => ({
  id,
  name,
  type,
  assetGroupId,
  archived: false,
});

const group = (id: string, name: string): AssetGroup => ({ id, name });

describe('AssetsPage', () => {
  let fixture: ComponentFixture<AssetsPage>;
  let api: HttpTestingController;
  let view: ReturnType<typeof page>;

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AssetsPage);
    view = page(fixture);
    await serve([asset('truck', 'Haul truck 7', 'Truck', 'depot'), asset('gen', 'Generator 1', 'Generator')], [group('depot', 'Depot'), group('quarry', 'Quarry')]);
  });

  afterEach(() => api.verify());

  /** Answers the page's reads, then lets the answers render. */
  async function serve(assets: Asset[], groups: AssetGroup[]) {
    await vi.waitFor(() => api.expectOne('/api/assets').flush(assets));
    api.expectOne('/api/asset-groups').flush(groups);
    await view.settle();
  }

  const rows = (section: string) => view.rows(`[aria-label="${section}"] tbody tr`);

  it('lists the Assets with their type and Asset Group, and the groups with how many Assets they hold', () => {
    expect(rows('Assets')).toEqual([
      ['Haul truck 7', 'Truck', 'Depot'],
      ['Generator 1', 'Generator', '—'],
    ]);
    expect(rows('Asset Groups')).toEqual([
      ['Depot', '1'],
      ['Quarry', '0'],
    ]);
  });

  it('creates an Asset with its name, type and Asset Group', async () => {
    view.button('New Asset').click();
    await view.settle();
    view.fill('name', 'Excavator 2');
    view.fill('type', 'Excavator');
    view.fill('assetGroupId', 'quarry');
    view.button('Save').click();

    const create = api.expectOne({ method: 'POST', url: '/api/assets' });
    expect(create.request.body).toEqual({ name: 'Excavator 2', type: 'Excavator', assetGroupId: 'quarry' });
    create.flush(asset('exc', 'Excavator 2', 'Excavator', 'quarry'));
    await serve([asset('exc', 'Excavator 2', 'Excavator', 'quarry')], [group('quarry', 'Quarry')]);

    expect(rows('Assets')).toEqual([['Excavator 2', 'Excavator', 'Quarry']]);
    expect(view.root.querySelector('form')).toBeNull();
  });

  it('edits an Asset, taking it out of its Asset Group', async () => {
    view.button('Edit', 'Haul truck 7').click();
    await view.settle();
    view.fill('name', 'Haul truck 8');
    view.fill('assetGroupId', '');
    view.button('Save').click();

    const edit = api.expectOne({ method: 'PUT', url: '/api/assets/truck' });
    expect(edit.request.body).toEqual({ name: 'Haul truck 8', type: 'Truck', assetGroupId: null });
    edit.flush(asset('truck', 'Haul truck 8', 'Truck'));
    await serve([], []);
  });

  it('builds the form afresh from each Asset it opens, so unsaved choices never reach another Asset', async () => {
    view.button('Edit', 'Haul truck 7').click();
    await view.settle();
    view.fill('type', 'Excavator');
    view.fill('assetGroupId', 'quarry');
    view.button('Edit', 'Generator 1').click();
    await view.settle();
    view.button('Save').click();

    const edit = api.expectOne({ method: 'PUT', url: '/api/assets/gen' });
    expect(edit.request.body).toEqual({ name: 'Generator 1', type: 'Generator', assetGroupId: null });
    edit.flush(asset('gen', 'Generator 1', 'Generator'));
    await serve([asset('truck', 'Haul truck 7', 'Truck', 'depot')], [group('depot', 'Depot'), group('quarry', 'Quarry')]);

    view.button('Edit', 'Haul truck 7').click();
    await view.settle();
    view.fill('assetGroupId', 'quarry');
    view.button('Edit', 'Haul truck 7').click();
    await view.settle();
    view.button('Save').click();

    const again = api.expectOne({ method: 'PUT', url: '/api/assets/truck' });
    expect(again.request.body).toEqual({ name: 'Haul truck 7', type: 'Truck', assetGroupId: 'depot' });
    again.flush(asset('truck', 'Haul truck 7', 'Truck', 'depot'));
    await serve([], []);
  });

  it('deletes an Asset only once the Admin confirms it in the page', async () => {
    view.button('Delete', 'Haul truck 7').click();
    await view.settle();
    api.expectNone({ method: 'DELETE' });
    expect(view.text(view.root.querySelector('[role="group"]'))).toContain('Archived instead');

    view.button('Cancel').click();
    await view.settle();
    view.button('Delete', 'Haul truck 7').click();
    await view.settle();
    view.button('Yes, delete').click();

    api.expectOne({ method: 'DELETE', url: '/api/assets/truck' }).flush(null, { status: 204, statusText: 'No Content' });
    await serve([asset('gen', 'Generator 1', 'Generator')], []);
  });

  it('creates, renames and deletes Asset Groups', async () => {
    view.button('New Asset Group').click();
    await view.settle();
    view.fill('groupName', 'Yard');
    view.button('Save').click();
    const create = api.expectOne({ method: 'POST', url: '/api/asset-groups' });
    expect(create.request.body).toEqual({ name: 'Yard' });
    create.flush(group('yard', 'Yard'));
    await serve([], [group('depot', 'Depot'), group('quarry', 'Quarry')]);

    view.button('Rename', 'Quarry').click();
    await view.settle();
    view.fill('groupName', 'South quarry');
    view.button('Save').click();
    const rename = api.expectOne({ method: 'PUT', url: '/api/asset-groups/quarry' });
    expect(rename.request.body).toEqual({ name: 'South quarry' });
    rename.flush(group('quarry', 'South quarry'));
    await serve([], [group('quarry', 'South quarry')]);

    view.button('Delete', 'South quarry').click();
    await view.settle();
    view.button('Yes, delete').click();
    api.expectOne({ method: 'DELETE', url: '/api/asset-groups/quarry' }).flush(null, { status: 204, statusText: 'No Content' });
    await serve([], []);
  });

  it('shows why the API refused a change, as text', async () => {
    view.button('Delete', 'Depot').click();
    await view.settle();
    view.button('Yes, delete').click();
    const detail = '<b>Assets or Alert Rules name the Asset Group; move or change them first.</b>';
    api.expectOne({ method: 'DELETE', url: '/api/asset-groups/depot' }).flush({ detail }, { status: 409, statusText: 'Conflict' });
    await view.settle();

    const shown = view.root.querySelector('[role="alert"]')!;
    expect(view.text(shown)).toBe(detail);
    expect(shown.querySelector('b')).toBeNull();
  });
});
