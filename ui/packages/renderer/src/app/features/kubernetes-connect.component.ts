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
  type KubernetesContextInfo,
  type KubernetesDetection,
  type KubernetesStatus,
} from '@scout/shared';

import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';

import { confirmAction } from '../core/confirm-dialog.component';
import { IconComponent } from '../core/icon.component';
import { NoticeService } from '../core/notice.service';
import { ScoutBridgeService } from '../core/scout-bridge';

import { CollapsibleListComponent } from './collapsible-list.component';

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
  imports: [CollapsibleListComponent, IconComponent, MatButtonModule],
  template: `
    @if (status(); as s) {
      <section class="connect" data-test="kubernetes-connect-section">
        @if (s.connected) {
          <div class="row">
            <span class="note using" data-test="kubernetes-connected"
              >Using <strong>{{ labelsOf(s) }}</strong> from {{ s.path }}</span
            >
            <button
              type="button"
              matButton="outlined"
              class="danger"
              data-test="kubernetes-disconnect"
              [disabled]="busy()"
              (click)="askDisconnect(s)"
            >
              <svg
                viewBox="0 0 24 24"
                width="16"
                height="16"
                fill="none"
                stroke="currentColor"
                stroke-width="2"
                stroke-linecap="round"
                stroke-linejoin="round"
                aria-hidden="true"
              >
                <path d="M9 7V3M15 7V3M7 7h10v4a5 5 0 0 1-10 0zM12 16v5M3 3l18 18" /></svg
              >Disconnect
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
            <h3 class="question" data-test="kubernetes-question">
              Use the Kubernetes configuration on this computer?
            </h3>
            <code class="path" data-test="kubernetes-path">{{ d.path }}</code>
            <sc-collapsible-list
              key="kubernetes-contexts"
              [startOpen]="true"
              filterLabel="Filter the contexts"
              noMatch="No context matches the filter."
              [summary]="contextsSummary(d.contexts.length)"
              [items]="d.contexts"
              [textOf]="contextText"
            >
              <ng-template let-c>
                <div class="context-lines">
                  <label class="context">
                    <input
                      type="checkbox"
                      [checked]="selected().has(c.name)"
                      [attr.data-test]="'kubernetes-context-' + c.name"
                      (change)="toggle(c.name)"
                    />
                    <span>{{ contextLine(c) }}</span>
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
                </div>
              </ng-template>
            </sc-collapsible-list>
            @if (labelError(); as error) {
              <p class="err" data-test="kubernetes-label-error">{{ error }}</p>
            }
            <button
              type="button"
              matButton="filled"
              data-test="kubernetes-use"
              [disabled]="selected().size === 0 || labelError() !== null || busy()"
              (click)="use()"
            >
              <sc-icon name="check" [size]="16" />Use selected contexts
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
      @if (status() === null) {
        <button
          type="button"
          matButton="outlined"
          data-test="kubernetes-retry"
          [disabled]="busy()"
          (click)="retry()"
        >
          <sc-icon name="refresh" [size]="16" />Try again
        </button>
      }
    }
  `,
  styles: [
    `
      .connect {
        display: flex;
        flex-direction: column;
        gap: 10px;
        align-items: flex-start;
      }
      .using {
        margin-right: auto;
      }
      .question {
        margin: 0;
        font-size: 14px;
        font-weight: 600;
        color: var(--sc-fg);
      }
      .path {
        font-family: var(--sc-mono);
        font-size: 12px;
        color: var(--sc-fg-2);
        word-break: break-all;
        padding: 6px 10px;
        border-radius: 8px;
        background: var(--sc-code-bg);
      }
      sc-collapsible-list {
        align-self: stretch;
        --sc-list-row-bg: var(--sc-bg);
        --sc-list-row-padding: 8px 10px;
        --sc-list-filter-bg: var(--sc-bg);
        --sc-list-body-padding: 10px 0 0;
        --sc-list-body-gap: 10px;
        --sc-list-rows-gap: 6px;
        --sc-list-max-height: none;
      }
      .context-lines {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .context {
        display: flex;
        gap: 8px;
        align-items: flex-start;
        font-size: 13px;
        color: var(--sc-fg);
        cursor: pointer;
        overflow-wrap: anywhere;
      }
      .label-field {
        display: flex;
        flex-direction: column;
        gap: 4px;
        padding-left: 24px;
        font-size: 12px;
        color: var(--sc-muted);
      }
      .label-field .input {
        flex: none;
        width: 200px;
        height: 32px;
        box-sizing: border-box;
        border-radius: 8px;
        background: var(--sc-surface);
        font-size: 13px;
      }
      .exec {
        margin: 0 0 0 24px;
        font-size: 12px;
        color: var(--sc-on-warn-container);
      }
      .commands {
        margin: 0;
        padding: 8px 10px;
        border-radius: var(--sc-radius-sm);
        background: var(--sc-code-bg);
        color: var(--sc-fg-2);
        font-family: var(--sc-mono);
        font-size: 12px;
        white-space: pre-wrap;
        user-select: all;
        align-self: stretch;
      }
      .row {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
        align-items: center;
      }
      .note {
        margin: 0;
        font-size: 13px;
        color: var(--sc-fg);
      }
      .help {
        margin: 0;
        font-size: 12px;
        color: var(--sc-muted);
      }
      .link {
        align-self: flex-start;
        padding: 0;
        border: none;
        background: none;
        color: var(--sc-accent);
        font: inherit;
        font-size: 13px;
        cursor: pointer;
      }
      .input {
        flex: 1;
        height: 36px;
        font: inherit;
        font-size: 14px;
        padding: 0 10px;
        border-radius: var(--sc-radius-sm);
        border: 1px solid var(--sc-line);
        background: var(--sc-surface);
        color: var(--sc-fg);
      }
      .err {
        margin: 0;
        font-size: 13px;
        color: var(--sc-error);
      }
      .danger {
        --mat-button-outlined-label-text-color: var(--sc-error);
      }
      button sc-icon,
      button svg {
        margin-right: 8px;
        flex: none;
      }
    `,
  ],
})
export class KubernetesConnectComponent implements OnInit {
  private readonly bridge = inject(ScoutBridgeService);
  private readonly notice = inject(NoticeService);
  private readonly dialog = inject(MatDialog);

  readonly status = signal<KubernetesStatus | null>(null);
  readonly detection = signal<KubernetesDetection | null>(null);
  /** The ticked context names, each with the label the owner typed (prefilled with the name). */
  readonly selected = signal<ReadonlyMap<string, string>>(new Map());
  readonly labelError = computed(() =>
    kubernetesLabelError([...this.selected()].map(([name, label]) => ({ name, label }))),
  );
  readonly busy = signal(false);
  readonly actionError = signal<string | null>(null);
  /** What the contexts filter matches: the name, the server and the sign-in. */
  readonly contextText = (c: KubernetesContextInfo): string => this.contextLine(c);
  readonly commands = READ_ONLY_USER_COMMANDS;

  @Output() readonly connectedChange = new EventEmitter<boolean>();

  async ngOnInit(): Promise<void> {
    await this.retry();
  }

  /** Reads the Kubernetes status again, after a read that failed when the card opened. */
  async retry(): Promise<void> {
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

  /** One context as the list shows it: its name, its server and how it signs in. */
  contextLine(c: KubernetesContextInfo): string {
    return `${c.name} · ${c.server} · ${AUTH_LABEL[c.auth]}`;
  }

  /** The contexts in one line: how many, and what the owner does with them. */
  contextsSummary(count: number): string {
    return `Contexts: ${count} (tick the ones Scout may read)`;
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
    const s = this.status();
    if (this.actionError() === null && s?.connected === true)
      this.notice.show(`Kubernetes connected: ${this.labelsOf(s)}`, 'success');
  }

  /** Disconnect stops the cluster reads, so it asks first with Cancel focused. */
  async askDisconnect(status: KubernetesStatus): Promise<void> {
    const n = status.contexts.length;
    const confirmed = await confirmAction(this.dialog, {
      title: 'Disconnect Kubernetes?',
      body: `Scout stops reading from the ${n} ${n === 1 ? 'context' : 'contexts'} you chose. Your Kubernetes configuration is not changed.`,
      confirmLabel: 'Disconnect',
      confirmIcon: 'plug-off',
    });
    if (confirmed) await this.disconnect();
  }

  async disconnect(): Promise<void> {
    await this.run(() => this.load(this.bridge.kubernetesDisconnect()));
    if (this.actionError() === null && this.status()?.connected === false)
      this.notice.show('Kubernetes disconnected', 'success');
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
