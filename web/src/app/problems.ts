import { HttpErrorResponse } from '@angular/common/http';

/** Why the API refused a change, as text for people: its validation messages, else its problem detail. */
export function problemsOf(error: unknown): string[] {
  const body = error instanceof HttpErrorResponse ? error.error : null;
  const messages = Object.values<string[]>(body?.errors ?? {}).flat();
  if (messages.length) {
    return messages;
  }

  return [typeof body?.detail === 'string' ? body.detail : 'The change did not go through. Try again.'];
}
