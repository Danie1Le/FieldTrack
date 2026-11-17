import { HttpClient } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
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

  protected readonly assets = signal<Asset[] | null>(null);
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

  protected assetsIn(group: AssetGroup): number {
    return (this.assets() ?? []).filter((asset) => asset.assetGroupId === group.id).length;
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
        firstValueFrom(this.http.get<Asset[]>('/api/assets')),
        firstValueFrom(this.http.get<AssetGroup[]>('/api/asset-groups')),
      ]);
      this.assets.set(assets);
      this.groups.set(groups);
    } catch {
      this.changes.problems.set(['The Assets could not be loaded. Reload the page to try again.']);
    }
  }
}
