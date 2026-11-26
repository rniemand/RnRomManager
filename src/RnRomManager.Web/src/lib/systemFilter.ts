import type { SystemInfo, FilterOption } from '#lib/types/index.js';

/** The System menu's options (Games page L3, reports F1): systems with ROMs, plus the selected one so it shows. */
export function systemFilterOptions(
	systems: SystemInfo[],
	selected: string | null
): FilterOption[] {
	return systems
		.filter((s) => s.romCount > 0 || s.id === selected)
		.map((s) => ({
			value: s.id,
			label: s.displayName,
			badge: { system: s.id, name: s.shortName },
			search: [s.shortName]
		}));
}
