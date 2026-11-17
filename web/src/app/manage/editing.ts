import { computed, signal } from '@angular/core';

/**
 * The item a management form is open for, or none.
 *
 * Every `open` starts a new session, and the page renders the form inside
 * `@for (session of editing.sessions(); track session.id)`, so each open builds the form afresh from its item. A form
 * that stays rendered keeps what was typed for the previous item instead: `[value]`, `[selected]` and `[checked]`
 * rewrite a field only when their bound expression changes, and Save would send those values to the new item.
 */
export class Editing<T> {
  private opened = 0;
  private readonly session = signal<{ readonly id: number; readonly item: T } | null>(null);

  /** The open session as a list of none or one, for `@for ... track session.id`. */
  readonly sessions = computed(() => {
    const session = this.session();
    return session ? [session] : [];
  });

  /** The item the form is open for, or null while it is closed. */
  readonly item = computed(() => this.session()?.item ?? null);

  open(item: T): void {
    this.session.set({ id: ++this.opened, item });
  }

  close(): void {
    this.session.set(null);
  }
}
