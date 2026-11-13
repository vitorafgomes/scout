import { ChangeDetectionStrategy, Component, inject, OnInit, signal } from '@angular/core';
import { INSECURE_STORE_MESSAGE, type VaultUnavailableReason } from '@scout/shared';
import { ScoutBridgeService } from '../core/scout-bridge';

@Component({
  selector: 'sc-vault-availability-banner',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (!available()) {
      <div class="vault-warn-banner" role="alert">
        @if (reason() === 'insecure-store') {
          {{ insecureStoreMessage }}
        } @else {
          Secret storage is unavailable on this system. Scout cannot save bot tokens securely until
          a keyring is installed. See docs for details.
        }
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
  readonly reason = signal<VaultUnavailableReason | undefined>(undefined);
  readonly insecureStoreMessage = INSECURE_STORE_MESSAGE;

  ngOnInit(): void {
    void this.checkAvailability();
  }

  private async checkAvailability(): Promise<void> {
    try {
      const result = await this.bridge.vaultIsAvailable();
      this.available.set(result.available);
      this.reason.set(result.reason);
    } catch {
      this.available.set(false);
    }
  }
}
