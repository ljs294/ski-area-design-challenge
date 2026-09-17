import { BrowserDesignStorage } from './browserDesignStorage';
import { DesignSaveRepository } from './designSaveRepository';
import { desktop } from './desktopBridge';
import type {
  DesignLoadResponse, DesignSaveDraft, DesignSaveResponse, DesignSaveSummary,
} from './types/designSave';

let browserRepository: DesignSaveRepository | null = null;

function browser(): DesignSaveRepository {
  browserRepository ??= new DesignSaveRepository(new BrowserDesignStorage());
  return browserRepository;
}

export function saveDesign(draft: DesignSaveDraft): Promise<DesignSaveResponse> {
  return desktop ? desktop.designs.save(draft) : browser().save(draft);
}

export function loadDesign(key: string): Promise<DesignLoadResponse> {
  return desktop ? desktop.designs.load(key) : browser().load(key);
}

export function listDesigns(): Promise<DesignSaveSummary[]> {
  return desktop ? desktop.designs.list() : browser().list();
}
