import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, Router } from '@angular/router';
import { BehaviorSubject, Subject } from 'rxjs';
import { ApiErrorService } from '../../services/api-error.service';
import { BotManagementRange } from './bot-management.models';
import { OperationsPageBase } from './operations-page.base';

@Component({ standalone: true, template: '' })
class TestOperationsPage extends OperationsPageBase<string> {
  readonly requests: { range: BotManagementRange; response: Subject<string> }[] = [];
  constructor() { super(); this.initialize(); }
  protected request(range: BotManagementRange) {
    const response = new Subject<string>(); this.requests.push({ range, response }); return response.asObservable();
  }
}

describe('Operations page request recovery', () => {
  const params = new BehaviorSubject(convertToParamMap({ range: '7d' }));
  beforeEach(() => {
    params.next(convertToParamMap({ range: '7d' }));
    TestBed.configureTestingModule({ imports: [TestOperationsPage], providers: [
      { provide: ActivatedRoute, useValue: { queryParamMap: params } },
      { provide: Router, useValue: { navigate: jasmine.createSpy() } },
      { provide: ApiErrorService, useValue: { resolve: () => ({ message: 'Try again' }) } }
    ] });
  });
  it('cancels stale ranges and only displays the latest response', () => {
    const fixture = TestBed.createComponent(TestOperationsPage); const page = fixture.componentInstance;
    params.next(convertToParamMap({ range: '90d' }));
    expect(page.requests[0].response.observed).toBeFalse();
    page.requests[0].response.next('old');
    expect(page.data()).toBeNull(); expect(page.loading()).toBeTrue();
    page.requests[1].response.next('new'); page.requests[1].response.complete();
    expect(page.data()).toBe('new'); expect(page.loading()).toBeFalse();
    fixture.destroy();
  });
  it('allows retry after a failure without retaining mismatched data', () => {
    const fixture = TestBed.createComponent(TestOperationsPage); const page = fixture.componentInstance;
    page.requests[0].response.error(new Error('failed'));
    expect(page.error()).toBe('Try again'); expect(page.loading()).toBeFalse();
    page.load(); expect(page.error()).toBeNull(); expect(page.loading()).toBeTrue();
    page.requests[1].response.next('recovered'); page.requests[1].response.complete();
    expect(page.data()).toBe('recovered');
    fixture.destroy();
  });
});
