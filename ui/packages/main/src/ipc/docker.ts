import type { IpcMain, WebContents } from 'electron';
import {
  CHANNEL,
  type DockerImagesResponse,
  type DockerLastRunResponse,
  type DockerPullImageRequest,
  type DockerPullRequest,
  type DockerRepositoriesResponse,
} from '@scout/shared';
import type { Docker } from '../docker/docker.js';

/** The Docker card's channels; the download's progress goes back only to the window that asked for it. */
export function registerDockerHandlers(
  ipcMain: Pick<IpcMain, 'handle' | 'removeHandler'>,
  docker: Docker,
): () => void {
  ipcMain.handle(CHANNEL.dockerState, () => docker.state());
  ipcMain.handle(CHANNEL.dockerImages, async (): Promise<DockerImagesResponse> => ({
    images: await docker.images(),
  }));
  ipcMain.handle(CHANNEL.dockerPull, (event, req: Partial<DockerPullRequest> | undefined) => {
    const sender: WebContents = event.sender;
    const stack = typeof req?.stack === 'string' ? req.stack : '';
    return docker.pull(stack, (progress) => {
      if (!sender.isDestroyed()) sender.send(CHANNEL.dockerPullProgress, progress);
    });
  });
  ipcMain.handle(
    CHANNEL.dockerPullImage,
    (event, req: Partial<DockerPullImageRequest> | undefined) => {
      const sender: WebContents = event.sender;
      const reference = typeof req?.reference === 'string' ? req.reference : '';
      return docker.pullImage(reference, (progress) => {
        if (!sender.isDestroyed()) sender.send(CHANNEL.dockerPullProgress, progress);
      });
    },
  );
  ipcMain.handle(CHANNEL.dockerWorkFolder, () => docker.workFolder());
  ipcMain.handle(CHANNEL.dockerClear, () => docker.clear());
  ipcMain.handle(CHANNEL.dockerRepositories, async (): Promise<DockerRepositoriesResponse> => ({
    repositories: await docker.repositories(),
  }));
  ipcMain.handle(CHANNEL.dockerLastRun, async (): Promise<DockerLastRunResponse> => ({
    run: await docker.lastRun(),
  }));

  return () => {
    ipcMain.removeHandler(CHANNEL.dockerLastRun);
    ipcMain.removeHandler(CHANNEL.dockerPullImage);
    ipcMain.removeHandler(CHANNEL.dockerRepositories);
    ipcMain.removeHandler(CHANNEL.dockerClear);
    ipcMain.removeHandler(CHANNEL.dockerWorkFolder);
    ipcMain.removeHandler(CHANNEL.dockerPull);
    ipcMain.removeHandler(CHANNEL.dockerImages);
    ipcMain.removeHandler(CHANNEL.dockerState);
  };
}
