import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnInit,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';

import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';

import type { FeatureId } from '@scout/shared';

import { confirmAction } from '../core/confirm-dialog.component';
import { FeaturesStore, type FeatureViewModel } from '../core/features.store';
import { IconComponent } from '../core/icon.component';
import { NoticeService } from '../core/notice.service';
import { ScoutBridgeService } from '../core/scout-bridge';

import { CredentialDialogComponent } from './credential-dialog.component';
import { DesktopVoiceComponent } from './desktop-voice.component';
import {
  DockerCardComponent,
  NOTHING_BUT_DOCKER,
  type DockerCardStatus,
} from './docker-card.component';
import {
  FeatureCardComponent,
  featureChip,
  permissionCounts,
  type CardAction,
  type CardChip,
  type PermissionCounts,
} from './feature-card.component';
import { FeatureDetailsComponent } from './feature-details.component';
import { FeaturePanelComponent } from './feature-panel.component';
import { GitHubConnectComponent } from './github-connect.component';
import { KubernetesConnectComponent } from './kubernetes-connect.component';
import { WriteConfirmationDialogComponent } from './write-confirmation-dialog.component';
import { YouTubeConnectComponent } from './youtube-connect.component';

type Connection = 'connected' | 'not-connected' | 'unknown';

interface WriteConfirmation {
  readonly feature: FeatureViewModel;
  readonly purpose: 'enable' | 'grant';
}

/** What a feature's side panel shows: one per catalog feature, and Docker. */
export type PanelId = FeatureId | 'docker';

/** One card of the grid with what its panel header shows, all read from the live state. */
export interface CardView {
  readonly id: PanelId;
  readonly name: string;
  readonly description: string;
  readonly chip: CardChip | null;
  readonly fact: string;
  readonly factError: boolean;
  readonly counts: PermissionCounts | null;
  readonly noPermissions: string | null;
  readonly action: CardAction | null;
  readonly run: (() => void) | null;
}

/** Docker declares no rows of its own; its runs ride on GitHub's grant. */
export const DOCKER_PERMISSIONS = "Runs also need GitHub's write permission";

type Summary = Pick<CardView, 'fact' | 'factError' | 'action' | 'run'>;

function joined(...parts: readonly (string | null)[]): string {
  return parts.filter((p): p is string => p !== null).join(' · ');
}

export const NO_ALLOWED_USERS_WARNING =
  'Nobody can use this connection from chat until you add allowed users in Settings.';

@Component({
  selector: 'sc-features',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FeatureCardComponent,
    FeatureDetailsComponent,
    FeaturePanelComponent,
    CredentialDialogComponent,
    DesktopVoiceComponent,
    DockerCardComponent,
    IconComponent,
    MatButtonModule,
    MatProgressBarModule,
    RouterLink,
    GitHubConnectComponent,
    KubernetesConnectComponent,
    YouTubeConnectComponent,
    WriteConfirmationDialogComponent,
  ],
  template: `
    <main class="page">
      <header class="header">
        <div class="title-block">
          <h1>Features</h1>
          <p class="lede" data-test="features-lede">
            Connect Scout to your tools. A feature that writes asks for your permission first;
            actions that cannot be undone wait for you in
            <a routerLink="/review" data-test="features-approvals-link">Approvals</a>.
          </p>
        </div>
        <button type="button" matButton="outlined" data-test="features-refresh" (click)="reload()">
          <sc-icon name="refresh" [size]="16" />Refresh
        </button>
      </header>

      @if (store.loading() && store.features().length === 0) {
        <div
          class="loading"
          role="status"
          aria-label="Loading the features"
          data-test="features-loading"
        >
          <mat-progress-bar mode="indeterminate" aria-label="Loading the features" />
          <div class="skeletons" aria-hidden="true">
            @for (n of skeletons; track n) {
              <div class="skeleton" data-test="features-skeleton">
                <i class="line line--title"></i>
                <i class="line"></i>
                <i class="line line--short"></i>
                <i class="line line--button"></i>
              </div>
            }
          </div>
        </div>
      } @else if (store.backendUnavailable()) {
        <div class="alert" role="alert">
          <svg
            class="alert-icon"
            viewBox="0 0 24 24"
            width="20"
            height="20"
            fill="none"
            stroke="currentColor"
            stroke-width="2"
            stroke-linecap="round"
            aria-hidden="true"
          >
            <circle cx="12" cy="12" r="9" />
            <path d="M12 7v6M12 16.5v.5" />
          </svg>
          <span class="alert-text" data-test="backend-unavailable"
            >The Scout control plane is not running, so the features cannot be shown.</span
          >
          <button
            type="button"
            matButton="outlined"
            class="retry"
            data-test="features-retry"
            (click)="reload()"
          >
            <sc-icon name="refresh" [size]="16" />Try again
          </button>
        </div>
      } @else {
        @if (store.loadError(); as message) {
          <div class="alert" role="alert">
            <svg
              class="alert-icon"
              viewBox="0 0 24 24"
              width="20"
              height="20"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              aria-hidden="true"
            >
              <circle cx="12" cy="12" r="9" />
              <path d="M12 7v6M12 16.5v.5" />
            </svg>
            <span class="alert-text" data-test="features-error"
              >The features could not be read: {{ message }}</span
            >
            <button
              type="button"
              matButton="outlined"
              class="retry"
              data-test="features-retry"
              (click)="reload()"
            >
              <sc-icon name="refresh" [size]="16" />Try again
            </button>
          </div>
        }
        <div class="features-grid" data-test="features-grid">
          @for (c of cards(); track c.id) {
            <sc-feature-card
              [id]="c.id"
              [name]="c.name"
              [chip]="c.chip"
              [fact]="c.fact"
              [factError]="c.factError"
              [counts]="c.counts"
              [noPermissions]="c.noPermissions"
              [action]="c.action"
              [expanded]="selected() === c.id"
              (act)="c.run?.()"
              (details)="openPanel(c.id)"
            />
          }
        </div>

        <sc-feature-panel
          [open]="panelCard() !== null"
          [logo]="panelCard()?.id ?? ''"
          [name]="panelCard()?.name ?? ''"
          [chip]="panelCard()?.chip ?? null"
          [description]="panelCard()?.description ?? ''"
          (closed)="closePanel()"
        >
          @for (f of store.features(); track f.id) {
            <section
              class="panel-section"
              [hidden]="selected() !== f.id"
              [attr.data-test]="'panel-' + f.id"
            >
              @if (f.id === 'github') {
                <sc-github-connect
                  [docker]="dockerStatus()"
                  (connectedChange)="githubConnection.set($event ? 'connected' : 'not-connected')"
                  (setupDocker)="openPanel('docker')"
                />
              }
              @if (f.id === 'kubernetes') {
                <sc-kubernetes-connect
                  (connectedChange)="
                    kubernetesConnection.set($event ? 'connected' : 'not-connected')
                  "
                />
              }
              @if (f.id === 'youtube') {
                <sc-youtube-connect
                  (connectedChange)="youtubeConnection.set($event ? 'connected' : 'not-connected')"
                />
              }
              @if (f.id === 'desktop') {
                <sc-desktop-voice />
              }
              @if (f.id !== 'desktop' && connectionOf(f.id) === 'connected' && noAllowedUsers()) {
                <p class="access-warning" [attr.data-test]="'access-warning-' + f.id">
                  <sc-icon name="warning" [size]="16" /><span
                    >Nobody can use this connection from chat until you add allowed users in
                    <a routerLink="/settings">Settings</a>.</span
                  >
                </p>
              }
              <sc-feature-details
                [feature]="f"
                [connection]="connectionOf(f.id)"
                (toggle)="onToggle(f)"
                (allowWrites)="confirming.set({ feature: f, purpose: 'grant' })"
                (revokeWrites)="onRevokeWrites(f)"
                (openCredentialDialog)="openDialog(f)"
                (clearCredentials)="onClearCredentials(f)"
              />
            </section>
            @if (f.id === 'github') {
              <section
                class="panel-section"
                [hidden]="selected() !== 'docker'"
                data-test="panel-docker"
              >
                <sc-docker-card
                  [githubConnected]="githubConnection() === 'connected'"
                  (statusChange)="dockerStatus.set($event)"
                />
              </section>
            }
          }
        </sc-feature-panel>
      }

      @if (activeDialog(); as dlg) {
        <sc-credential-dialog
          [feature]="dlg"
          (submit)="onDialogSubmit(dlg.id, $event)"
          (cancel)="closeDialog()"
        />
      }

      @if (confirming(); as c) {
        <sc-write-confirmation-dialog
          [feature]="c.feature"
          (allow)="onAllowWrites(c)"
          (cancel)="confirming.set(null)"
        />
      }
    </main>
  `,
  styles: [
    `
      :host {
        display: block;
        --mat-button-outlined-container-height: 36px;
        --mat-button-outlined-horizontal-padding: 14px;
        --mat-button-outlined-label-text-size: 14px;
        --mat-button-outlined-label-text-weight: 400;
        --mat-button-outlined-disabled-label-text-color: var(--sc-muted);
        --mat-button-outlined-disabled-outline-color: var(--sc-line);
        --mat-button-filled-container-height: 36px;
        --mat-button-filled-horizontal-padding: 16px;
        --mat-button-filled-label-text-size: 14px;
        --mat-button-filled-label-text-weight: 400;
        --mat-button-filled-container-color: var(--sc-accent-strong);
        --mat-button-filled-label-text-color: var(--sc-on-accent);
        --mat-button-filled-disabled-container-color: var(--sc-surface-2);
        --mat-button-filled-disabled-label-text-color: var(--sc-muted);
      }
      .page {
        padding: 24px 32px;
        max-width: 1200px;
        box-sizing: border-box;
        display: flex;
        flex-direction: column;
        gap: 16px;
        color: var(--sc-fg);
        font-family:
          Inter,
          -apple-system,
          'Segoe UI',
          sans-serif;
      }
      .header {
        display: flex;
        flex-wrap: wrap;
        align-items: flex-end;
        gap: 12px;
      }
      .title-block {
        display: flex;
        flex-direction: column;
        gap: 4px;
        margin-right: auto;
      }
      h1 {
        margin: 0;
        font-size: 20px;
        font-weight: 600;
      }
      .lede {
        margin: 0;
        font-size: 14px;
        color: var(--sc-muted);
      }
      .lede a,
      .access-warning a {
        color: var(--sc-accent);
      }
      .header button sc-icon,
      .alert button sc-icon {
        margin-right: 8px;
      }
      .features-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(min(100%, 320px), 1fr));
        grid-auto-rows: 200px;
        gap: 16px;
      }
      .loading {
        display: flex;
        flex-direction: column;
        gap: 12px;
        --mat-progress-bar-track-height: 4px;
        --mat-progress-bar-active-indicator-height: 4px;
        --mat-progress-bar-track-shape: 2px;
        --mat-progress-bar-track-color: var(--sc-surface-2);
        --mat-progress-bar-active-indicator-color: var(--sc-accent);
      }
      .skeletons {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(min(100%, 320px), 1fr));
        grid-auto-rows: 200px;
        gap: 16px;
      }
      .skeleton {
        border: 1px solid var(--sc-line);
        border-radius: var(--sc-radius);
        background: var(--sc-surface);
        padding: 16px;
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .line {
        display: block;
        height: 12px;
        width: 90%;
        border-radius: 6px;
        background: var(--sc-surface-2);
      }
      .line--title {
        height: 16px;
        width: 35%;
      }
      .line--short {
        width: 70%;
      }
      .line--button {
        margin-top: auto;
        height: 36px;
        width: 140px;
        border-radius: 18px;
      }
      .panel-section {
        display: flex;
        flex-direction: column;
        gap: 14px;
      }
      .panel-section[hidden] {
        display: none;
      }
      .access-warning {
        margin: 0;
        display: flex;
        gap: 8px;
        align-items: flex-start;
        padding: 10px 12px;
        border-radius: 10px;
        background: var(--sc-warn-container);
        color: var(--sc-on-warn-container);
        font-size: 13px;
      }
      .access-warning span {
        color: var(--sc-fg);
      }
      .alert {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: 12px;
        padding: 12px 16px;
        border-radius: var(--sc-radius);
        border: 1px solid var(--sc-error);
        background: var(--sc-error-container);
        color: var(--sc-on-error-container);
      }
      .alert-icon {
        flex: none;
        color: var(--sc-error);
      }
      .alert-text {
        margin-right: auto;
        color: var(--sc-fg);
      }
      .retry {
        background: var(--sc-surface);
      }
    `,
  ],
})
export class FeaturesComponent implements OnInit {
  readonly store = inject(FeaturesStore);
  private readonly bridge = inject(ScoutBridgeService);
  private readonly notice = inject(NoticeService);
  private readonly dialog = inject(MatDialog);
  readonly noAllowedUsers = signal(false);
  readonly noAllowedUsersWarning = NO_ALLOWED_USERS_WARNING;
  readonly activeDialog = signal<FeatureViewModel | null>(null);
  readonly confirming = signal<WriteConfirmation | null>(null);
  readonly githubConnection = signal<Connection>('unknown');
  readonly kubernetesConnection = signal<Connection>('unknown');
  readonly youtubeConnection = signal<Connection>('unknown');
  readonly dockerStatus = signal<DockerCardStatus | null>(null);
  readonly skeletons = [1, 2, 3, 4, 5];
  /** The feature whose side panel is open; null while it is closed. */
  readonly selected = signal<PanelId | null>(null);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly githubConnect = viewChild(GitHubConnectComponent);
  private readonly kubernetesConnect = viewChild(KubernetesConnectComponent);
  private readonly youtubeConnect = viewChild(YouTubeConnectComponent);
  private readonly desktopVoice = viewChild(DesktopVoiceComponent);
  private readonly dockerCard = viewChild(DockerCardComponent);

  /** The cards in the catalog's order, Docker right after GitHub, each summed up from the state its panel holds. */
  readonly cards = computed<readonly CardView[]>(() => {
    const cards: CardView[] = [];
    for (const f of this.store.features()) {
      cards.push(this.featureCard(f));
      if (f.id === 'github') cards.push(this.dockerCardView());
    }
    return cards;
  });

  readonly panelCard = computed(() => this.cards().find((c) => c.id === this.selected()) ?? null);

  /** Opens the side panel on a feature; switching features keeps it open. */
  openPanel(id: PanelId): void {
    this.selected.set(id);
  }

  /** Closes the side panel and gives the focus back to the Details button of the card it showed. */
  closePanel(): void {
    const id = this.selected();
    this.selected.set(null);
    if (id === null) return;
    this.host.nativeElement.querySelector<HTMLElement>(`[data-test="details-${id}"]`)?.focus();
  }

  connectionOf(id: FeatureId): Connection | null {
    switch (id) {
      case 'github':
        return this.githubConnection();
      case 'kubernetes':
        return this.kubernetesConnection();
      case 'youtube':
        return this.youtubeConnection();
      case 'desktop':
        return 'connected';
      default:
        return null;
    }
  }

  private featureCard(f: FeatureViewModel): CardView {
    const connection = this.connectionOf(f.id);
    return {
      id: f.id,
      name: f.displayName,
      description: f.description,
      chip: featureChip(f, connection),
      counts: permissionCounts(f),
      noPermissions: null,
      ...this.summaryOf(f, connection),
    };
  }

  private summaryOf(f: FeatureViewModel, connection: Connection | null): Summary {
    switch (f.id) {
      case 'github':
        return this.githubSummary(f, connection);
      case 'kubernetes':
        return this.kubernetesSummary();
      case 'youtube':
        return this.youtubeSummary(f, connection);
      case 'desktop':
        return this.desktopSummary();
      default:
        return this.controlPlaneSummary(f);
    }
  }

  private writesLine(f: FeatureViewModel): string | null {
    if (f.writeClass === 'none') return null;
    return f.granted ? 'Writes allowed' : 'Writes not allowed';
  }

  private allowWrites(f: FeatureViewModel, connection: Connection | null): Summary['action'] {
    if (f.writeClass === 'none' || f.granted || connection !== 'connected') return null;
    return { label: 'Allow writes', icon: 'check', filled: false, disabled: f.busy };
  }

  private githubSummary(f: FeatureViewModel, connection: Connection | null): Summary {
    const gh = this.githubConnect();
    const s = gh?.status() ?? null;
    const allow = this.allowWrites(f, connection);
    const opens = { action: allow, run: allow === null ? null : () => this.openPanel(f.id) };
    if (gh === undefined || s === null) {
      const error = gh?.actionError() ?? null;
      return { fact: error ?? 'Checking GitHub...', factError: error !== null, ...opens };
    }
    if (s.connected) {
      const who = s.login === null ? 'Connected' : `Connected as ${s.login}`;
      return { fact: joined(who, this.writesLine(f)), factError: false, ...opens };
    }
    if (s.pending !== null) {
      return { fact: `Enter the code ${s.pending.userCode} on GitHub`, factError: false, ...opens };
    }
    return {
      fact: s.configured
        ? 'Not connected · Scout shows a one-time code to enter on GitHub'
        : 'The Scout GitHub App is not configured yet.',
      factError: false,
      action: {
        label: 'Connect with GitHub',
        icon: 'plug',
        filled: true,
        disabled: !s.configured || gh.busy(),
      },
      run: () => {
        void gh.connect();
        this.openPanel('github');
      },
    };
  }

  private kubernetesSummary(): Summary {
    const k = this.kubernetesConnect();
    const s = k?.status() ?? null;
    const d = k?.detection() ?? null;
    const none = { action: null, run: null };
    if (k !== undefined && s?.connected === true) {
      return { fact: `Using ${k.labelsOf(s)}`, factError: false, ...none };
    }
    if (s === null || d === null) {
      const error = k?.actionError() ?? null;
      return { fact: error ?? 'Checking Kubernetes...', factError: error !== null, ...none };
    }
    if (d.path === null) {
      return {
        fact: 'No Kubernetes configuration found on this computer',
        factError: false,
        ...none,
      };
    }
    if (d.error !== null) return { fact: d.error, factError: true, ...none };
    const n = d.contexts.length;
    return {
      fact: `${n} ${n === 1 ? 'context' : 'contexts'} found in ${d.path}`,
      factError: false,
      ...none,
    };
  }

  private youtubeSummary(f: FeatureViewModel, connection: Connection | null): Summary {
    const yt = this.youtubeConnect();
    const s = yt?.status() ?? null;
    const allow = this.allowWrites(f, connection);
    const opens = { action: allow, run: allow === null ? null : () => this.openPanel(f.id) };
    if (yt === undefined || s === null) {
      const error = yt?.actionError() ?? null;
      return { fact: error ?? 'Checking YouTube...', factError: error !== null, ...opens };
    }
    if (s.connected) {
      const who =
        s.method === 'apiKey'
          ? 'Connected with an API key'
          : `Connected to ${s.channelTitle ?? 'your channel'}`;
      return { fact: joined(who, this.writesLine(f)), factError: false, ...opens };
    }
    if (s.pending) return { fact: 'Waiting for the browser...', factError: false, ...opens };
    return {
      fact: s.configured
        ? 'Not connected · sign in with Google or add an API key'
        : 'Not connected · add an API key, or a Google OAuth client to sign in',
      factError: false,
      action: {
        label: 'Sign in with Google',
        icon: 'plug',
        filled: true,
        disabled: !s.configured || yt.busy(),
      },
      run: () => void yt.signIn(),
    };
  }

  private desktopSummary(): Summary {
    const voice = this.desktopVoice();
    if (voice === undefined) {
      return { fact: 'Voice model: checking...', factError: false, action: null, run: null };
    }
    const state = voice.state();
    const fact = voice.line();
    switch (state.kind) {
      case 'none':
        return {
          fact,
          factError: false,
          action: { label: 'Download', icon: 'download', filled: true, disabled: false },
          run: () => void voice.openDialog(),
        };
      case 'failed':
        return {
          fact,
          factError: true,
          action: { label: 'Retry', icon: 'refresh', filled: false, disabled: false },
          run: () => void voice.download(state.variant),
        };
      case 'unknown':
        return {
          fact,
          factError: true,
          action: { label: 'Try again', icon: 'refresh', filled: false, disabled: false },
          run: () => void voice.refresh(),
        };
      default:
        return { fact, factError: false, action: null, run: null };
    }
  }

  private controlPlaneSummary(f: FeatureViewModel): Summary {
    const failed = f.status === 'error' && f.lastError !== null;
    return {
      fact: failed ? (f.lastError ?? '') : f.description,
      factError: failed,
      action: {
        label: f.hasCredentials ? 'Update credentials' : 'Add credentials',
        icon: f.hasCredentials ? 'edit' : 'plug',
        filled: true,
        disabled: f.busy,
      },
      run: () => this.openDialog(f),
    };
  }

  private dockerCardView(): CardView {
    const d = this.dockerCard();
    const base = {
      id: 'docker' as const,
      name: 'Docker',
      description: NOTHING_BUT_DOCKER,
      counts: null,
      noPermissions: DOCKER_PERMISSIONS,
    };
    if (d === undefined) {
      return {
        ...base,
        chip: { label: 'Checking...', tone: 'off' },
        fact: 'Scout is checking Docker on this computer.',
        factError: false,
        action: null,
        run: null,
      };
    }
    const tone = d.chipTone();
    const chip: CardChip = { label: d.chipLabel(), tone: tone === 'neutral' ? 'off' : tone };
    const images = d.images();
    const fact =
      d.running() && images !== null
        ? `Images: ${images.length - d.missingCount()} of ${images.length} downloaded`
        : d.statusLine();
    const state = d.state()?.state ?? null;
    let action: CardAction | null = null;
    let run: (() => void) | null = null;
    if (state === 'notInstalled') {
      action = { label: 'How to install Docker', icon: 'external', filled: false, disabled: false };
      run = () => void d.openInstallGuide();
    } else if (state === 'notRunning' || d.stateFailed()) {
      action = { label: 'Refresh', icon: 'refresh', filled: false, disabled: d.refreshing() };
      run = () => void d.refresh();
    } else if (state === 'running' && d.githubConnected() && !d.enabled()) {
      action = { label: 'Turn on', icon: 'check', filled: false, disabled: d.switchBusy() };
      run = () => void d.enable(true);
    } else if (state === 'running' && d.neededMissing().length > 0) {
      action = { label: 'Download images', icon: 'download', filled: false, disabled: d.pulling() };
      run = () => void d.downloadNeeded();
    }
    return { ...base, chip, fact, factError: false, action, run };
  }

  async ngOnInit(): Promise<void> {
    void this.bridge.accessGet().then(
      (res) => this.noAllowedUsers.set(res.userIds.length === 0),
      () => this.noAllowedUsers.set(false),
    );
    await this.store.load();
  }

  /** Reads the feature list again; the progress bar shows until the control plane answers. */
  async reload(): Promise<void> {
    await this.store.load();
  }

  /** Clearing credentials cannot be undone, so it asks first with Cancel focused. */
  async onClearCredentials(feature: FeatureViewModel): Promise<void> {
    const confirmed = await confirmAction(this.dialog, {
      title: `Clear the ${feature.displayName} credentials?`,
      body: `Scout deletes the ${feature.displayName} credentials from this computer's protected storage. You can add them again later.`,
      confirmLabel: 'Clear credentials',
    });
    if (confirmed) await this.store.clearCredentials(feature.id);
  }

  onToggle(feature: FeatureViewModel): void {
    if (!feature.enabled && feature.writeClass !== 'none') {
      this.confirming.set({ feature, purpose: 'enable' });
      return;
    }
    void this.store.toggle(feature.id);
  }

  async onAllowWrites(confirmation: WriteConfirmation): Promise<void> {
    this.confirming.set(null);
    const { id, displayName } = confirmation.feature;
    if (confirmation.purpose === 'enable') {
      await this.store.toggle(id, true);
      return;
    }
    await this.store.setWriteGrant(id, true, true);
    if (this.errorOf(id) === null) this.notice.show(`Writes allowed for ${displayName}`, 'success');
  }

  async onRevokeWrites(feature: FeatureViewModel): Promise<void> {
    await this.store.setWriteGrant(feature.id, false, false);
    if (this.errorOf(feature.id) === null)
      this.notice.show(`Writes revoked for ${feature.displayName}`, 'info');
  }

  private errorOf(id: FeatureId): string | null {
    return this.store.features().find((f) => f.id === id)?.lastError ?? this.store.lastError();
  }

  openDialog(feature: FeatureViewModel): void {
    this.activeDialog.set(feature);
  }

  closeDialog(): void {
    this.activeDialog.set(null);
  }

  async onDialogSubmit(
    id: FeatureId,
    payload: { credentials: Record<string, string>; settings: Record<string, string> },
  ): Promise<void> {
    this.closeDialog();
    await this.store.setup(id, payload.credentials, payload.settings);
    const feature = this.store.features().find((f) => f.id === id);
    if (feature !== undefined && this.errorOf(id) === null)
      this.notice.show(`${feature.displayName} credentials saved`, 'success');
  }
}
