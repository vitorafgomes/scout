import { inject, provideAppInitializer, provideZonelessChangeDetection } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { provideRouter, withHashLocation } from '@angular/router';

import { AppComponent } from './app/app.component';
import { appRoutes } from './app/app.routes';
import { WizardStore } from './app/core/wizard.store';

bootstrapApplication(AppComponent, {
  providers: [
    provideZonelessChangeDetection(),
    provideRouter(appRoutes, withHashLocation()),
    // Hydrate provisioning state from the vault before first render so the
    // post-connect navigation rail shows on any entry route, not only after
    // passing through the welcome screen. loadFromVault swallows errors and
    // resolves fast (one vaultHas IPC roundtrip).
    provideAppInitializer(async () => {
      await inject(WizardStore).loadFromVault();
    }),
  ],
}).catch((err: unknown) => {
  console.error(err);
});
