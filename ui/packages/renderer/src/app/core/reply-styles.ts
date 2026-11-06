/**
 * The look of a conversation turn, shared by the Chat screen and the quick-ask window: who spoke
 * (the owner's bubble on the right, Scout's reply under its mark on the left), the dots while Scout
 * works and the line that says what it is doing, and the rendered Markdown of a reply.
 */
export const REPLY_STYLES = `
  .turn {
    display: flex;
    flex-direction: column;
    gap: 10px;
    min-width: 0;
  }
  .row {
    display: flex;
    gap: 10px;
    align-items: flex-start;
    min-width: 0;
  }
  .row.user {
    flex-direction: row-reverse;
  }
  .col {
    display: flex;
    flex-direction: column;
    gap: 4px;
    min-width: 0;
    flex: 1 1 auto;
  }
  .row.user .col {
    align-items: flex-end;
  }
  .avatar {
    flex: none;
    width: 26px;
    height: 26px;
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 11px;
    font-weight: 600;
  }
  .avatar svg {
    width: 16px;
    height: 16px;
  }
  .avatar.user {
    background: #1e2e45;
    color: #c3cadb;
  }
  .avatar.scout {
    background: #3b6ff2;
    color: #ffffff;
  }
  .who {
    margin: 0;
    font-size: 11px;
    color: #8a93a6;
    display: flex;
    gap: 6px;
    align-items: center;
  }
  .question {
    margin: 0;
    max-width: 80%;
    width: fit-content;
    background: #1e2e45;
    border-radius: 10px 2px 10px 10px;
    padding: 8px 12px;
    white-space: pre-wrap;
    font-size: 14px;
  }
  .with-transcript {
    margin: 0;
    width: fit-content;
    font-size: 11px;
    color: #8a93a6;
  }
  .working {
    margin: 0;
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 13px;
    color: #8a93a6;
    min-width: 0;
  }
  .working code {
    font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
    font-size: 12px;
    color: #c3cadb;
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
    background: #8a93a6;
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
    border-left: 2px solid #252b3b;
    padding-left: 12px;
  }
  .reply.failed {
    color: #d45a5a;
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
    color: #f3f5f9;
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
    font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
    font-size: 12.5px;
    background: #1a2436;
    border: 1px solid #253249;
    border-radius: 4px;
    padding: 1px 5px;
  }
  .reply pre {
    margin: 0 0 10px;
    padding: 10px 12px;
    background: #0b111c;
    border: 1px solid #253249;
    border-radius: 8px;
    overflow-x: auto;
    font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
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
    border-left: 3px solid #3b6ff2;
    color: #c3cadb;
  }
  .reply a {
    color: #7aa2f7;
  }
  .reply hr {
    border: 0;
    border-top: 1px solid #253249;
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
    border: 1px solid #253249;
    padding: 4px 8px;
    text-align: left;
  }
  .citations {
    margin: 6px 0 0;
    padding-left: 18px;
    font-size: 13px;
  }
  .citations a {
    color: #7aa2f7;
  }
`;
