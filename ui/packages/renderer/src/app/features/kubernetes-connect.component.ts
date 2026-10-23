import {
  ChangeDetectionStrategy,
  Component,
  EventEmitter,
  OnInit,
  Output,
  computed,
  inject,
  signal,
} from '@angular/core';

import {
  kubernetesLabelError,
  readIpcError,
  type KubernetesAuthKind,
  type KubernetesDetection,
  type KubernetesStatus,
} from '@scout/shared';

import { ScoutBridgeService } from '../core/scout-bridge';

const AUTH_LABEL: Record<KubernetesAuthKind, string> = {
  token: 'token',
  'client-certificate': 'client certificate',
  exec: 'command',
  'auth-provider': 'auth provider',
  basic: 'username and password',
  unknown: 'unknown sign-in',
};

export const READ_ONLY_USER_COMMANDS = [
  'kubectl create serviceaccount scout-reader -n default',
  'kubectl create clusterrolebinding scout-reader-view --clusterrole=view --serviceaccount=default:scout-reader',
  'kubectl create token scout-reader -n default --duration=8760h',
].join('\n');

@Component({
  selector: 'sc-kubernetes-connect',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (status(); as s) {
      <section class="connect" data-test="kubernetes-connect-section">
        @if (s.connected) {
          <p class="note" data-test="kubernetes-connected">
            Using {{ labelsOf(s) }} from {{ s.path }}
          </p>
          <div class="row">
            <button
              type="button"
              class="btn btn--secondary"
              data-test="kubernetes-disconnect"
              [disabled]="busy()"
              (click)="disconnect()"
            >
              Disconnect
            </button>
          </div>
        } @else if (detection(); as d) {
          @if (d.path === null) {
            <p class="note" data-test="kubernetes-not-found">
              No Kubernetes configuration found on this computer (looked in $KUBECONFIG and
              ~/.kube/config).
            </p>
          } @else if (d.error !== null) {
            <p class="err" data-test="kubernetes-detect-error">{{ d.error }}</p>
          } @else {
            <p class="note" data-test="kubernetes-question">
              Use the Kubernetes configuration on this computer?
            </p>
            <code class="path" data-test="kubernetes-path">{{ d.path }}</code>
            <ul class="contexts">
              @for (c of d.contexts; track c.name) {
                <li>
                  <label class="context">
                    <input
                      type="checkbox"
                      [checked]="selected().has(c.name)"
                      [attr.data-test]="'kubernetes-context-' + c.name"
                      (change)="toggle(c.name)"
                    />
                    <span
                      >{{ c.name }} &middot; {{ c.server }} &middot; {{ authLabel[c.auth] }}</span
                    >
                  </label>
                  @if (c.execCommand) {
                    <p class="exec" [attr.data-test]="'kubernetes-exec-' + c.name">
                      Runs {{ c.execCommand }} to get a token
                    </p>
                  }
                  @if (selected().has(c.name)) {
                    <label class="label-field">
                      <span>Label</span>
                      <input
                        #labelInput
                        type="text"
                        class="input"
                        [value]="selected().get(c.name)"
                        [attr.data-test]="'kubernetes-label-' + c.name"
                        (input)="setLabel(c.name, labelInput.value)"
                      />
                    </label>
                  }
                </li>
              }
            </ul>
            @if (labelError(); as error) {
              <p class="err" data-test="kubernetes-label-error">{{ error }}</p>
            }
            <button
              type="button"
              class="btn btn--primary"
              data-test="kubernetes-use"
              [disabled]="selected().size === 0 || labelError() !== null || busy()"
              (click)="use()"
            >
              Use selected contexts
            </button>
            <p class="help" data-test="kubernetes-read-only-note">
              Scout only reads from your clusters, but these credentials may allow changes. A
              read-only user is safer:
            </p>
            <pre class="commands" data-test="kubernetes-read-only-commands">{{ commands }}</pre>
            <button
              type="button"
              class="link"
              data-test="kubernetes-copy-commands"
              (click)="copy(commands)"
            >
              Copy commands
            </button>
          }
        }
      </section>
    }
    @if (actionError(); as message) {
      <p class="err" data-test="kubernetes-action-error">{{ message }}</p>
    }
  `,
  styles: [
    `
      .connect {
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .row {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
      }
      .note {
        margin: 0;
        font-size: 13px;
        color: #e4e8f0;
      }
      .path {
        font-size: 12px;
        color: #7b8299;
        word-break: break-all;
      }
      .contexts {
        list-style: none;
        margin: 0;
        padding: 0;
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .context {
        display: inline-flex;
        gap: 6px;
        align-items: center;
        font-size: 13px;
        color: #e4e8f0;
      }
      .label-field {
        display: flex;
        gap: 6px;
        align-items: center;
        margin: 4px 0 0 22px;
        font-size: 12px;
        color: #7b8299;
      }
      .input {
        font-size: 12px;
        padding: 4px 6px;
        border-radius: 6px;
        border: 1px solid #252b3b;
        background: #11131a;
        color: #e4e8f0;
      }
      .exec {
        margin: 2px 0 0 22px;
        font-size: 12px;
        color: #fbbf24;
      }
      .help {
        margin: 0;
        font-size: 12px;
        color: #7b8299;
      }
      .commands {
        margin: 0;
        padding: 8px;
        border-radius: 6px;
        background: #11131a;
        color: #e4e8f0;
        font-size: 11px;
        white-space: pre-wrap;
        user-select: all;
      }
      .link {
        align-self: flex-start;
        padding: 0;
        border: none;
        background: none;
        color: #60a5fa;
        font-size: 12px;
        cursor: pointer;
      }
      .err {
        margin: 0;
        font-size: 12px;
        color: #f87171;
      }
      .btn {
        align-self: flex-start;
        font-size: 13px;
        padding: 6px 12px;
        border-radius: 6px;
        border: 1px solid transparent;
        cursor: pointer;
      }
      .btn:disabled {
        opacity: 0.5;
        cursor: not-allowed;
      }
      .btn--primary {
        background: #2563eb;
        color: white;
      }
      .btn--secondary {
        background: transparent;
        color: #e4e8f0;
        border-color: #252b3b;
      }
    `,
  ],
})
export class KubernetesConnectComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);

  readonly status = signal<KubernetesStatus | null>(null);
  readonly detection = signal<KubernetesDetection | null>(null);
  /** The ticked context names, each with the label the owner typed (prefilled with the name). */
  readonly selected = signal<ReadonlyMap<string, string>>(new Map());
  readonly labelError = computed(() =>
    kubernetesLabelError([...this.selected()].map(([name, label]) => ({ name, label }))),
  );
  readonly busy = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly authLabel = AUTH_LABEL;
  readonly commands = READ_ONLY_USER_COMMANDS;

  @Output() readonly connectedChange = new EventEmitter<boolean>();

  async ngOnInit(): Promise<void> {
    await this.run(() => this.load(this.bridge.kubernetesStatus()));
  }

  toggle(name: string): void {
    const next = new Map(this.selected());
    if (!next.delete(name)) next.set(name, name);
    this.selected.set(next);
  }

  setLabel(name: string, label: string): void {
    this.selected.set(new Map(this.selected()).set(name, label));
  }

  labelsOf(status: KubernetesStatus): string {
    return status.contexts.map((c) => c.label).join(', ');
  }

  async use(): Promise<void> {
    const selected = this.selected();
    const contexts = (this.detection()?.contexts ?? [])
      .filter((c) => selected.has(c.name))
      .map((c) => ({ name: c.name, label: (selected.get(c.name) ?? c.name).trim() }));
    await this.run(() => this.load(this.bridge.kubernetesUse({ contexts })));
  }

  async disconnect(): Promise<void> {
    await this.run(() => this.load(this.bridge.kubernetesDisconnect()));
  }

  async copy(text: string): Promise<void> {
    await navigator.clipboard.writeText(text).catch(() => undefined);
  }

  private async load(next: Promise<KubernetesStatus>): Promise<void> {
    const status = await next;
    this.status.set(status);
    this.connectedChange.emit(status.connected);
    if (!status.connected) {
      this.selected.set(new Map());
      this.detection.set(await this.bridge.kubernetesDetect());
    }
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    this.actionError.set(null);
    try {
      await action();
    } catch (err: unknown) {
      this.actionError.set(readIpcError(err).message);
    } finally {
      this.busy.set(false);
    }
  }
}
