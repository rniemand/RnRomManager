import catalog from './systemEmulators.json';
import type { Emulator } from '#lib/types/index.js';
import type { EmulatorOs } from '#lib/enums/index.js';

/** Known emulators per system id; the keys are checked against the handlers by `SystemEmulatorsCatalogTests`. */
export const systemEmulators = catalog as Record<string, Emulator[]>;

/** The modal's groups, in display order (B7). */
export const emulatorOsList: { os: EmulatorOs; label: string; icon: string }[] = [
	{ os: 'windows', label: 'Windows', icon: 'os-windows' },
	{ os: 'macos', label: 'macOS', icon: 'os-macos' },
	{ os: 'linux', label: 'Linux', icon: 'os-linux' },
	{ os: 'android', label: 'Android', icon: 'os-android' },
	{ os: 'ios', label: 'iOS', icon: 'os-ios' }
];

/** Non-empty OS groups for a system, in order; `[]` for an unknown or empty system. */
export function emulatorGroups(systemId: string) {
	const emulators = Object.hasOwn(systemEmulators, systemId) ? systemEmulators[systemId] : [];
	return emulatorOsList
		.map((group) => ({ ...group, emulators: emulators.filter((e) => e.os.includes(group.os)) }))
		.filter((group) => group.emulators.length > 0);
}

/** Where a link goes, shown next to it (B7). */
export const linkHost = (url: string) => new URL(url).hostname;
