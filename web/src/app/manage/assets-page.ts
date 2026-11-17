import { HttpClient } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AssetGroup, assetTypes } from '../control-room/live';
import { Asset } from './api';
import { Changes } from './changes';
import { ConfirmAction } from './confirm-action';
import { Editing } from './editing';

/** Assets and Asset Groups (Admins only, spec #21 stories 17, 21-24): create, edit, and Archive or delete. */
@Component({
  selector: 'app-assets-page',
  imports: [ConfirmAction],
  templateUrl: './assets-page.html',
  styleUrl: './manage.css',
})
export class AssetsPage {
  private readonly http = inject(HttpClient);
  protected readonly changes = new Changes();
  protected readonly assetTypes = assetTypes;

  /** Archived ones too, so their Asset Groups count them. */
  private readonly assets = signal<Asset[] | null>(null);
  /** The Assets listed: those in use. */
  protected readonly inUse = computed(() => this.assets()?.filter((asset) => !asset.archived) ?? null);
  protected readonly groups = signal<AssetGroup[]>([]);
  /** The Asset being edited, or 'new' while one is being created. */
  protected readonly assetForm = new Editing<Asset | 'new'>();
  protected readonly groupForm = new Editing<AssetGroup | 'new'>();

  constructor() {
    void this.load();
  }

  protected groupName(id: string | null): string {
    return this.groups().find((group) => group.id === id)?.name ?? '—';
  }

  /** How many Assets the group holds; Archived ones are counted apart, as they too keep the group from being deleted. */
  protected assetsIn(group: AssetGroup): string {
    const members = (this.assets() ?? []).filter((asset) => asset.assetGroupId === group.id);
    const archived = members.filter((asset) => asset.archived).length;
    return `${members.length - archived}${archived ? ` (${archived} Archived)` : ''}`;
  }

  protected saveAsset(form: HTMLFormElement): Promise<void> {
    const values = new FormData(form);
    const body = { name: values.get('name'), type: values.get('type'), assetGroupId: values.get('assetGroupId') || null };
    const editing = this.assetForm.item();
    return this.changes.send(
      editing === 'new' ? this.http.post('/api/assets', body) : this.http.put(`/api/assets/${encodeURIComponent(editing!.id)}`, body),
      () => this.done(),
    );
  }

  protected removeAsset(asset: Asset): Promise<void> {
    return this.changes.send(this.http.delete(`/api/assets/${encodeURIComponent(asset.id)}`), () => this.done());
  }

  protected saveGroup(form: HTMLFormElement): Promise<void> {
    const body = { name: new FormData(form).get('groupName') };
    const editing = this.groupForm.item();
    return this.changes.send(
      editing === 'new'
        ? this.http.post('/api/asset-groups', body)
        : this.http.put(`/api/asset-groups/${encodeURIComponent(editing!.id)}`, body),
      () => this.done(),
    );
  }

  protected removeGroup(group: AssetGroup): Promise<void> {
    return this.changes.send(this.http.delete(`/api/asset-groups/${encodeURIComponent(group.id)}`), () => this.done());
  }

  protected cancel(): void {
    this.assetForm.close();
    this.groupForm.close();
    this.changes.problems.set([]);
  }

  private done(): Promise<void> {
    this.assetForm.close();
    this.groupForm.close();
    return this.load();
  }

  private async load(): Promise<void> {
    try {
      const [assets, groups] = await Promise.all([
        firstValueFrom(this.http.get<Asset[]>('/api/assets?includeArchived=true')),
        firstValueFrom(this.http.get<AssetGroup[]>('/api/asset-groups')),
      ]);
      this.assets.set(assets);
      this.groups.set(groups);
    } catch {
      this.changes.problems.set(['The Assets could not be loaded. Reload the page to try again.']);
    }
  }
}
