import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { ScoutBridgeService } from '../core/scout-bridge';

@Component({
  selector: 'sc-vault-availability-banner',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (!available()) {
      <div class="vault-warn-banner" role="alert">
        Secret storage is unavailable on this system. Scout cannot save bot tokens securely until a
        keyring is installed. See docs for details.
      </div>
    }
  `,
  styles: [
    `
      .vault-warn-banner {
        background: #7a5c00;
        color: #fff3cd;
        padding: 10px 16px;
        font-size: 13px;
        text-align: center;
        position: sticky;
        top: 0;
        z-index: 100;
      }
    `,
  ],
})
export class VaultAvailabilityBannerComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);

  readonly available = signal<boolean>(true);

  ngOnInit(): void {
    void this.checkAvailability();
  }

  private async checkAvailability(): Promise<void> {
    try {
      const result = await this.bridge.vaultIsAvailable();
      this.available.set(result.available);
    } catch {
      // If the bridge is not available (outside Electron), assume available
      // so the banner does not appear in non-Electron environments by default.
      this.available.set(true);
    }
  }
}
