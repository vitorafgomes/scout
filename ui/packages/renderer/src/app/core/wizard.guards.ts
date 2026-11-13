import { inject } from '@angular/core';
import { Router, type CanActivateFn, type UrlTree } from '@angular/router';

import { WizardStore } from './wizard.store';

export const requireClientIdGuard: CanActivateFn = (): boolean | UrlTree => {
  const store = inject(WizardStore);
  const router = inject(Router);
  if (store.clientId() === null) {
    return router.parseUrl('/');
  }
  return true;
};

export const requireGuildGuard: CanActivateFn = (): boolean | UrlTree => {
  const store = inject(WizardStore);
  const router = inject(Router);
  if (store.guild() === null) {
    return router.parseUrl('/');
  }
  return true;
};

// Guards /launch: token alone (no clientId/guild) is sufficient to proceed.
export const requireBotTokenGuard: CanActivateFn = (): boolean | UrlTree => {
  const store = inject(WizardStore);
  const router = inject(Router);
  if (!store.hasProvisioning()) {
    return router.parseUrl('/');
  }
  return true;
};
