import { describe, expect, it } from 'vitest';
import { BuildingBatchRenderer } from './buildingRenderer';
import { fixedPumpHouseFixture } from './buildingFixture';

class FakeGl {
  readonly VERTEX_SHADER = 1; readonly FRAGMENT_SHADER = 2; readonly COMPILE_STATUS = 3;
  readonly LINK_STATUS = 4; readonly ARRAY_BUFFER = 5; readonly STATIC_DRAW = 6;
  readonly FLOAT = 7; readonly TRIANGLES = 8; readonly DEPTH_TEST = 9;
  readonly BLEND = 10; readonly CULL_FACE = 11; readonly CURRENT_PROGRAM = 12;
  readonly ARRAY_BUFFER_BINDING = 13;
  readonly deleted: string[] = [];
  drawCalls = 0;
  createShader(): object { return {}; }
  shaderSource(): void {}
  compileShader(): void {}
  getShaderParameter(): boolean { return true; }
  getShaderInfoLog(): string { return ''; }
  deleteShader(): void { this.deleted.push('shader'); }
  createProgram(): object { return {}; }
  attachShader(): void {}
  linkProgram(): void {}
  getProgramParameter(): boolean { return true; }
  getProgramInfoLog(): string { return ''; }
  deleteProgram(): void { this.deleted.push('program'); }
  createBuffer(): object { return {}; }
  bindBuffer(): void {}
  bufferData(): void {}
  getAttribLocation(_program: object, name: string): number { return name.length; }
  getUniformLocation(): object { return {}; }
  createVertexArray(): object { return {}; }
  bindVertexArray(): void {}
  deleteVertexArray(): void { this.deleted.push('vao'); }
  getExtension(): null { return null; }
  enableVertexAttribArray(): void {}
  vertexAttribPointer(): void {}
  getParameter(): null { return null; }
  isEnabled(): boolean { return false; }
  useProgram(): void {}
  uniformMatrix4fv(): void {}
  enable(): void {}
  disable(): void {}
  drawArrays(): void { this.drawCalls += 1; }
  deleteBuffer(): void { this.deleted.push('buffer'); }
}

describe('batched native building renderer', () => {
  it('generates one batch for the fixed Graphics Lab pump-house fixture', () => {
    const renderer = new BuildingBatchRenderer();
    renderer.setBuildings([fixedPumpHouseFixture([-121.47, 46.92])]);
    const gl = new FakeGl();
    renderer.onAdd({ triggerRepaint: () => {} } as never, gl as never);
    expect(renderer.getMeshCount()).toBe(1);
    expect(renderer.getVertexCount()).toBeGreaterThan(0);
    renderer.render(gl as never, { modelViewProjectionMatrix: new Float32Array(16) } as never);
    expect(gl.drawCalls).toBe(1);
    renderer.setVisible(false);
    renderer.render(gl as never, { modelViewProjectionMatrix: new Float32Array(16) } as never);
    expect(gl.drawCalls).toBe(1);
    renderer.onRemove({ triggerRepaint: () => {} } as never, gl as never);
    expect(gl.deleted).toEqual(['vao', 'buffer', 'program', 'shader', 'shader']);
  });

  it('keeps committed meshes during capture while excluding the placement draft', () => {
    const renderer = new BuildingBatchRenderer();
    renderer.setBuildings([fixedPumpHouseFixture([-121.47, 46.92])]);
    renderer.setDraft({ ...fixedPumpHouseFixture([-121.47, 46.92]), id: 'draft', draft: true });
    const gl = new FakeGl();
    renderer.onAdd({ triggerRepaint: () => {} } as never, gl as never);
    const withDraft = renderer.getVertexCount();
    renderer.setCaptureTransient(true);
    renderer.render(gl as never, { modelViewProjectionMatrix: new Float32Array(16) } as never);
    expect(renderer.getVertexCount()).toBeLessThan(withDraft);
    expect(renderer.getMeshCount()).toBe(1);
    renderer.onRemove({ triggerRepaint: () => {} } as never, gl as never);
  });
});

