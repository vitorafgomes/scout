import type { FeatureScopeAccess, FeatureScopeRowDto } from '@scout/shared';

/** A feature of the control plane's catalog as a test needs it: its description and its rows. */
export interface CatalogFixture {
  readonly description: string;
  readonly writeClass: 'none' | 'executive';
  readonly rows: readonly FeatureScopeRowDto[];
}

function row(
  scope: string,
  access: FeatureScopeAccess,
  summary: string,
  actions: readonly string[],
): FeatureScopeRowDto {
  return { scope, summary, actions, access };
}

/** The descriptions and rows of the control plane's feature catalog, word for word, for specs that render a panel. */
export const CATALOG: Readonly<
  Record<'github' | 'kubernetes' | 'youtube' | 'desktop', CatalogFixture>
> = {
  github: {
    description:
      'Query repositories, issues and pull requests from chat; review a pull request and audit a whole project. With your write permission it also posts reviews and comments, opens issues and gives verdicts, each one only after you approve it.',
    writeClass: 'executive',
    rows: [
      row('Metadata', 'read', 'List the repositories the Scout GitHub App is installed on', [
        'List the repositories the Scout GitHub App is installed on',
      ]),
      row(
        'Contents',
        'read',
        'Read files, directories and languages; list and search every file to audit a project',
        [
          'Read the README, languages and top-level files of a repository',
          'Read one file or list one directory of a repository, at a branch, tag or commit, for a code review',
          'List every file of a repository and search its code, to audit a whole project',
        ],
      ),
      row(
        'Pull requests',
        'read',
        'List and read pull requests, their diffs, comments, checks and reviews, and the account Scout signs in as',
        [
          'List open pull requests',
          'Read one pull request with its changed files and their diffs',
          'Read the comments on a pull request',
          'Read which account Scout signs in as, so it never approves a pull request that account opened',
          "Read the check runs and commit statuses of a pull request's head commit, and the reviews already given on it",
        ],
      ),
      row('Issues', 'read', 'List open issues and read one issue', [
        'List open issues',
        'Read one issue',
      ]),
      row(
        'Notifications',
        'read',
        "Search every five minutes for your mentions, review requests and replies; read the pull requests that mention your Scout bot and the bot's own name",
        [
          'Search your GitHub activity every five minutes for the bell: mentions, review requests and replies on your pull requests and issues',
          'When your Scout bot is installed, search the open pull requests on its account for a mention of the bot, and read the description, the comments, the review comments and the reviews of each pull request found, to confirm the mention before the bell shows it',
          "When your Scout bot exists, read its GitHub App's own name and handle at start and whenever the GitHub card reads the bot, so a rename on GitHub is followed",
        ],
      ),
      row(
        "Scout's data folder",
        'write',
        'Keep conventions profiles, repository snapshots and audit jobs on this computer',
        [
          'Keep the conventions profile of a repository Scout audited, so later audits judge its code against its own rules',
          'Keep a snapshot of a repository at one commit, within a size cap (200 MB unless configured), so a review or an audit reads every file locally and the Knowledge index can search it',
          'Keep the audit job of a repository, its plan, progress and findings, when you ask for a whole-repository audit in chat; the audit reads the snapshot and pauses or stops when you ask',
        ],
      ),
      row(
        'Pull request reviews',
        'irreversible',
        'Post a review with comments; your click on Post on a review card is the approval, and one asked in chat waits for you in Approvals',
        [
          "Post a review with comments on a pull request; this review only comments, it never approves or requests changes. A review notifies the pull request's author and everyone following it, so each one needs your approval in Scout Desktop: your click on Post on a review card is that approval, and a review asked for in chat waits for you to approve it",
        ],
      ),
      row(
        'Pull request verdicts',
        'irreversible',
        'Approve a pull request or request changes; your choice on a review card is the approval, and one asked in chat waits for you in Approvals',
        [
          "Approve a pull request or request changes on it when you choose that verdict on the review card; a verdict stays in the pull request's history, so each one runs only after you approve that action in Scout Desktop",
          'Approve a pull request or request changes on it when you ask for it in chat; each verdict waits for you to approve it in Scout Desktop',
        ],
      ),
      row(
        'Pull request comments',
        'irreversible',
        'Comment on a pull request or reply to a review comment; each one waits for you in Approvals',
        [
          "Comment on a pull request's conversation when you ask for it in chat; a comment notifies everyone following the pull request, so each one waits for you to approve that comment in Scout Desktop",
          'Reply to a review comment on a pull request when you ask for it in chat; each reply waits for you to approve it in Scout Desktop',
        ],
      ),
      row(
        'New issues',
        'irreversible',
        'Open an issue; your click on Open issues on a findings card is the approval, and one asked in chat waits for you in Approvals',
        [
          "Open an issue on a repository when you ask for it in chat; an issue notifies the repository's watchers and stays in its history, so each one waits for you to approve that issue in Scout Desktop",
          'Open the issues a findings card lists when you click Open issues on it; that click is your approval of exactly those issues',
        ],
      ),
      row(
        'Issue comments',
        'irreversible',
        'Comment on an existing issue; each one waits for you in Approvals',
        [
          'Comment on an existing issue when you ask for it in chat; a comment notifies everyone following the issue, so each one waits for you to approve that comment in Scout Desktop',
        ],
      ),
      row(
        'Local container',
        'write',
        "Run a pull request's tests in a disposable container on this computer",
        [
          "Run a pull request's own tests in a disposable container on this computer, over the snapshot in Scout's data folder; the container sees no file, secret or variable of yours and, during the tests, no network",
        ],
      ),
      row(
        'Scout bot on GitHub (optional)',
        'write',
        "Sign what you switched to the bot with its token, on the repositories you installed it on; you create the bot on GitHub's page and its key stays on this computer",
        [
          'Sign the reviews, verdicts, comments and issues you switched to the bot with its token, on the repositories you installed it on; on any other repository Scout signs as you and says so, and every read still uses your sign-in',
          "Creating the bot is your own action in the desktop, like connecting GitHub: you confirm the GitHub App on GitHub's page, and its private key stays in this computer's protected storage",
        ],
      ),
    ],
  },
  kubernetes: {
    description:
      'Read pods, deployments and events from your clusters. Uses the Kubernetes configuration on this computer, with your consent; read-only.',
    writeClass: 'none',
    rows: [
      row('Namespaces', 'read', 'List the namespaces of each context you chose', [
        'List the namespaces of each context you chose',
      ]),
      row('Pods', 'read', 'Read pod status and restarts; never logs or secrets', [
        'Read pod status and restarts; never logs or secrets',
      ]),
      row('Deployments', 'read', 'Read replicas and rollout status', [
        'Read replicas and rollout status',
      ]),
      row('Events', 'read', 'Read recent events', ['Read recent events']),
    ],
  },
  youtube: {
    description:
      "Reads a channel's overview, uploads, statistics, search results and comments; your own channel after you sign in. With your write permission it also produces videos under Scout's folder and publishes what you approve.",
    writeClass: 'executive',
    rows: [
      row('channels.list', 'read', 'Title, handle, subscribers, total views and video count', [
        "Read a channel's title, handle, subscribers, total views and video count",
      ]),
      row('playlistItems.list', 'read', 'The latest uploads of a channel', [
        'List the latest uploads of a channel',
      ]),
      row('videos.list', 'read', 'Title, duration, views, likes and comment count', [
        "Read a video's title, duration, views, likes and comment count",
      ]),
      row('search.list', 'read', 'Search YouTube for videos on a topic', [
        'Search YouTube for videos on a topic',
      ]),
      row('commentThreads.list', 'read', 'The comments on your videos', [
        'Read the comments on your videos',
      ]),
      row(
        'youtubeAnalytics reports.query',
        'read',
        'Views, watch time, retention and subscribers gained',
        ["Read a video's views, watch time, retention and subscribers gained"],
      ),
      row('youtubeReporting reports.list', 'read', 'Thumbnail impressions and click-through rate', [
        "Read the reach reports with a video's thumbnail impressions and click-through rate",
      ]),
      row(
        'api.openverse.org and the host of the picture it names',
        'read',
        'Find a CC0, public domain or CC BY picture for a scene on Openverse and download it',
        [
          "Search Openverse for a CC0, public domain or CC BY picture for a scene without one, and download the picture it names into the project's media folder",
        ],
      ),
      row(
        "Scout's data folder",
        'write',
        'Save scripts, narration, images, the scene list, the video and thumbnails, and the recording you attach with its transcript',
        [
          'Save scripts, narration, images, the scene list, the video and thumbnails, and keep the recording you attach and its transcript',
        ],
      ),
      row(
        'youtubeReporting jobs.create',
        'write',
        "Create the channel's reach reporting job once",
        ["Create the channel's reach reporting job once, so YouTube prepares the reach reports"],
      ),
      row('videos.insert', 'irreversible', 'Public, private, unlisted or scheduled', [
        'Upload a video as public, private or unlisted, or as private with a time YouTube publishes it',
      ]),
      row('videos.update', 'irreversible', "Change a video's title during a title test", [
        "Change a video's title during a title test: to each title the test tries, and then to the title kept when it ends",
      ]),
      row('thumbnails.set', 'irreversible', "Set a video's thumbnail", ["Set a video's thumbnail"]),
      row('playlistItems.insert', 'irreversible', 'Add a video to a playlist', [
        'Add a video to a playlist',
      ]),
      row('comments.insert', 'irreversible', 'Publish a reply to a comment', [
        'Publish a reply to a comment',
      ]),
      row(
        'comments.setModerationStatus',
        'irreversible',
        'Hold a comment for your review on YouTube',
        ['Hold a comment for your review on YouTube; Scout never publishes or rejects a comment'],
      ),
    ],
  },
  desktop: {
    description:
      'What Scout can see and hear on this computer when you ask from the desktop. Nothing is captured unless you click.',
    writeClass: 'none',
    rows: [
      row('Screen', 'read', 'One screenshot you attach to a question, and the text read from it', [
        'Send one screenshot with a question when you attach it',
        'Read the text on a screenshot with the bundled OCR program',
      ]),
      row('Microphone', 'read', 'Your voice while you hold to talk, listen or record a meeting', [
        'Record a voice question',
        'Listen while you speak',
        'Listen while you record a meeting',
      ]),
      row(
        'System audio',
        'read',
        "The computer's sound while you keep System audio on or record a meeting",
        [
          'Listen to what this computer plays while you keep System audio on',
          'Listen to what this computer plays while you record a meeting',
        ],
      ),
    ],
  },
};
