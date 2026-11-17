import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { Observable, firstValueFrom } from 'rxjs';
import { problemsOf } from '../problems';

/** A management page's changes to the API: one at a time, showing why the last one was refused. */
export class Changes {
  readonly busy = signal(false);
  readonly problems = signal<string[]>([]);

  /** `reload` reads the page's data again; it runs when a refusal says the data shown is stale. */
  constructor(private readonly reload: () => Promise<void>) {}

  /** Sends a change; once it is done runs `then` (close the form, reload), else keeps why it was refused. */
  async send(request: Observable<unknown>, then: () => unknown): Promise<void> {
    this.busy.set(true);
    this.problems.set([]);
    try {
      await firstValueFrom(request, { defaultValue: null });
      await then();
    } catch (error) {
      // 404 and 409: another change got there first (deleted the item, ended the Attachment), so retrying the same
      // change on the data shown would fail again.
      const status = error instanceof HttpErrorResponse ? error.status : 0;
      const gone = status === 404 && typeof (error as HttpErrorResponse).error?.detail !== 'string';
      this.problems.set(gone ? ['It no longer exists. The page now shows what is there.'] : problemsOf(error));
      if (status === 404 || status === 409) {
        await this.reload();
      }
    } finally {
      this.busy.set(false);
    }
  }
}
