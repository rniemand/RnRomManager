import { api } from '../api/client';
import type { BundleEntry, BundleMember, BundleResolvedItem, Me } from '../api/types';

/** B2: the most files a cart or saved queue holds. */
export const maxBundleEntries = 500;

/** B3: labels and file names are capped at `VARCHAR(255)`, counted in code points. */
export const maxBundleText = 255;

/** The `localStorage` key of a user's working cart (B2). */
export const cartStorageKey = (userId: number) => `rnrm.bundle.cart.${userId}`;

const sha1Pattern = /^[0-9a-f]{40}$/;
const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/** Length in Unicode code points, as the server counts it. */
export const codePoints = (s: string) => [...s].length;

/** Cuts a label to `maxBundleText` code points. */
export const capText = (s: string) =>
	codePoints(s) > maxBundleText ? [...s].slice(0, maxBundleText).join('') : s;

/** The identity part of an entry: what the server resolves and what duplicates are judged by. */
export type BundleIdentity = Pick<BundleEntry, 'gameId' | 'releaseSha1' | 'member'>;

/**
 * The key two identities share exactly when they name the same file (B1): main and set files differ, set-file names
 * compare exactly, so `Track 2.bin` and `track 2.bin` are different files. Mirrors `BundleFile.Key` on the server.
 */
export function entryKey(identity: BundleIdentity): string {
	const base = `${identity.gameId.replaceAll('-', '').toLowerCase()}/${identity.releaseSha1}`;
	return identity.member ? `${base}/${identity.member.sha1}/${identity.member.fileName}` : base;
}

const isText = (v: unknown): v is string =>
	typeof v === 'string' && v.length > 0 && codePoints(v) <= maxBundleText;

function isMember(v: unknown): v is BundleMember {
	const m = v as BundleMember;
	return (
		typeof m === 'object' && m !== null && sha1Pattern.test(String(m.sha1)) && isText(m.fileName)
	);
}

/** True for a structurally valid entry (B3), the same rules the server applies. */
export function isBundleEntry(v: unknown): v is BundleEntry {
	const e = v as BundleEntry;
	return (
		typeof e === 'object' &&
		e !== null &&
		guidPattern.test(String(e.gameId).toLowerCase()) &&
		!/^[0-]+$/.test(String(e.gameId)) &&
		sha1Pattern.test(String(e.releaseSha1)) &&
		(e.member === null || isMember(e.member)) &&
		isText(e.gameTitle) &&
		isText(e.fileName)
	);
}

/** Only the wire fields, labels capped, in a fixed order. */
function normalize(entry: BundleEntry): BundleEntry {
	return {
		gameId: entry.gameId.toLowerCase(),
		releaseSha1: entry.releaseSha1,
		member: entry.member ? { sha1: entry.member.sha1, fileName: entry.member.fileName } : null,
		gameTitle: capText(entry.gameTitle),
		fileName: capText(entry.fileName)
	};
}

/** Valid, unique entries in order, at most `maxBundleEntries`; anything else is dropped. */
export function cleanEntries(input: unknown): BundleEntry[] {
	if (!Array.isArray(input)) return [];
	// A local, non-reactive lookup: it never leaves this function.
	// eslint-disable-next-line svelte/prefer-svelte-reactivity
	const seen = new Set<string>();
	const out: BundleEntry[] = [];
	for (const raw of input) {
		if (out.length >= maxBundleEntries) break;
		const candidate =
			raw && typeof raw === 'object'
				? {
						...raw,
						gameTitle: capText(String(raw.gameTitle ?? '')),
						fileName: capText(String(raw.fileName ?? ''))
					}
				: raw;
		if (!isBundleEntry(candidate)) continue;
		const entry = normalize(candidate);
		const key = entryKey(entry);
		if (seen.has(key)) continue;
		seen.add(key);
		out.push(entry);
	}
	return out;
}

/** A cart row: the entry, its availability after the last refresh and the names to show. */
export interface CartItem {
	key: string;
	entry: BundleEntry;
	/** `unknown` until a refresh answers for this entry. */
	status: 'unknown' | 'available' | 'unavailable';
	/** The current names when available, else the last-known labels. */
	gameTitle: string;
	fileName: string;
	downloadUrl: string | null;
}

export type AddResult = 'added' | 'duplicate' | 'full' | 'signedOut' | 'invalid';

/**
 * B1–B3, B5: the signed-in user's working cart. It lives in this browser's `localStorage` under the user's id and is
 * restored on load; signing out or switching accounts clears it from memory, and results of requests started for a
 * previous account are ignored.
 */
class BundleCart {
	entries = $state<BundleEntry[]>([]);
	/** Availability by entry key from the latest refresh. */
	resolved = $state<Record<string, BundleResolvedItem>>({});
	refreshing = $state(false);
	refreshError = $state(false);
	#userId: number | null = null;
	/** Bumped on every account change and every content change, so stale refresh answers are dropped. */
	#generation = 0;

	get userId(): number | null {
		return this.#userId;
	}

	get count(): number {
		return this.entries.length;
	}

	get full(): boolean {
		return this.entries.length >= maxBundleEntries;
	}

	get items(): CartItem[] {
		return this.entries.map((entry) => {
			const key = entryKey(entry);
			const r = this.resolved[key];
			const status = r ? (r.available ? 'available' : 'unavailable') : 'unknown';
			return {
				key,
				entry,
				status,
				gameTitle: (r?.available && r.gameTitle) || entry.gameTitle,
				fileName: (r?.available && r.fileName) || entry.fileName,
				downloadUrl: r?.available ? r.downloadUrl : null
			};
		});
	}

	/** Called whenever `me` changes: loads that user's saved cart, or empties it when signed out. */
	follow(me: Me | null): void {
		const id = me?.id ?? null;
		if (id === this.#userId) return;
		this.#userId = id;
		this.#generation++;
		this.resolved = {};
		this.refreshing = false;
		this.refreshError = false;
		this.entries = id === null ? [] : this.#read(id);
	}

	has(identity: BundleIdentity): boolean {
		const key = entryKey(identity);
		return this.entries.some((e) => entryKey(e) === key);
	}

	/** Appends one file (B1); duplicates of an identity already in the cart are rejected. */
	add(entry: BundleEntry): AddResult {
		if (this.#userId === null) return 'signedOut';
		const candidate = {
			...entry,
			gameTitle: capText(entry.gameTitle),
			fileName: capText(entry.fileName)
		};
		if (!isBundleEntry(candidate)) return 'invalid';
		if (this.has(candidate)) return 'duplicate';
		if (this.full) return 'full';
		this.#change([...this.entries, normalize(candidate)]);
		return 'added';
	}

	remove(key: string): void {
		this.#change(this.entries.filter((e) => entryKey(e) !== key));
	}

	clear(): void {
		this.#change([]);
	}

	/** Replaces the whole cart (loading a saved queue, B2); entries are cleaned like stored ones. */
	replace(entries: BundleEntry[]): void {
		if (this.#userId === null) return;
		this.#change(cleanEntries(entries));
		this.resolved = {};
	}

	/** True when the cart holds exactly these identities in this order (no confirmation needed to replace it). */
	sameAs(entries: BundleIdentity[]): boolean {
		return (
			entries.length === this.entries.length &&
			entries.every((e, i) => entryKey(e) === entryKey(this.entries[i]))
		);
	}

	/**
	 * B5: asks the server which entries are available now and with which names and links. The answer is dropped when
	 * the account or the cart changed meanwhile; a failure keeps the previous results and sets `refreshError`.
	 */
	async refresh(): Promise<void> {
		if (this.#userId === null) return;
		const generation = this.#generation;
		const snapshot = this.entries.slice();
		if (snapshot.length === 0) {
			this.resolved = {};
			return;
		}
		this.refreshing = true;
		this.refreshError = false;
		try {
			const answer = await api.post<{ items: BundleResolvedItem[] }>('/api/bundle-files/resolve', {
				items: snapshot.map((e) => ({
					gameId: e.gameId,
					releaseSha1: e.releaseSha1,
					member: e.member
				}))
			});
			if (generation !== this.#generation) return;
			const resolved: Record<string, BundleResolvedItem> = {};
			let relabelled = false;
			const entries = snapshot.map((entry, i) => {
				const item = answer.items[i];
				if (!item) return entry;
				resolved[entryKey(entry)] = item;
				// Keep the last-known labels current, so an entry that later goes missing shows its latest names.
				if (
					item.available &&
					item.gameTitle &&
					item.fileName &&
					(item.gameTitle !== entry.gameTitle || item.fileName !== entry.fileName)
				) {
					relabelled = true;
					return { ...entry, gameTitle: capText(item.gameTitle), fileName: capText(item.fileName) };
				}
				return entry;
			});
			this.resolved = resolved;
			if (relabelled) {
				this.entries = entries;
				this.#write();
			}
		} catch {
			if (generation === this.#generation) this.refreshError = true;
		} finally {
			if (generation === this.#generation) this.refreshing = false;
		}
	}

	#change(entries: BundleEntry[]): void {
		this.#generation++;
		this.refreshing = false;
		this.entries = entries;
		this.#write();
	}

	#read(userId: number): BundleEntry[] {
		try {
			const raw = localStorage.getItem(cartStorageKey(userId));
			if (!raw) return [];
			const parsed = JSON.parse(raw) as { version?: unknown; entries?: unknown };
			return parsed?.version === 1 ? cleanEntries(parsed.entries) : [];
		} catch {
			return [];
		}
	}

	#write(): void {
		if (this.#userId === null) return;
		try {
			if (this.entries.length === 0) localStorage.removeItem(cartStorageKey(this.#userId));
			else
				localStorage.setItem(
					cartStorageKey(this.#userId),
					JSON.stringify({ version: 1, entries: this.entries })
				);
		} catch {
			// Storage can be unavailable or full: the cart still works for this page.
		}
	}
}

export const bundle = new BundleCart();
