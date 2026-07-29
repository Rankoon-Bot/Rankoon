import { Component, ElementRef, EventEmitter, Input, Output, ViewChild } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'rk-confirmation-dialog',
  standalone: true,
  imports: [TranslocoPipe],
  templateUrl: './confirmation-dialog.component.html',
  styleUrl: './confirmation-dialog.component.scss',
})
export class ConfirmationDialogComponent {
  @Input() title = '';
  @Input() description = '';
  @Input() impact = '';
  @Input() confirmLabel = '';
  @Input() busy = false;
  @Input() dangerous = false;
  @Input() irreversible = false;
  @Output() readonly confirmed = new EventEmitter<void>();
  @Output() readonly dismissed = new EventEmitter<void>();
  @ViewChild('dialog') private readonly dialog?: ElementRef<HTMLDialogElement>;

  open(): void { this.dialog?.nativeElement.showModal(); }
  close(): void { this.dialog?.nativeElement.close(); }
  cancel(event: Event): void {
    event.preventDefault();
    if (!this.busy) this.dismissed.emit();
  }
}
