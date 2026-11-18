import { HttpErrorResponse } from '@angular/common/http';

/**
 * Why the API refused a request, as text for people: its validation messages, else its problem detail, else
 * `fallback`. Never the problem's title: a bare status (`NotFound()`) only titles it "Not Found". Shown as text, never
 * as HTML.
 */
export function problemsOf(error: unknown, fallback = 'The change did not go through. Try again.'): string[] {
  const body = error instanceof HttpErrorResponse ? error.error : null;
  const messages = Object.values<string[]>(body?.errors ?? {}).flat();
  if (messages.length) {
    return messages;
  }

  return [typeof body?.detail === 'string' ? body.detail : fallback];
}
