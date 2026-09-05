import { describe, expect, it } from 'vitest';
import { TOOL_IDS } from './toolCoordinator';
import { sectionForTool, dockForSection, constructionStage } from './workspaceModel';

describe('workspace navigation', () => {
  it('routes every supported construction tool to an existing domain owner', () => {
    for (const tool of TOOL_IDS) expect(dockForSection(sectionForTool(tool))).not.toBeNull();
    expect(sectionForTool('building')).toBe('snowmaking');
    expect(sectionForTool('guest-portal')).toBe('infrastructure');
    expect(sectionForTool('ski-path')).toBe('trails');
    expect(dockForSection('weather')).toBeNull();
  });
  it('keeps review and construction distinct from drawing', () => {
    expect(constructionStage('review')).toBe(2);
    expect(constructionStage('building')).toBe(3);
    expect(constructionStage('drawing')).toBe(1);
    expect(constructionStage('configure')).toBe(0);
  });
});
