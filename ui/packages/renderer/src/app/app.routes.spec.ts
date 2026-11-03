import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { AppComponent } from './app.component';
import { appRoutes } from './app.routes';
import { MenuPreferences } from './core/menu-preferences';
import { ScoutBridgeService } from './core/scout-bridge';
import { FloatingButtonComponent } from './floating-button/floating-button.component';
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

  it('production resolves to the Production screen', async () => {
    const router = TestBed.inject(Router);

    await router.navigateByUrl('/production');

    let route = TestBed.inject(ActivatedRoute);
    while (route.firstChild !== null) route = route.firstChild;
    expect(router.url).toBe('/production');
    expect(route.component).toBe(ProductionComponent);
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
