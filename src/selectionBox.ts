import L from 'leaflet';
import type { AreaSizeMeters } from './types';
import { boundsForSquareMeters } from './geo';

export interface SelectionBoxOptions {
  initialCenter: L.LatLngExpression;
  initialSizeMeters: AreaSizeMeters;
  onChange?: (center: L.LatLng, sizeMeters: AreaSizeMeters) => void;
}

/**
 * A repositionable, fixed-size-preset square selection overlay on a Leaflet
 * map. Hand-rolled rather than using leaflet-draw: the interaction is
 * narrow (reposition + snap to one of a few preset sizes, no freeform
 * resize/rotate), so a small custom implementation keeps full control over
 * styling and avoids an unmaintained dependency.
 *
 * The box is a true real-world-meters square (via the cos(lat) correction
 * below), which means it can render very slightly trapezoidal on screen at
 * high latitude under Web Mercator — that's expected and correct, not a bug.
 */
export class SelectionBox {
  private map: L.Map;
  private rectangle: L.Rectangle;
  private center: L.LatLng;
  private sizeMeters: AreaSizeMeters;
  private onChange?: (center: L.LatLng, sizeMeters: AreaSizeMeters) => void;
  private isDragging = false;
  private suppressNextClick = false;

  constructor(map: L.Map, options: SelectionBoxOptions) {
    this.map = map;
    this.center = L.latLng(options.initialCenter);
    this.sizeMeters = options.initialSizeMeters;
    this.onChange = options.onChange;

    this.rectangle = L.rectangle(this.computeBounds(), {
      color: '#2a6cd1',
      weight: 2,
      fillColor: '#2a6cd1',
      fillOpacity: 0.1,
    }).addTo(map);

    this.rectangle.on('mousedown', this.handleBoxMouseDown);
    this.map.on('click', this.handleMapClick);
  }

  private computeBounds(): L.LatLngBounds {
    const b = boundsForSquareMeters(this.center.lat, this.center.lng, this.sizeMeters);
    return L.latLngBounds(L.latLng(b.south, b.west), L.latLng(b.north, b.east));
  }

  private handleBoxMouseDown = (e: L.LeafletMouseEvent): void => {
    L.DomEvent.stopPropagation(e);
    this.isDragging = true;
    this.map.dragging.disable();
    this.map.on('mousemove', this.handleMapMouseMove);
    this.map.once('mouseup', this.handleMapMouseUp);
  };

  private handleMapMouseMove = (e: L.LeafletMouseEvent): void => {
    if (!this.isDragging) return;
    this.center = e.latlng;
    this.rectangle.setBounds(this.computeBounds());
  };

  private handleMapMouseUp = (): void => {
    this.isDragging = false;
    this.suppressNextClick = true;
    this.map.dragging.enable();
    this.map.off('mousemove', this.handleMapMouseMove);
    this.onChange?.(this.center, this.sizeMeters);
  };

  private handleMapClick = (e: L.LeafletMouseEvent): void => {
    if (this.suppressNextClick) {
      this.suppressNextClick = false;
      return;
    }
    this.center = e.latlng;
    this.rectangle.setBounds(this.computeBounds());
    this.onChange?.(this.center, this.sizeMeters);
  };

  public setSizeMeters(size: AreaSizeMeters): void {
    this.sizeMeters = size;
    this.rectangle.setBounds(this.computeBounds());
    this.onChange?.(this.center, this.sizeMeters);
  }

  public setCenter(center: L.LatLngExpression): void {
    this.center = L.latLng(center);
    this.rectangle.setBounds(this.computeBounds());
    this.onChange?.(this.center, this.sizeMeters);
  }

  public getCenter(): L.LatLng {
    return this.center;
  }

  public getSizeMeters(): AreaSizeMeters {
    return this.sizeMeters;
  }

  public getBounds(): L.LatLngBounds {
    return this.rectangle.getBounds();
  }

  public destroy(): void {
    this.map.off('click', this.handleMapClick);
    this.map.off('mousemove', this.handleMapMouseMove);
    this.rectangle.off('mousedown', this.handleBoxMouseDown);
    this.map.removeLayer(this.rectangle);
  }
}
