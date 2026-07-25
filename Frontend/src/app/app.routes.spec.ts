import { botOperatorGuard, guildGuard, moduleGuard } from './guards/auth.guard';
import { routes } from './app.routes';

describe('application routes', () => {
  const children = routes.find(route => route.path === '')!.children!;
  it('guards every analytics shell and page with the analytics module', () => {
    const analytics = children.find(route => route.path === 'analytics')!;
    expect(analytics.canActivate).toEqual([guildGuard, moduleGuard]); expect(analytics.data?.['module']).toBe('analytics');
    for (const route of analytics.children!.filter(route => route.loadComponent)) { expect(route.canActivate).toEqual([guildGuard, moduleGuard]); expect(route.data?.['module']).toBe('analytics'); }
  });
  it('guards the Bot Operations shell and every loaded child', () => {
    const operations = children.find(route => route.path === 'bot-management')!; expect(operations.canActivate).toEqual([botOperatorGuard]);
    for (const route of operations.children!.filter(route => route.loadComponent)) expect(route.canActivate).toEqual([botOperatorGuard]);
  });
  it('redirects legacy logs and never loads guild error logs', () => {
    expect(children.find(route => route.path === 'logs/errors')?.redirectTo).toBe('/analytics/audit');
    expect(JSON.stringify(children)).not.toContain('ErrorLogsComponent');
  });
});
