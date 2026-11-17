import { signal } from '@angular/core';
import { Observable, firstValueFrom } from 'rxjs';
import { problemsOf } from '../problems';

/** A management page's changes to the API: one at a time, showing why the last one was refused. */
export class Changes {
  readonly busy = signal(false);
  readonly problems = signal<string[]>([]);

  /** Sends a change; once it is done runs `then` (close the form, reload), else keeps why it was refused. */
  async send(request: Observable<unknown>, then: () => unknown): Promise<void> {
    this.busy.set(true);
    this.problems.set([]);
    try {
      await firstValueFrom(request, { defaultValue: null });
      await then();
    } catch (error) {
      this.problems.set(problemsOf(error));
    } finally {
      this.busy.set(false);
    }
  }
}
