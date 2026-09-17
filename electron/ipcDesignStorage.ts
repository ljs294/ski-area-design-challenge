import { app, ipcMain } from 'electron';
import path from 'path';
import { DesignSaveRepository } from '../src/designSaveRepository';
import {
  DESIGN_LOAD_CHANNEL, DESIGN_LIST_CHANNEL, DESIGN_SAVE_CHANNEL,
  type DesignLoadIpcResponse, type DesignLoadRequest, type DesignListIpcResponse,
  type DesignSaveIpcResponse, type DesignSaveRequest,
} from '../src/ipcContract';
import { FileDesignStorage } from './designFileStorage';

export function registerDesignStorageHandlers(): void {
  const repository = new DesignSaveRepository(
    new FileDesignStorage(path.join(app.getPath('userData'), 'three-designs')),
  );
  ipcMain.handle(DESIGN_SAVE_CHANNEL, (_event, request: DesignSaveRequest): Promise<DesignSaveIpcResponse> =>
    repository.save(request.draft));
  ipcMain.handle(DESIGN_LOAD_CHANNEL, (_event, request: DesignLoadRequest): Promise<DesignLoadIpcResponse> =>
    repository.load(request.key));
  ipcMain.handle(DESIGN_LIST_CHANNEL, (): Promise<DesignListIpcResponse> => repository.list());
}
