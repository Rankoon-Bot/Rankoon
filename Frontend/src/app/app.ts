import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { LocaleService } from './i18n/locale.service';
import { ToastContainerComponent } from './components/toast-container/toast-container.component';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, ToastContainerComponent],
  template: '<router-outlet></router-outlet><app-toast-container></app-toast-container>',
  styleUrl: './app.scss'
})
export class App {
  private readonly locale = inject(LocaleService);
}
