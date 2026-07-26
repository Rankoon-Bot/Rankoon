import { Component, EventEmitter, HostListener, Input, Output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'rk-sticky-save-bar',
  standalone: true,
  imports: [TranslocoPipe],
  templateUrl: './sticky-save-bar.component.html',
  styleUrl: './sticky-save-bar.component.scss',
})
export class StickySaveBarComponent {
  @Input() dirty = false;
  @Input() saving = false;
  @Input() valid = true;
  @Input() message = '';
  @Input() contextName = '';
  @Input() validationMessage = '';
  @Input() saveLabel = '';
  @Input() resetLabel = '';
  @Output() readonly save = new EventEmitter<void>();
  @Output() readonly reset = new EventEmitter<void>();

  @HostListener('window:keydown', ['$event']) onKeydown(event: KeyboardEvent): void {
    if (!this.dirty || this.saving || !this.valid || !(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== 's') return;
    event.preventDefault(); this.save.emit();
  }
}
