/**
 * The AppImage this process runs from, or null: `APPIMAGE` counts only when the running executable sits inside the
 * mount `APPDIR` names, because a program started from another AppImage inherits that program's variables.
 */
export function ownAppImage(
  env: Readonly<Record<string, string | undefined>>,
  execPath: string,
): string | null {
  const appImage = env['APPIMAGE'] ?? '';
  const appDir = env['APPDIR'] ?? '';
  if (appImage === '' || appDir === '') return null;
  return execPath.startsWith(appDir.endsWith('/') ? appDir : `${appDir}/`) ? appImage : null;
}

/** The program a login launch runs: Scout's own AppImage when it runs from one, else this executable. */
export function ownExecutable(
  env: Readonly<Record<string, string | undefined>>,
  execPath: string,
): string {
  return ownAppImage(env, execPath) ?? execPath;
}
