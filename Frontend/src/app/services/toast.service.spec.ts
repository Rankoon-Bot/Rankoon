import { fakeAsync, TestBed, tick } from '@angular/core/testing';
import { ToastService } from './toast.service';

describe('ToastService', () => {
  let service: ToastService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(ToastService);
  });

  it('stacks notifications and removes the oldest after six entries', () => {
    for (let index = 1; index <= 7; index++) service.info(`Message ${index}`);

    expect(service.toasts().map(toast => toast.message)).toEqual(['Message 2', 'Message 3', 'Message 4', 'Message 5', 'Message 6', 'Message 7']);
  });

  it('dismisses notifications manually', () => {
    service.success('Saved');
    service.dismiss(service.toasts()[0].id);

    expect(service.toasts()).toEqual([]);
  });

  it('dismisses notifications after the configured duration', fakeAsync(() => {
    service.error('Could not save');
    tick(6000);

    expect(service.toasts()).toEqual([]);
  }));
});
