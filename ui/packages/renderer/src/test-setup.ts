// Vitest setup for zoneless Angular. No zone.js: Angular 21's TestBed supports
// zoneless change detection via provideZonelessChangeDetection() inside each
// TestBed.configureTestingModule call.
import { getTestBed } from '@angular/core/testing';
import { BrowserTestingModule, platformBrowserTesting } from '@angular/platform-browser/testing';

getTestBed().initTestEnvironment(BrowserTestingModule, platformBrowserTesting(), {
  teardown: { destroyAfterEach: true },
});
