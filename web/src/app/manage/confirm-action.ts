import { Component, input, output, signal } from '@angular/core';

/**
 * A button for a destructive action that asks first, in the page (#40): a first click shows what will happen and a
 * "Yes, …" button; only that one emits `confirmed`.
 */
@Component({
  selector: 'app-confirm-action',
  template: `
    @if (asking()) {
      <span class="ask" role="group" [attr.aria-label]="label()">
        <span>{{ question() }}</span>
        <button type="button" class="btn yes" [disabled]="disabled()" (click)="asking.set(false); confirmed.emit()">
          Yes, {{ label().toLowerCase() }}
        </button>
        <button type="button" class="btn" (click)="asking.set(false)">Cancel</button>
      </span>
    } @else {
      <button type="button" class="btn" [disabled]="disabled()" (click)="asking.set(true)">{{ label() }}</button>
    }
  `,
  styles: `
    .ask { display: inline-flex; flex-wrap: wrap; align-items: center; gap: 6px; white-space: normal; text-align: left; }
    .yes { color: var(--alert); border-color: var(--alert); }
  `,
})
export class ConfirmAction {
  /** What the button does, such as Delete or Detach. */
  readonly label = input.required<string>();
  /** What will happen, asked before it does. */
  readonly question = input.required<string>();
  readonly disabled = input(false);
  readonly confirmed = output<void>();
  protected readonly asking = signal(false);
}
