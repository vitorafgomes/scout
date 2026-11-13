/**
 * The look of a conversation turn, shared by the Chat screen and the quick-ask window: the owner's
 * question as an accent bubble on the right under "You", Scout's reply on the left under a header
 * with its mark and name, the dots while Scout works and the line that says what it is doing, and
 * the rendered Markdown of a reply.
 */
export const REPLY_STYLES = `
  .turn {
    display: flex;
    flex-direction: column;
    gap: 12px;
    min-width: 0;
  }
  .row {
    display: flex;
    flex-direction: column;
    min-width: 0;
  }
  .row.user {
    align-items: flex-end;
    gap: 4px;
  }
  .row.scout {
    gap: 8px;
  }
  .avatar {
    flex: none;
    width: 22px;
    height: 22px;
    border-radius: 6px;
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--sc-surface-2);
    color: var(--sc-accent);
  }
  .avatar svg {
    width: 14px;
    height: 14px;
  }
  .who {
    margin: 0;
    font-size: 12px;
    color: var(--sc-muted);
    display: flex;
    gap: 8px;
    align-items: center;
  }
  .who .name {
    font-size: 13px;
    font-weight: 600;
    color: var(--sc-fg);
  }
  .question {
    margin: 0;
    max-width: 80%;
    width: fit-content;
    background: var(--sc-accent-strong);
    color: var(--sc-on-accent);
    border-radius: 14px 14px 4px 14px;
    padding: 8px 12px;
    white-space: pre-wrap;
    font-size: 14px;
  }
  .with-transcript {
    margin: 0;
    width: fit-content;
    font-size: 11px;
    color: var(--sc-muted);
  }
  .working {
    margin: 0;
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 13px;
    color: var(--sc-muted);
    min-width: 0;
  }
  .working code {
    font-family: var(--sc-mono);
    font-size: 12px;
    color: var(--sc-fg-2);
  }
  .dots {
    display: inline-flex;
    gap: 4px;
    align-items: center;
    height: 16px;
    flex: none;
  }
  .dots i {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: var(--sc-muted);
    animation: sc-blink 1.2s infinite ease-in-out;
  }
  .dots i:nth-child(2) {
    animation-delay: 0.2s;
  }
  .dots i:nth-child(3) {
    animation-delay: 0.4s;
  }
  @keyframes sc-blink {
    0%,
    80%,
    100% {
      opacity: 0.25;
      transform: translateY(0);
    }
    40% {
      opacity: 1;
      transform: translateY(-2px);
    }
  }
  @media (prefers-reduced-motion: reduce) {
    .dots i {
      animation: none;
      opacity: 0.7;
    }
  }
  .reply {
    margin: 0;
    font-size: 14px;
    line-height: 1.5;
    min-width: 0;
    overflow-wrap: anywhere;
    color: var(--sc-fg-2);
  }
  .reply.failed {
    color: var(--sc-error);
  }
  .reply > :first-child {
    margin-top: 0;
  }
  .reply > :last-child {
    margin-bottom: 0;
  }
  .reply h1,
  .reply h2,
  .reply h3,
  .reply h4 {
    margin: 14px 0 6px;
    font-weight: 600;
    line-height: 1.3;
    color: var(--sc-fg);
  }
  .reply h1 {
    font-size: 18px;
  }
  .reply h2 {
    font-size: 16px;
  }
  .reply h3,
  .reply h4 {
    font-size: 14px;
  }
  .reply p {
    margin: 0 0 8px;
  }
  .reply ul,
  .reply ol {
    margin: 0 0 8px;
    padding-left: 22px;
  }
  .reply li {
    margin: 2px 0;
  }
  .reply code {
    font-family: var(--sc-mono);
    font-size: 12.5px;
    background: var(--sc-code-bg);
    border: 1px solid var(--sc-line);
    border-radius: 4px;
    padding: 1px 5px;
  }
  .reply pre {
    margin: 0 0 10px;
    padding: 10px 12px;
    background: var(--sc-code-bg);
    border: 1px solid var(--sc-line);
    border-radius: 8px;
    overflow-x: auto;
    font-family: var(--sc-mono);
    font-size: 12.5px;
    line-height: 1.5;
  }
  .reply pre code {
    background: none;
    border: 0;
    padding: 0;
  }
  .reply blockquote {
    margin: 0 0 8px;
    padding: 2px 12px;
    border-left: 3px solid var(--sc-accent-strong);
    color: var(--sc-fg-2);
  }
  .reply a {
    color: var(--sc-accent);
  }
  .reply hr {
    border: 0;
    border-top: 1px solid var(--sc-line);
    margin: 12px 0;
  }
  .reply table {
    border-collapse: collapse;
    margin: 0 0 10px;
    font-size: 13px;
    display: block;
    overflow-x: auto;
  }
  .reply th,
  .reply td {
    border: 1px solid var(--sc-line);
    padding: 4px 8px;
    text-align: left;
  }
  .citations {
    margin: 6px 0 0;
    padding-left: 18px;
    font-size: 13px;
  }
  .citations a {
    color: var(--sc-accent);
  }
`;
