import { DatePipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Asset, Attachment, Device } from './api';
import { Changes } from './changes';
import { ConfirmAction } from './confirm-action';
import { Editing } from './editing';

/**
 * Devices and their Attachments (Admins only, spec #21 stories 18-20, 22, 23, 53): register, attach to and detach
 * from Assets, see each Device's Attachment history, and Archive or delete.
 */
@Component({
  selector: 'app-devices-page',
  imports: [ConfirmAction, DatePipe],
  templateUrl: './devices-page.html',
  styleUrl: './manage.css',
})
export class DevicesPage {
  private readonly http = inject(HttpClient);
  protected readonly changes = new Changes(() => this.load());

  protected readonly devices = signal<Device[] | null>(null);
  /** Archived ones too, so history still names them. */
  private readonly assets = signal<Asset[]>([]);
  /** Every Attachment, newest first. */
  private readonly attachments = signal<Attachment[]>([]);
  protected readonly registering = signal(false);
  /** The Device being attached. */
  protected readonly attaching = new Editing<Device>();
  /** The Device whose Attachment history is shown. */
  protected readonly history = signal<Device | null>(null);

  /** Assets that can take a Device: in use, with none attached (one Device per Asset at a time). */
  protected readonly freeAssets = computed(() =>
    this.assets().filter((asset) => !asset.archived && !this.attachments().some((a) => a.assetId === asset.id && !a.detachedAt)),
  );

  constructor() {
    void this.load();
  }

  protected openAttachment(device: Device): Attachment | undefined {
    return this.attachments().find((attachment) => attachment.deviceId === device.id && !attachment.detachedAt);
  }

  protected historyOf(device: Device): Attachment[] {
    return this.attachments().filter((attachment) => attachment.deviceId === device.id);
  }

  protected assetName(id: string): string {
    return this.assets().find((asset) => asset.id === id)?.name ?? 'Unknown Asset';
  }

  protected register(form: HTMLFormElement): Promise<void> {
    const body = { serialNumber: new FormData(form).get('serialNumber') };
    return this.changes.send(this.http.post('/api/devices', body), () => this.done());
  }

  protected attach(form: HTMLFormElement): Promise<void> {
    const body = { deviceId: this.attaching.item()!.id, assetId: new FormData(form).get('assetId') };
    return this.changes.send(this.http.post('/api/attachments', body), () => this.done());
  }

  protected detach(attachment: Attachment): Promise<void> {
    return this.changes.send(this.http.post(`/api/attachments/${encodeURIComponent(attachment.id)}/detach`, null), () => this.done());
  }

  protected remove(device: Device): Promise<void> {
    return this.changes.send(this.http.delete(`/api/devices/${encodeURIComponent(device.id)}`), () => {
      // Deleted or Archived, the Device leaves the list, so its history panel goes too.
      if (this.history()?.id === device.id) {
        this.history.set(null);
      }

      return this.done();
    });
  }

  protected cancel(): void {
    this.registering.set(false);
    this.attaching.close();
    this.changes.problems.set([]);
  }

  private done(): Promise<void> {
    this.registering.set(false);
    this.attaching.close();
    return this.load();
  }

  private async load(): Promise<void> {
    try {
      const [devices, assets, attachments] = await Promise.all([
        firstValueFrom(this.http.get<Device[]>('/api/devices')),
        firstValueFrom(this.http.get<Asset[]>('/api/assets?includeArchived=true')),
        firstValueFrom(this.http.get<Attachment[]>('/api/attachments')),
      ]);
      this.devices.set(devices);
      this.assets.set(assets);
      this.attachments.set(attachments);
    } catch {
      this.changes.problems.set(['The Devices could not be loaded. Reload the page to try again.']);
    }
  }
}
