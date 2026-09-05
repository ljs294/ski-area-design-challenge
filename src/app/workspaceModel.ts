import type { DockId, ToolId } from './toolCoordinator';

export type WorkspaceSection = 'resort' | 'lifts' | 'trails' | 'snowmaking' | 'infrastructure' | 'guests' | 'layers' | 'weather';
export interface WorkspaceRequest { section: WorkspaceSection; view?: 'browse' | 'network' | 'analysis' }
export const WORKSPACE_LABELS: Record<WorkspaceSection, string> = {
  resort: 'Resort', lifts: 'Lifts', trails: 'Trails', snowmaking: 'Snowmaking',
  infrastructure: 'Infrastructure', guests: 'Guests', layers: 'Layers', weather: 'Weather',
};
export function sectionForTool(tool: ToolId): WorkspaceSection {
  if (tool === 'lift') return 'lifts';
  if (tool === 'trail' || tool === 'ski-node' || tool === 'ski-path') return 'trails';
  if (tool === 'road' || tool === 'guest-portal') return 'infrastructure';
  return 'snowmaking';
}
export function dockForSection(section: WorkspaceSection): DockId | null {
  return ['lifts', 'trails', 'snowmaking', 'infrastructure', 'layers'].includes(section) ? section as DockId : null;
}
export function constructionStage(phase: string): number {
  if (phase === 'choosing' || phase === 'configuring' || phase === 'configure' || phase === 'type') return 0;
  if (phase === 'review') return 2;
  if (phase === 'building' || phase === 'committing') return 3;
  return 1;
}
