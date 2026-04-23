const DISCORD_AUTHORIZE = 'https://discord.com/oauth2/authorize';

export interface AuthorizeUrlParams {
  clientId: string;
  permissions: string;
  redirectUri: string;
  state: string;
  codeChallenge: string;
}

export function buildAuthorizeUrl(params: AuthorizeUrlParams): string {
  const query = new URLSearchParams({
    client_id: params.clientId,
    scope: 'bot applications.commands',
    permissions: params.permissions,
    redirect_uri: params.redirectUri,
    response_type: 'code',
    state: params.state,
    code_challenge: params.codeChallenge,
    code_challenge_method: 'S256',
  });
  return `${DISCORD_AUTHORIZE}?${query.toString()}`;
}
