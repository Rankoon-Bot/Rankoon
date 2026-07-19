import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { LocaleService } from './i18n/locale.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet],
  template: '<router-outlet></router-outlet>',
  styleUrl: './app.scss'
})
export class App {
  private readonly locale = inject(LocaleService);
}
