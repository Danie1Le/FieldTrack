import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Asset, Attachment, Device } from './api';
import { DevicesPage } from './devices-page';
import { page } from './testing';

const device = (id: string, serialNumber: string, lastReceivedAt: string | null = null): Device => ({ id, serialNumber, archived: false, lastReceivedAt });

const asset = (id: string, name: string, archived = false): Asset => ({ id, name, type: 'Truck', assetGroupId: null, archived });

const attachment = (id: string, deviceId: string, assetId: string, attachedAt: string, detachedAt: string | null = null): Attachment => ({
  id,
  deviceId,
  assetId,
  attachedAt,
  detachedAt,
});

describe('DevicesPage', () => {
  let fixture: ComponentFixture<DevicesPage>;
  let api: HttpTestingController;
  let view: ReturnType<typeof page>;

  const assets = [asset('truck7', 'Haul truck 7'), asset('truck8', 'Haul truck 8'), asset('old', 'Old truck', true)];
  const history = [
    attachment('now', 'trk7', 'truck7', '2026-11-17T09:00:00Z'),
    attachment('before', 'trk7', 'old', '2026-11-10T08:00:00Z', '2026-11-16T17:30:00Z'),
  ];

  beforeEach(async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(DevicesPage);
    view = page(fixture);
    await serve([device('trk7', 'TRK-007', '2026-11-17T10:00:00Z'), device('trk9', 'TRK-009')]);
  });

  afterEach(() => api.verify());

  /** Answers the page's reads, then lets the answers render. */
  async function serve(devices: Device[], attachments: Attachment[] = history) {
    await vi.waitFor(() => api.expectOne('/api/devices').flush(devices));
    api.expectOne('/api/assets?includeArchived=true').flush(assets);
    api.expectOne('/api/attachments').flush(attachments);
    await view.settle();
  }

  it('lists the Devices with the Asset each one is attached to', () => {
    expect(view.rows('[aria-label="Devices"] tbody tr').map((row) => row.slice(0, 2))).toEqual([
      ['TRK-007', 'Haul truck 7'],
      ['TRK-009', 'Not attached'],
    ]);
  });

  it('registers a Device by serial number', async () => {
    view.button('Register Device').click();
    await view.settle();
    view.fill('serialNumber', 'TRK-010');
    view.button('Save').click();

    const create = api.expectOne({ method: 'POST', url: '/api/devices' });
    expect(create.request.body).toEqual({ serialNumber: 'TRK-010' });
    create.flush(device('trk10', 'TRK-010'));
    await serve([device('trk10', 'TRK-010')]);
  });

  it('attaches a Device to an Asset in use that has no Device', async () => {
    view.button('Attach', 'TRK-009').click();
    await view.settle();
    const choices = [...view.root.querySelectorAll('select[name="assetId"] option')].map((option) => view.text(option));
    expect(choices).toEqual(['Haul truck 8']);

    view.fill('assetId', 'truck8');
    view.button('Save').click();
    const attach = api.expectOne({ method: 'POST', url: '/api/attachments' });
    expect(attach.request.body).toEqual({ deviceId: 'trk9', assetId: 'truck8' });
    attach.flush(attachment('new', 'trk9', 'truck8', '2026-11-17T11:00:00Z'));
    await serve([device('trk9', 'TRK-009')]);
  });

  it('detaches a Device only once the Admin confirms it in the page', async () => {
    view.button('Detach', 'TRK-007').click();
    await view.settle();
    api.expectNone({ method: 'POST' });
    expect(view.text(view.root.querySelector('[role="group"]'))).toContain('Device detached');

    view.button('Yes, detach').click();
    api.expectOne({ method: 'POST', url: '/api/attachments/now/detach' }).flush(attachment('now', 'trk7', 'truck7', '2026-11-17T09:00:00Z', '2026-11-17T11:00:00Z'));
    await serve([device('trk7', 'TRK-007')]);
  });

  it("shows a Device's Attachment history, newest first, naming Archived Assets too", async () => {
    view.button('History', 'TRK-007').click();
    await view.settle();

    const rows = view.rows('[aria-label="Attachment history"] tbody tr');
    expect(rows.map((row) => row[0])).toEqual(['Haul truck 7', 'Old truck']);
    expect(rows[0][2]).toBe('Still attached');
  });

  it('deletes a Device only once the Admin confirms it in the page', async () => {
    view.button('Delete', 'TRK-009').click();
    await view.settle();
    api.expectNone({ method: 'DELETE' });
    expect(view.text(view.root.querySelector('[role="group"]'))).toContain('Archived instead');

    view.button('Yes, delete').click();
    api.expectOne({ method: 'DELETE', url: '/api/devices/trk9' }).flush(null, { status: 204, statusText: 'No Content' });
    await serve([device('trk7', 'TRK-007')]);
  });

  it('shows why the API refused a change, as text', async () => {
    view.button('Register Device').click();
    await view.settle();
    view.fill('serialNumber', 'TRK-007');
    view.button('Save').click();
    api.expectOne({ method: 'POST', url: '/api/devices' }).flush(
      { detail: 'A Device with serial number TRK-007 is already registered.' },
      { status: 409, statusText: 'Conflict' },
    );
    await view.settle();

    expect(view.text(view.root.querySelector('[role="alert"]'))).toBe('A Device with serial number TRK-007 is already registered.');
  });
});
