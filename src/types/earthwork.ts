export interface EarthworkEstimate {
  /** Gross material removed from the original DEM. */
  cutM3: number;
  /** Gross material added above the original DEM. */
  fillM3: number;
  /** Positive means excess cut; negative means additional fill is required. */
  balanceM3: number;
}
