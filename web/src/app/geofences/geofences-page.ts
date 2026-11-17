import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import { AuthService } from '../auth/auth';
import { AlertRule, GeoJsonPolygon, Geofence } from '../control-room/live';
import { GeofenceDraft, GeofenceEditor } from './geofence-editor';

/** Why the API refused a change, as text for people: its validation messages, else its problem detail. */
function problemsOf(error: unknown): string[] {
  const body = error instanceof HttpErrorResponse ? error.error : null;
  const messages = Object.values<string[]>(body?.errors ?? {}).flat();
  if (messages.length) {
    return messages;
  }

  return [typeof body?.detail === 'string' ? body.detail : 'The change did not go through. Try again.'];
}

/**
 * The Geofences page (Admins only, spec #21 story 27): draw, edit and delete Geofences, and see which Alert Rules use
 * each one. The Control room map only shows them.
 */
@Component({
  selector: 'app-geofences-page',
  imports: [GeofenceEditor, RouterLink],
  templateUrl: './geofences-page.html',
  styleUrl: './geofences-page.css',
})
export class GeofencesPage {
  private readonly http = inject(HttpClient);
  protected readonly auth = inject(AuthService);

  protected readonly geofences = signal<Geofence[] | null>(null);
  private readonly rules = signal<AlertRule[]>([]);
  /** The Geofence being edited, or a just-drawn area (id null); the map starts editing from its area. */
  protected readonly draft = signal<GeofenceDraft | null>(null);
  protected readonly name = signal('');
  protected readonly kind = signal<Geofence['kind']>('Inclusion');
  /** The draft's area as last drawn or edited on the map. */
  private readonly area = signal<GeoJsonPolygon | null>(null);
  protected readonly busy = signal(false);
  protected readonly problems = signal<string[]>([]);

  constructor() {
    void this.load();
  }

  protected rulesOf(geofence: Geofence): string {
    const names = this.rules().filter((r) => r.geofenceId === geofence.id).map((r) => r.name);
    return names.length ? names.join(', ') : 'No Alert Rule names it';
  }

  protected edit(geofence: Geofence): void {
    this.start(geofence.id, geofence.area, geofence.name, geofence.kind);
  }

  protected drawn(area: GeoJsonPolygon): void {
    this.start(null, area, '', 'Inclusion');
  }

  protected changed(area: GeoJsonPolygon): void {
    this.area.set(area);
  }

  protected cancel(): void {
    this.draft.set(null);
    this.problems.set([]);
  }

  protected save(): Promise<void> {
    const id = this.draft()!.id;
    const body = { name: this.name(), kind: this.kind(), area: this.area() };
    return this.change(
      id === null
        ? this.http.post('/api/geofences', body)
        : this.http.put(`/api/geofences/${encodeURIComponent(id)}`, body),
    );
  }

  protected remove(): Promise<void> {
    const id = this.draft()!.id!;
    if (!confirm(`Delete the Geofence "${this.name()}"?`)) {
      return Promise.resolve();
    }

    return this.change(this.http.delete(`/api/geofences/${encodeURIComponent(id)}`));
  }

  private start(id: string | null, area: GeoJsonPolygon, name: string, kind: Geofence['kind']): void {
    this.draft.set({ id, area });
    this.area.set(area);
    this.name.set(name);
    this.kind.set(kind);
    this.problems.set([]);
  }

  /** Sends a change; once it is done, closes the form and reloads, else shows why it was refused. */
  private async change(request: Observable<unknown>): Promise<void> {
    this.busy.set(true);
    this.problems.set([]);
    try {
      await firstValueFrom(request);
      this.draft.set(null);
      await this.load();
    } catch (error) {
      this.problems.set(problemsOf(error));
    } finally {
      this.busy.set(false);
    }
  }

  private async load(): Promise<void> {
    try {
      const [geofences, rules] = await Promise.all([
        firstValueFrom(this.http.get<Geofence[]>('/api/geofences')),
        firstValueFrom(this.http.get<AlertRule[]>('/api/alert-rules')),
      ]);
      this.geofences.set(geofences);
      this.rules.set(rules);
    } catch {
      this.problems.set(['The Geofences could not be loaded. Reload the page to try again.']);
    }
  }
}
