import { Component, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';

import { OperatorShellComponent } from './operator-shell.component';

@Component({ selector: 'sc-stub-production', standalone: true, template: 'production-page' })
class StubProduction {}
@Component({ selector: 'sc-stub-assembly', standalone: true, template: 'assembly-page' })
class StubAssembly {}
@Component({ selector: 'sc-stub-approvals', standalone: true, template: 'approvals-page' })
class StubApprovals {}

async function setup() {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      provideRouter([
        { path: 'operator/production', component: StubProduction },
        { path: 'operator/assembly', component: StubAssembly },
        { path: 'operator/approvals', component: StubApprovals },
      ]),
    ],
  });
  const fixture = TestBed.createComponent(OperatorShellComponent);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, router: TestBed.inject(Router) };
}

describe('OperatorShellComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('its nav shows Production, Assembly and Approvals, and nothing from the main window sidebar', async () => {
    const { fixture } = await setup();

    const links = Array.from(
      fixture.nativeElement.querySelectorAll('.nav-link') as NodeListOf<HTMLElement>,
    ).map((link) => [link.textContent?.trim(), link.getAttribute('href')]);

    expect(links).toEqual([
      ['Production', '/operator/production'],
      ['Assembly', '/operator/assembly'],
      ['Approvals', '/operator/approvals'],
    ]);
    expect(fixture.nativeElement.querySelector('[data-test="nav-features"]')).toBeNull();
  });

  it('shows the page of the nav entry the owner opened and marks it active', async () => {
    const { fixture, router } = await setup();

    await router.navigateByUrl('/operator/approvals');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('approvals-page');
    expect(
      fixture.nativeElement.querySelector('.nav-link--active')?.getAttribute('data-test'),
    ).toBe('operator-nav-approvals');
  });
});
