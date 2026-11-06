/** Google search links (RV2, GP2). Opened only by an explicit click. */
export const googleSearchUrl = (query: string): string =>
	`https://www.google.com/search?q=${encodeURIComponent(query)}`;

/** Drops every `(…)`/`[…]` group, turns `_` and `.` into spaces, collapses spaces. */
export function cleanName(name: string): string {
	return name
		.replace(/\([^)]*\)|\[[^\]]*\]/g, ' ')
		.replace(/[_.]/g, ' ')
		.replace(/\s+/g, ' ')
		.trim();
}

const alphanumerics = (s: string) => (s.match(/[\p{L}\p{N}]/gu) ?? []).length;

/** RV2: `"<name>" <system> rom`, the header title when usable, else the file name; the CRC32 when nothing is left. */
export function reviewSearchQuery(
	item: { originalFileName: string; crc32: string; header: Record<string, unknown> | null },
	systemName: string
): string {
	const title = typeof item.header?.title === 'string' ? item.header.title.trim() : '';
	// Only a file name has an extension to drop.
	const name = cleanName(
		title && alphanumerics(title) >= 3 ? title : item.originalFileName.replace(/\.[^.]*$/, '')
	);
	return name ? `"${name}" ${systemName} rom` : `${item.crc32} ${systemName} rom`;
}
