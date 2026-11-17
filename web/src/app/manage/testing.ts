import { ComponentFixture } from '@angular/core/testing';

/** Reads and drives a management page in specs the way an Admin would: by visible text and field names. */
export function page(fixture: ComponentFixture<unknown>) {
  const root = fixture.nativeElement as HTMLElement;
  const text = (element: Element | null | undefined) => element?.textContent!.trim();

  return {
    root,
    text,
    /** Lets awaited HTTP answers finish, then renders. */
    async settle() {
      await new Promise((resolve) => setTimeout(resolve));
      await fixture.whenStable();
    },
    /** The button labelled `label`, in the table row named `row` when given. */
    button(label: string, row?: string): HTMLButtonElement {
      const scope = row ? [...root.querySelectorAll('tr')].find((tr) => text(tr.querySelector('td')) === row)! : root;
      const found = [...scope.querySelectorAll('button')].find((b) => text(b) === label);
      if (!found) {
        throw new Error(`No button "${label}"${row ? ` in row "${row}"` : ''}`);
      }

      return found;
    },
    /** Types or picks `value` in the form field named `name`. */
    fill(name: string, value: string) {
      const field = root.querySelector<HTMLInputElement | HTMLSelectElement>(`[name="${name}"]`)!;
      field.value = value;
      field.dispatchEvent(new Event('input'));
      field.dispatchEvent(new Event('change'));
    },
    /** Each row's cell texts, without the action buttons. */
    rows(selector: string): (string | undefined)[][] {
      return [...root.querySelectorAll(selector)].map((row) => [...row.querySelectorAll('td:not(.actions)')].map((cell) => text(cell)));
    },
  };
}
