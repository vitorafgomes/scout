import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { AppComponent } from './app.component';
import { AssemblyComponent } from './assembly/assembly.component';
import { ExperimentsComponent } from './experiments/experiments.component';
import { ReportsComponent } from './reports/reports.component';
import { appRoutes } from './app.routes';
import { MenuPreferences } from './core/menu-preferences';
import { ScoutBridgeService } from './core/scout-bridge';
import { FloatingButtonComponent } from './floating-button/floating-button.component';
import { OperatorShellComponent } from './operator/operator-shell.component';
import { ProductionComponent } from './production/production.component';
import { ReviewComponent } from './review/review.component';

describe('app routes', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter(appRoutes)],
    });
  });

  it('sends the retired Status screen to Logs', async () => {
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/status');

    expect(router.url).toBe('/logs');
  });

  it('opens Logs while it is shown in the menu', async () => {
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/logs');

    expect(router.url).toBe('/logs');
  });

  it('sends /logs to Settings once Logs is hidden from the menu', async () => {
    TestBed.inject(MenuPreferences).setShowLogs(false);
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/logs');

    expect(router.url).toBe('/settings');
  });

  it('review resolves to the Review screen', async () => {
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/review');

    let route = TestBed.inject(ActivatedRoute);
    while (route.firstChild !== null) route = route.firstChild;
    expect(router.url).toBe('/review');
    expect(route.component).toBe(ReviewComponent);
  });

  it('operator opens on Production inside the operator shell', async () => {
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/operator');

    let route = TestBed.inject(ActivatedRoute);
    while (route.firstChild !== null) route = route.firstChild;
    expect(router.url).toBe('/operator/production');
    expect(route.component).toBe(ProductionComponent);
    expect(route.parent?.component).toBe(OperatorShellComponent);
  });

  it('operator/assembly resolves to the Assembly page inside the operator shell', async () => {
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/operator/assembly');

    let route = TestBed.inject(ActivatedRoute);
    while (route.firstChild !== null) route = route.firstChild;
    expect(router.url).toBe('/operator/assembly');
    expect(route.component).toBe(AssemblyComponent);
    expect(route.parent?.component).toBe(OperatorShellComponent);
  });

  it('operator/reports and operator/experiments resolve to their pages inside the operator shell', async () => {
    const router = TestBed.inject(Router);
    const resolved: unknown[] = [];

    for (const url of ['/operator/reports', '/operator/experiments']) {
      await router.navigateByUrl(url);
      let route = TestBed.inject(ActivatedRoute);
      while (route.firstChild !== null) route = route.firstChild;
      resolved.push([router.url, route.component, route.parent?.component]);
    }

    expect(resolved).toEqual([
      ['/operator/reports', ReportsComponent, OperatorShellComponent],
      ['/operator/experiments', ExperimentsComponent, OperatorShellComponent],
    ]);
  });

  it('operator/approvals resolves to the Review screen filtered to youtube', async () => {
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/operator/approvals');

    let route = TestBed.inject(ActivatedRoute);
    while (route.firstChild !== null) route = route.firstChild;
    expect(router.url).toBe('/operator/approvals');
    expect(route.component).toBe(ReviewComponent);
    expect(route.snapshot.data['feature']).toBe('youtube');
  });

  it('the main window no longer has a production route', async () => {
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/production');

    expect(router.url).toBe('/');
  });

  it('the review route of the main window keeps every feature', async () => {
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/review');

    let route = TestBed.inject(ActivatedRoute);
    while (route.firstChild !== null) route = route.firstChild;
    expect(route.snapshot.data['feature']).toBeUndefined();
  });

  it('floating-button resolves to the floating button component', async () => {
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/floating-button');

    let route = TestBed.inject(ActivatedRoute);
    while (route.firstChild !== null) route = route.firstChild;
    expect(router.url).toBe('/floating-button');
    expect(route.component).toBe(FloatingButtonComponent);
  });
});

describe('the floating button window', () => {
  afterEach(() => {
    history.replaceState(null, '', '#/');
  });

  it('renders the operator route outside the wizard shell', () => {
    history.replaceState(null, '', '#/operator');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: ScoutBridgeService, useValue: {} },
      ],
    });

    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('sc-shell')).toBeNull();
    expect(fixture.nativeElement.querySelector('router-outlet')).not.toBeNull();
  });

  it('renders the floating button route outside the wizard shell', () => {
    history.replaceState(null, '', '#/floating-button');
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: ScoutBridgeService, useValue: {} },
      ],
    });

    const fixture = TestBed.createComponent(AppComponent);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('sc-shell')).toBeNull();
    expect(fixture.nativeElement.querySelector('router-outlet')).not.toBeNull();
  });
});
