/** Who signed a write on GitHub, from its login: "Bot (<login>)" when the login is a bot's, else "Owner (<login>)". */
export function signerInWords(login: string): string {
  return login.endsWith('[bot]') ? `Bot (${login})` : `Owner (${login})`;
}
