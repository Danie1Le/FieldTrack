import { Component, inject } from '@angular/core';
import { AuthService } from '../auth/auth';

/** The Control room: the whole fleet at once (Dashboard UX prototype, variant B). */
@Component({
  selector: 'app-control-room',
  templateUrl: './control-room.html',
  styleUrl: './control-room.css',
})
export class ControlRoom {
  protected readonly auth = inject(AuthService);
}
