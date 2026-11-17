import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/** The dashboard shell: every page is a route. */
@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  template: '<router-outlet />',
})
export class App {}
