/** How often a recurring maintenance amount is charged. */
export type MaintenanceCadence = 'unspecified' | 'daily' | 'monthly';

/** Optional economics shared by every player-built asset. */
export interface AssetEconomics {
  capitalCostUsd: number | null;
  maintenanceCostUsd: number | null;
  maintenanceCadence: MaintenanceCadence;
}

/** Economics for a newly authored asset. Costs remain deliberately TBD. */
export const TBD_ASSET_ECONOMICS: AssetEconomics = Object.freeze({
  capitalCostUsd: null,
  maintenanceCostUsd: null,
  maintenanceCadence: 'unspecified',
});

/** Stable presentation text for the null/TBD economics contract. */
export function formatAssetCostUsd(amountUsd: number | null): string {
  return amountUsd == null ? 'TBD' : `$${amountUsd.toLocaleString('en-US', {
    maximumFractionDigits: 2,
  })}`;
}

export function formatAssetEconomics(economics: AssetEconomics): {
  capital: string;
  maintenance: string;
} {
  return {
    capital: formatAssetCostUsd(economics.capitalCostUsd),
    maintenance: formatAssetCostUsd(economics.maintenanceCostUsd),
  };
}
