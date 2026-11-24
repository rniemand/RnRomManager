<script lang="ts">
	import { scrollActiveTab } from '#lib/scrollActiveTab.js';
	import { untrack } from 'svelte';
	import { SvelteMap, SvelteSet } from 'svelte/reactivity';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type {
		BundleEntry,
		CollectionRef,
		GameDetail,
		ReleaseFile,
		RomDetail
	} from '#lib/api/types.js';
	import { collectionsApi } from '#lib/collections.js';
	import ConfirmModal from '#lib/components/ConfirmModal.svelte';
	import CopyButton from '#lib/components/CopyButton.svelte';
	import GameAdminModal, {
		type AdminAction,
		type AdminOutcome
	} from '#lib/components/GameAdminModal.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import GameImages from '#lib/components/GameImages.svelte';
	import GameSaves from '#lib/components/GameSaves.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import FileSize from '#lib/components/FileSize.svelte';
	import GameCollectionsModal from '#lib/components/GameCollectionsModal.svelte';
	import SystemEmulatorsModal from '#lib/components/SystemEmulatorsModal.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import { hotkeys } from '#lib/stores/hotkeys.svelte.js';
	import InfoJsonModal from '#lib/components/InfoJsonModal.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import RatingControl from '#lib/components/RatingControl.svelte';
	import GameTitle from '#lib/components/GameTitle.svelte';
	import ReleaseNoteModal from '#lib/components/ReleaseNoteModal.svelte';
	import EditReleaseModal, { type ReleaseKind } from '#lib/components/EditReleaseModal.svelte';
	import EmulatorModal from '#lib/components/EmulatorModal.svelte';
	import {
		canPlay,
		opensInNewTab,
		playLinkEvents,
		playUrl,
		setMedia,
		usesPlayDisc
	} from '#lib/emulator.js';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import ZipBrowser from '#lib/components/ZipBrowser.svelte';
	import { formatDateTime, formatKey, formatSize, formatValue } from '#lib/format.js';
	import { gameSearchQuery, googleSearchUrl } from '#lib/googleSearch.js';
	import { renderMarkdown } from '#lib/markdown.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { bundle, maxBundleEntries, type AddResult } from '#lib/stores/bundle.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import {
		compareDlc,
		compareUpdates,
		discNumber,
		isDlc,
		isUpdate,
		orderByDisc
	} from '#lib/releases.js';
	import { systemColour } from '#lib/systemColours.js';
	import { tooltip } from '#lib/tooltip.js';

	const changedMessage = 'This game changed, please retry.';

	let game = $state<GameDetail | null>(null);
	let error = $state<unknown>(null);
	let notFound = $state(false);
	let notice = $state<string | null>(null);
	let headerRom = $state<RomDetail | null>(null);
	let headerOpen = $state(false);
	let reload = $state(0);
	let action = $state<AdminAction | null>(null);
	let infoJsonOpen = $state(false);
	let noteRom = $state<RomDetail | null>(null);
	/** Release editing B2: the release the Edit release modal edits. */
	let editRom = $state<RomDetail | null>(null);
	let editOpen = $state(false);
	let noteOpen = $state(false);
	let noteMode = $state<'edit' | undefined>();
	let playRom = $state<RomDetail | null>(null);
	/** Multi-disc M7: the disc set's playlist name while All discs is playing; null for a single release. */
	let playSet = $state<string | null>(null);
	let playOpen = $state(false);
	/** B16/B25: the caller's collections holding this game. */
	let memberOf = $state<CollectionRef[]>([]);
	let collectionsOpen = $state(false);
	let emulatorsOpen = $state(false);
	let emulatorsButton = $state<HTMLButtonElement>();
	// E2: Bootstrap doesn't restore focus for a modal opened with show(), so return it to the button.
	let emulatorsWasOpen = false;
	$effect(() => {
		if (emulatorsWasOpen && !emulatorsOpen) emulatorsButton?.focus();
		emulatorsWasOpen = emulatorsOpen;
	});

	const id = $derived(page.params.id ?? '');
	// P3: the cover (any type); screenshots are in the Images gallery.
	const cover = $derived(game?.images.find((i) => i.isCover));
	// P1: primary first, then the Releases order.
	// G5: a release pointer means the game's ROMs are stored next to its ZIP.
	const external = $derived(game?.roms.some((r) => r.externalFileName !== null) ?? false);
	// B9/B11 (DLC B1–B3): updates and DLC have their own tabs; the rest are the releases.
	const releases = $derived(orderByDisc(game?.roms.filter((r) => !isUpdate(r) && !isDlc(r)) ?? []));
	const updates = $derived(game ? game.roms.filter(isUpdate).sort(compareUpdates) : []);
	const dlc = $derived(game ? game.roms.filter(isDlc).sort(compareDlc) : []);
	// B10 (DLC B5): the header download lists releases only; else updates, else DLC.
	const downloadable = $derived(releases.length ? releases : updates.length ? updates : dlc);
	const downloadRoms = $derived([
		...downloadable.filter((r) => r.isPrimary),
		...downloadable.filter((r) => !r.isPrimary)
	]);

	async function fetchGame(gameId: string, signal?: AbortSignal): Promise<GameDetail | null> {
		try {
			return await api.get<GameDetail>(`/api/games/${encodeURIComponent(gameId)}`, { signal });
		} catch (e) {
			if (isApiError(e) && e.status === 404) return null;
			throw e;
		}
	}

	$effect(() => {
		const gameId = id;
		void reload;
		const controller = new AbortController();
		error = null;
		game = null;
		notFound = false;
		headerOpen = false;
		fetchGame(gameId, controller.signal).then(
			(g) => {
				game = g;
				notFound = g === null;
			},
			(e) => {
				if (!controller.signal.aborted) error = e;
			}
		);
		return () => controller.abort();
	});

	$effect(() => {
		const gameId = id;
		let stale = false;
		memberOf = [];
		collectionsApi.forGame(gameId).then(
			(list) => {
				if (!stale) memberOf = list;
			},
			() => {} // the badges are extra; the game itself shows any error
		);
		return () => (stale = true);
	});

	/**
	 * Shows a refetched or edited game only while the route still shows it: a response for game A that lands after a
	 * hotkey or link moved the page to game B must not replace B.
	 */
	function setGame(detail: GameDetail): void {
		if (detail.id.toLowerCase() === id.toLowerCase()) game = detail;
	}

	/**
	 * §11: ROM ids change on every rewrite, so check the release still exists before handing the URL to the browser;
	 * a stale one reloads the detail and asks for a retry instead of downloading a 404 body.
	 */
	async function download(url: string, rom?: RomDetail): Promise<void> {
		notice = null;
		let fresh: GameDetail | null;
		try {
			fresh = await fetchGame(id);
		} catch (e) {
			error = e;
			return;
		}
		if (fresh === null) {
			game = null;
			notFound = true;
		} else if (rom && !fresh.roms.some((r) => r.id === rom.id)) {
			setGame(fresh);
			notice = changedMessage;
		} else {
			window.location.assign(url);
		}
	}

	/** Bundle Creator B1/B3: a cart entry from the rendered metadata; `file` names a set file, else the main file. */
	function bundleEntry(g: GameDetail, rom: RomDetail, file?: ReleaseFile): BundleEntry {
		return {
			gameId: g.id,
			releaseSha1: rom.sha1,
			member: file ? { sha1: file.sha1, fileName: file.fileName } : null,
			gameTitle: g.displayTitle,
			fileName: file?.fileName ?? rom.fileName
		};
	}

	const bundleMessages: Record<AddResult, [string, 'success' | 'info' | 'warning' | 'danger']> = {
		added: ['Added {file} to the bundle.', 'success'],
		duplicate: ['{file} is already in the bundle.', 'info'],
		full: [`The bundle holds at most ${maxBundleEntries} files.`, 'warning'],
		signedOut: ['Sign in to use the bundle.', 'warning'],
		invalid: ["{file} can't be added to the bundle.", 'danger']
	};

	function addToBundle(entry: BundleEntry): void {
		const [text, kind] = bundleMessages[bundle.add(entry)];
		toasts.show(text.replace('{file}', entry.fileName), kind);
	}

	/**
	 * B27: `?play=<romId>` (from Global Search) opens the Play modal for that release once the game is loaded, also on
	 * the already-open game; then only `play` leaves the URL (`tab` stays). A release not in the game, one that can't
	 * play, or a PSP one (it opens in a new tab) is ignored and removed all the same.
	 */
	$effect(() => {
		const playId = page.url.searchParams.get('play');
		const current = game;
		if (playId === null || !current || current.id !== id) return;
		untrack(() => {
			const rom = current.roms.find((r) => String(r.id) === playId);
			if (
				rom &&
				canPlay(current, rom) &&
				current.browserCore &&
				!opensInNewTab(current.browserCore)
			) {
				playRom = rom;
				playSet = null;
				playOpen = true;
			}
			const next = new URL(page.url.href);
			next.searchParams.delete('play');
			// eslint-disable-next-line svelte/no-navigation-without-resolve -- same page, query only
			void goto(`${next.pathname}${next.search}`, { replace: true, reset: false });
		});
	});

	/** Release editing B3: reload the renamed release, then show the tab it now belongs to. */
	async function onReleaseEdited(kind: ReleaseKind): Promise<void> {
		await refreshGame();
		selectTab(kind === 'release' ? 'releases' : kind === 'update' ? 'updates' : 'dlc');
	}

	function showHeader(rom: RomDetail): void {
		headerRom = rom;
		headerOpen = true;
	}

	function showNote(rom: RomDetail, mode?: 'edit'): void {
		noteRom = rom;
		noteMode = mode;
		noteOpen = true;
	}

	/**
	 * B7: each open card's rendered note, by file name (ROM ids change on every ZIP rewrite), loaded on first expand.
	 * `pendingNotes` holds file names whose note was just saved: their ids are stale until the refetch lands.
	 */
	type NoteState = { loading: true } | { text: string } | { error: unknown };
	const notes = new SvelteMap<string, NoteState>();
	const pendingNotes = new SvelteSet<string>();

	async function loadNote(rom: RomDetail): Promise<void> {
		const fileName = rom.fileName;
		notes.set(fileName, { loading: true });
		try {
			const text = await api.getText(`/api/games/${encodeURIComponent(id)}/roms/${rom.id}/note`);
			notes.set(fileName, { text });
		} catch (e) {
			if (isApiError(e) && e.status === 404) {
				// Deleted meanwhile, as the modal treats it: the block goes and the game is refetched.
				notes.delete(fileName);
				rom.hasNote = false;
				await refreshGame();
			} else notes.set(fileName, { error: e });
		}
	}

	$effect(() => {
		for (const rom of shownRoms)
			if (
				rom.hasNote &&
				openReleases.has(rom.fileName) &&
				!notes.has(rom.fileName) &&
				!pendingNotes.has(rom.fileName)
			)
				untrack(() => void loadNote(rom));
	});

	/**
	 * A note write rewrites the ZIP, and the mirror gives every ROM a new id (§11): flip the icon now, then quietly
	 * refetch so the next note/download/edit uses current ids. B7: the cached note goes (open or not), and an open
	 * card reloads it with the new id once the refetch lands.
	 */
	async function onNoteSaved(romId: number, hasNote: boolean): Promise<void> {
		const rom = game?.roms.find((r) => r.id === romId);
		if (rom) {
			rom.hasNote = hasNote;
			pendingNotes.add(rom.fileName);
		}
		try {
			const fresh = await fetchGame(id);
			if (fresh) setGame(fresh);
		} catch {
			// The flipped icon stands; a stale id later reloads and asks for a retry as usual.
		} finally {
			if (rom) {
				notes.delete(rom.fileName);
				pendingNotes.delete(rom.fileName);
			}
		}
	}

	/** A save change rewrote the ZIP and renumbered the release ids: reload them. */
	async function refreshGame(): Promise<void> {
		try {
			const fresh = await fetchGame(id);
			if (fresh) setGame(fresh);
		} catch {
			// A stale id later reloads and asks for a retry as usual.
		}
	}

	async function onAdminDone(outcome: AdminOutcome): Promise<void> {
		if ('game' in outcome) {
			game = outcome.game;
			toasts.show(outcome.message, 'success');
		} else if ('goTo' in outcome) {
			toasts.show(outcome.message, 'success');
			await goto(outcome.goTo ? resolve('/games/[id]', { id: outcome.goTo }) : resolve('/'));
		} else {
			if (outcome.warning) notice = outcome.message ?? null;
			else if (outcome.message) toasts.show(outcome.message, 'success');
			reload++;
		}
	}

	const list = (items: string[]) => (items.length ? items.join(', ') : '—');

	/** R2: how a release was identified. */
	const identifiedBy = {
		Dat: 'DAT',
		Manual: 'Manual',
		TitleDb: 'Title DB',
		Serial: 'Game ID'
	} as const;

	/** R3: the header's key/value pairs, two per table row. */
	function headerRows(header: Record<string, unknown>): [string, unknown][][] {
		const entries = Object.entries(header);
		const rows: [string, unknown][][] = [];
		for (let i = 0; i < entries.length; i += 2) rows.push(entries.slice(i, i + 2));
		return rows;
	}

	// R1–R4: release cards start collapsed. Open ones are kept by file name (unique within a game; ROM ids change on
	// every ZIP rewrite), so a reload of the same game keeps them; a new game id starts collapsed again.
	const openReleases = new SvelteSet<string>();
	// Z1: the Zip Contents tree's selection and expansion; kept across reloads, reset (root selected) for a new game id.
	let zipSelected = $state('');
	// Z1: empty; the browser expands every folder on a game's first load.
	const zipExpanded = new SvelteSet<string>();
	$effect(() => {
		void id;
		untrack(() => {
			openReleases.clear();
			openFiles.clear();
			notes.clear();
			zipSelected = '';
			zipExpanded.clear();
		});
	});
	// B20: which releases' file lists are open, by file name like openReleases.
	const openFiles = new SvelteSet<string>();
	const allReleasesOpen = $derived.by(() => shownRoms.every((r) => openReleases.has(r.fileName)));

	function toggleRelease(fileName: string): void {
		if (openReleases.has(fileName)) openReleases.delete(fileName);
		else openReleases.add(fileName);
	}

	/** R2: empty header space toggles; its buttons and links don't, nor does ending a text selection (copying the name). */
	function onReleaseHeaderClick(event: MouseEvent, fileName: string): void {
		if ((event.target as Element).closest('button, a')) return;
		if (getSelection()?.toString()) return;
		toggleRelease(fileName);
	}

	function toggleAllReleases(): void {
		if (allReleasesOpen) openReleases.clear();
		else shownRoms.forEach((r) => openReleases.add(r.fileName));
	}

	// GP1: the tab is URL state (`?tab=images`); switching never reloads the game (the load effect keys on the id).
	type Tab = 'releases' | 'updates' | 'dlc' | 'images' | 'saves' | 'zip';
	// B9 (DLC B2): ?tab=updates or ?tab=dlc on a game without any shows Releases, as an unknown tab does.
	const tab = $derived<Tab>(
		(
			[
				'images',
				'saves',
				'zip',
				...(updates.length ? ['updates' as const] : []),
				...(dlc.length ? ['dlc' as const] : [])
			] as const
		).find((t) => t === page.url.searchParams.get('tab')) ?? 'releases'
	);
	// B10 (DLC B4): one panel lists the Releases, Updates or DLC cards.
	const shownRoms = $derived(tab === 'updates' ? updates : tab === 'dlc' ? dlc : releases);
	const romTab = $derived(tab === 'updates' || tab === 'dlc' ? tab : 'releases');
	/** B4: each tab's value, label and icon. */
	const tabs = $derived<[Tab, string, string][]>([
		['releases', `Releases (${releases.length})`, 'releases'],
		...(updates.length
			? [['updates', `Updates (${updates.length})`, 'update'] as [Tab, string, string]]
			: []),
		...(dlc.length ? [['dlc', `DLC (${dlc.length})`, 'dlc'] as [Tab, string, string]] : []),
		['images', `Images (${game?.images.length ?? 0})`, 'images'],
		['saves', `Saves (${game?.saveCount ?? 0})`, 'save'],
		['zip', 'Zip Contents', 'zip']
	]);

	/** B19: bumped after the Play dialog saved to the library, so the Saves tab reloads. */
	let savesVersion = $state(0);

	/** Missing Metadata M7: opened from that report; kept across tab switches. */
	const fromMissingMetadata = $derived(page.url.searchParams.get('from') === 'missing-metadata');

	function selectTab(next: Tab): void {
		const path = resolve('/games/[id]', { id });
		const query = [
			fromMissingMetadata && 'from=missing-metadata',
			next !== 'releases' && `tab=${next}`
		]
			.filter(Boolean)
			.join('&');
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- the resolved path plus a query
		void goto(query ? `${path}?${query}` : path, { replace: true, reset: false });
	}
</script>

<svelte:head><title>{game?.displayTitle ?? 'Game'} · RnRomManager</title></svelte:head>

<p>
	<a class="btn btn-sm btn-outline-primary" href={resolve('/')}
		><Icon name="back" /> Back to Library</a
	>
</p>

{#if error}
	<ErrorAlert {error} onretry={() => reload++} />
{:else if notFound}
	<EmptyState icon="not-found" message="This game doesn't exist any more.">
		{#snippet action()}
			<a class="btn btn-primary" href={resolve('/')}>Go to the Library</a>
		{/snippet}
	</EmptyState>
{:else if !game}
	<Loading label="Loading game…" />
{:else}
	{#if notice}
		<div class="alert alert-warning alert-dismissible" role="alert">
			{notice}
			<button type="button" class="btn-close" aria-label="Dismiss" onclick={() => (notice = null)}
			></button>
		</div>
	{/if}

	<div class="row g-4 mb-4">
		<div class="col-12 col-md-4 col-lg-3">
			{#if cover}
				<img
					class="img-fluid rounded border"
					src={cover.url}
					width={cover.width}
					height={cover.height}
					alt="{game.displayTitle} cover"
				/>
			{/if}
		</div>
		<div class="col-12 col-md-8 col-lg-9">
			<div class="d-flex flex-wrap align-items-center gap-3 mb-2">
				{#if auth.isAdmin}
					<GameTitle
						{game}
						onsaved={setGame}
						onstale={() => onAdminDone({ reload: true, message: changedMessage, warning: true })}
					/>
				{:else}
					<h1 class="h3 mb-0">{game.displayTitle}</h1>
				{/if}
				<RatingControl
					gameId={game.id}
					title={game.displayTitle}
					bind:rating={game.rating}
					bind:favourite={game.favourite}
					readonly={!auth.isAdmin}
					onsaved={setGame}
				/>
			</div>
			{#if memberOf.length}
				<ul class="list-inline mb-2" aria-label="In collections">
					{#each memberOf as collection (collection.id)}
						<li class="list-inline-item">
							<a
								class="badge text-bg-secondary text-decoration-none"
								href={resolve('/collections/[id]', { id: collection.id })}
								><Icon name="collections" /> {collection.name}</a
							>
						</li>
					{/each}
				</ul>
			{/if}
			<!-- G1: key/value table (T3). -->
			<div class="card mb-3">
				<table class="table table-sm table-striped table-hover mb-0">
					<caption class="visually-hidden">Game information</caption>
					<tbody>
						<tr>
							<th scope="row" class="fw-normal text-body-secondary">System</th>
							<td class="text-break">
								<div class="d-flex align-items-center gap-2">
									<SystemBadge system={game.system} name={game.systemName} class="text-wrap" />
									{#if auth.isAdmin}
										<!-- Quick edit: opens the same Edit Metadata modal as the Manage menu. -->
										<button
											type="button"
											class="btn btn-sm btn-link text-body-secondary p-0 ms-auto quick-edit"
											aria-label="Quick edit"
											use:tooltip={'Edit Metadata'}
											onclick={() => (action = { kind: 'edit' })}><Icon name="edit" /></button
										>
									{/if}
								</div>
							</td>
						</tr>
						<tr>
							<th scope="row" class="fw-normal text-body-secondary">Year</th>
							<td class="text-break">{game.releaseYear ?? '—'}</td>
						</tr>
						<tr>
							<th scope="row" class="fw-normal text-body-secondary">Developers</th>
							<td class="text-break">{list(game.developers)}</td>
						</tr>
						<tr>
							<th scope="row" class="fw-normal text-body-secondary">Publishers</th>
							<td class="text-break">{list(game.publishers)}</td>
						</tr>
						<tr>
							<th scope="row" class="fw-normal text-body-secondary">Genres</th>
							<td class="text-break">{list(game.genres)}</td>
						</tr>
					</tbody>
				</table>
			</div>
			{#if auth.isAdmin && game.igdbConfigured && game.igdbId === null}
				<!-- IGDB lookup N1: unlinked games only; gone once a save returns the linked detail. -->
				<div class="alert alert-info d-flex flex-wrap align-items-center gap-2 mb-3" role="status">
					This game isn't linked to IGDB.
					<button
						type="button"
						class="btn btn-sm btn-outline-primary ms-auto"
						onclick={() => (action = { kind: 'edit', lookup: true })}
						><Icon name="search" /> Find on IGDB</button
					>
				</div>
			{/if}
			{#if game.description}
				<p style="white-space: pre-line">{game.description}</p>
			{/if}
			<div class="d-flex flex-wrap gap-2">
				{#if external}
					<!-- G5: an external system's ZIP holds no ROMs; they download per release. -->
					<button
						type="button"
						class="btn btn-sm btn-primary"
						use:tooltip={'ROM files are downloaded per release'}
						onclick={() => download(`/api/games/${encodeURIComponent(id)}/download`)}
					>
						<Icon name="download" /> Download ZIP
					</button>
				{:else}
					<button
						type="button"
						class="btn btn-sm btn-primary"
						onclick={() => download(`/api/games/${encodeURIComponent(id)}/download`)}
					>
						<Icon name="download" /> Download ZIP
					</button>
				{/if}
				{#if downloadRoms.length === 1}
					<button
						type="button"
						class="btn btn-sm btn-outline-primary"
						onclick={() => download(downloadRoms[0].downloadUrl, downloadRoms[0])}
					>
						<Icon name="download" /> Download ROM
					</button>
				{:else if downloadRoms.length > 1}
					<div class="dropdown">
						<button
							type="button"
							class="btn btn-sm btn-outline-primary dropdown-toggle"
							data-bs-toggle="dropdown"
							aria-expanded="false"
						>
							<Icon name="download" /> Download ROM
						</button>
						<ul class="dropdown-menu">
							{#each downloadRoms as rom (rom.id)}
								<li>
									<button
										type="button"
										class="dropdown-item"
										onclick={() => download(rom.downloadUrl, rom)}
									>
										{rom.fileName} — {formatSize(rom.sizeBytes)}
									</button>
								</li>
							{/each}
						</ul>
					</div>
				{/if}
				<!-- External link: resolve() is for app routes. -->
				<!-- eslint-disable svelte/no-navigation-without-resolve -->
				<a
					class="btn btn-sm btn-outline-secondary"
					href={googleSearchUrl(gameSearchQuery(game))}
					target="_blank"
					rel="noopener noreferrer"><Icon name="google" /> Google</a
				>
				<!-- eslint-enable svelte/no-navigation-without-resolve -->
				<button
					type="button"
					class="btn btn-sm btn-outline-secondary"
					onclick={() => (collectionsOpen = true)}
				>
					<Icon name="collections" /> Collections
				</button>
				<button
					type="button"
					class="btn btn-sm btn-outline-secondary"
					bind:this={emulatorsButton}
					onclick={() => (emulatorsOpen = true)}><Icon name="emulators" /> Emulators</button
				>
				{#if fromMissingMetadata && auth.isAdmin}
					<button
						type="button"
						class="btn btn-sm btn-outline-primary"
						onclick={() => void hotkeys.run('reports.missingMetadata.next')}
					>
						<Icon name="report-missing-metadata" /> Next missing metadata
					</button>
				{/if}
				<!-- B3: Manage, last in the row: View info.json for everyone, then the Admin actions. -->
				<div class="dropdown">
					<button
						type="button"
						class="btn btn-sm btn-outline-secondary dropdown-toggle"
						data-bs-toggle="dropdown"
						aria-expanded="false"
					>
						<Icon name="admin" /> Manage
					</button>
					<ul class="dropdown-menu dropdown-menu-end">
						<li>
							<button type="button" class="dropdown-item" onclick={() => (infoJsonOpen = true)}>
								<Icon name="code" /> View info.json
							</button>
						</li>
						{#if auth.isAdmin}
							<li><hr class="dropdown-divider" /></li>
							<li>
								<button
									type="button"
									class="dropdown-item"
									onclick={() => (action = { kind: 'edit' })}
								>
									<Icon name="edit" /> Edit Metadata
								</button>
							</li>
							<li>
								<button
									type="button"
									class="dropdown-item"
									onclick={() => (action = { kind: 'retry' })}
								>
									<Icon name="retry" /> Retry media
								</button>
							</li>
							<li>
								<button
									type="button"
									class="dropdown-item"
									onclick={() => (action = { kind: 'merge' })}
								>
									<Icon name="merge" /> Merge into…
								</button>
							</li>
							<li><hr class="dropdown-divider" /></li>
							<li>
								<!-- text-danger-emphasis: plain text-danger fails AA on the dark dropdown. -->
								<button
									type="button"
									class="dropdown-item text-danger-emphasis"
									onclick={() => (action = { kind: 'deleteGame' })}
								>
									<Icon name="delete" /> Delete game
								</button>
							</li>
						{/if}
					</ul>
				</div>
			</div>
		</div>
	</div>

	<ul class="nav nav-tabs mb-3" use:scrollActiveTab role="tablist">
		{#each tabs as [value, label, icon] (value)}
			<li class="nav-item" role="presentation">
				<button
					type="button"
					class="nav-link"
					class:active={tab === value}
					id="{value}-tab"
					role="tab"
					aria-selected={tab === value}
					aria-controls={value === 'updates' || value === 'dlc'
						? 'releases-panel'
						: `${value}-panel`}
					onclick={() => selectTab(value)}><Icon name={icon} /> {label}</button
				>
			</li>
		{/each}
	</ul>

	<!-- Both panels stay mounted (hidden, not removed) so nothing unmounts on a tab switch. -->
	<div id="releases-panel" role="tabpanel" aria-labelledby="{romTab}-tab" hidden={tab !== romTab}>
		<h2 class="h5 visually-hidden">
			{tab === 'updates' ? 'Updates' : tab === 'dlc' ? 'DLC' : 'Releases'}
		</h2>
		{#if tab === 'releases' && !releases.length}
			<p class="text-body-secondary">No releases other than updates and DLC.</p>
		{/if}
		{#if shownRoms.length >= 2}
			<button
				type="button"
				class="btn btn-sm btn-outline-secondary mb-2"
				onclick={toggleAllReleases}>{allReleasesOpen ? 'Collapse all' : 'Expand all'}</button
			>
		{/if}
		<!-- G2: one card per release, so nothing scrolls sideways on a phone. -->
		<div class="d-flex flex-column gap-3 mb-4">
			{#each shownRoms as rom (rom.id)}
				{@const open = openReleases.has(rom.fileName)}
				<!-- R1: a stripe in the system's colour and a tinted header mark where each release starts. -->
				<article
					class="card"
					style="border-left: 6px solid var(--bs-{systemColour(game.system).name})"
					aria-labelledby="release-{rom.id}-name"
				>
					<!-- The chevron button is the keyboard path; a click on empty header space is a mouse shortcut. -->
					<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
					<div
						class="card-header bg-body-tertiary release-header"
						onclick={(e) => onReleaseHeaderClick(e, rom.fileName)}
					>
						<div class="d-flex flex-wrap align-items-center gap-2">
							<!-- B6: chevron and name on the left; min-width 0 lets a long name wrap. -->
							<div class="d-flex align-items-center gap-2" style="min-width: 0">
								<button
									type="button"
									class="btn btn-sm btn-link p-0 text-body chevron-toggle"
									aria-expanded={open}
									aria-controls="release-{rom.id}-details"
									aria-label="{open ? 'Hide' : 'Show'} details for {rom.fileName}"
									onclick={() => toggleRelease(rom.fileName)}><Icon name="chevron" /></button
								>
								<h3 class="h6 mb-0 text-break" id="release-{rom.id}-name">{rom.fileName}</h3>
							</div>
							<!-- B6: badges, then buttons, on the right. -->
							<div class="ms-auto d-flex flex-wrap gap-2 align-items-center justify-content-end">
								{#if rom.isPrimary}<span class="badge text-bg-primary">Primary</span>{/if}
								{#if rom.version}<span class="badge text-bg-info">Version {rom.version}</span>{/if}
								{#if discNumber(rom) !== null}<span class="badge text-bg-info"
										>Disc {discNumber(rom)}</span
									>{/if}
								{#if rom.externalFileName}
									<!-- G7: stored next to the ZIP under this name. -->
									<!-- svelte-ignore a11y_no_noninteractive_tabindex -->
									<span class="badge text-bg-dark" tabindex="0" use:tooltip={rom.externalFileName}
										>External</span
									>
								{/if}
								{#each rom.regions as region (region)}
									<span class="badge text-bg-secondary">{region}</span>
								{/each}
								<!-- R4: a filled button when the release has a note; Site users only view notes (U3). -->
								{#if auth.isAdmin || rom.hasNote}<button
										type="button"
										class="btn btn-sm {rom.hasNote ? 'btn-warning' : 'btn-outline-secondary'}"
										aria-label="{rom.hasNote ? 'Note for' : 'Add note for'} {rom.fileName}"
										use:tooltip={rom.hasNote ? 'View note' : 'Add note'}
										onclick={() => showNote(rom)}
									>
										<Icon name={rom.hasNote ? 'note-filled' : 'note'} />
									</button>{/if}
								{#if canPlay(game, rom) && game.browserCore && opensInNewTab(game.browserCore)}
									<!-- PSP B3: the isolated player runs in its own tab; a static page outside the SPA router. -->
									<!-- eslint-disable svelte/no-navigation-without-resolve -->
									<a
										class="btn btn-sm btn-outline-success"
										href={playUrl(
											game.id,
											rom,
											game.browserCore,
											usesPlayDisc(game.browserCore, rom)
										)}
										target="_blank"
										rel="noopener"
										aria-label="Play {rom.fileName} (opens in a new tab)"
										{...playLinkEvents(game.id, rom.id)}
										><!-- eslint-enable svelte/no-navigation-without-resolve -->
										<Icon name="play" />
									</a>
								{:else if canPlay(game, rom)}
									<button
										type="button"
										class="btn btn-sm btn-outline-success"
										aria-label="Play {rom.fileName}"
										onclick={() => {
											playRom = rom;
											playSet = null;
											playOpen = true;
										}}
									>
										<Icon name="play" />
									</button>
									{#if rom.discSet}
										{@const discSet = rom.discSet}
										{@const media = setMedia(game.browserCore)}
										<!-- Multi-disc M7 (Amiga F8: disks): every disc of the set in one session, from Disc 1's card. -->
										<button
											type="button"
											class="btn btn-sm btn-outline-success"
											aria-label="Play all {media} of {discSet.name}"
											use:tooltip={`Play all ${media} in one session`}
											onclick={() => {
												playRom = rom;
												playSet = discSet.name;
												playOpen = true;
											}}
										>
											<Icon name="play" /> All {media}
										</button>
									{/if}
								{/if}
								<button
									type="button"
									class="btn btn-sm btn-outline-primary"
									aria-label="Download {rom.fileName}"
									onclick={() => download(rom.downloadUrl, rom)}
								>
									<Icon name="download" />
								</button>
								{@render bundleButton(bundleEntry(game, rom))}
							</div>
						</div>
					</div>
					<div class="card-body small" id="release-{rom.id}-details" hidden={!open}>
						{#if rom.hasNote}
							{@const note = notes.get(rom.fileName)}
							<!-- B7: the note comes first; a non-interactive block (its Markdown links work) beside an Edit/View button. -->
							<div class="border rounded p-2 mb-3" data-testid="release-note">
								<div class="d-flex align-items-center gap-2 mb-2">
									<h4 class="h6 mb-0">Release note</h4>
									<button
										type="button"
										class="btn btn-sm btn-outline-secondary ms-auto"
										aria-label="{auth.isAdmin ? 'Edit' : 'View'} note for {rom.fileName}"
										onclick={() => showNote(rom, auth.isAdmin ? 'edit' : undefined)}
									>
										<Icon name={auth.isAdmin ? 'edit' : 'note-filled'} />
										{auth.isAdmin ? 'Edit note' : 'View note'}
									</button>
								</div>
								{#if note && 'error' in note}
									<ErrorAlert error={note.error} onretry={() => loadNote(rom)} />
								{:else if note && 'text' in note}
									<div class="note-markdown">
										<!-- eslint-disable-next-line svelte/no-at-html-tags -- sanitised by DOMPurify in renderMarkdown -->
										{@html renderMarkdown(note.text)}
									</div>
								{:else}
									<Loading label="Loading note…" />
								{/if}
							</div>
						{/if}
						<!-- B8: a key/value table (T1–T4), one pair per row, each row only when it has a value. Values
						     keep wrapping (G2: never truncated), an exception to the T-rule's text-truncate. -->
						<div class="card">
							<div class="table-responsive">
								<table class="table table-sm table-striped table-hover mb-0">
									<tbody>
										{#if rom.originalFileName !== rom.fileName}
											<tr>
												<th scope="row" class="fw-normal text-body-secondary">Original file name</th
												>
												<td class="text-break">
													<span class="text-break">{rom.originalFileName}</span>
													<CopyButton value={rom.originalFileName} label="original file name" />
												</td>
											</tr>
										{/if}
										<tr>
											<th scope="row" class="fw-normal text-body-secondary">Size</th>
											<td class="text-break"><FileSize bytes={rom.totalSizeBytes} /></td>
										</tr>
										{#each [['SHA1', rom.sha1], ['CRC32', rom.crc32], ['MD5', rom.md5]] as [name, hash] (name)}
											<tr>
												<th scope="row" class="fw-normal text-body-secondary">{name}</th>
												<td class="text-break">
													<span class="font-monospace text-break">{hash}</span>
													<CopyButton value={hash} label={name} />
												</td>
											</tr>
										{/each}
										{#if rom.revision}
											<tr>
												<th scope="row" class="fw-normal text-body-secondary">Revision</th>
												<td class="text-break">{rom.revision}</td>
											</tr>
										{/if}
										{#if rom.languages.length}
											<tr>
												<th scope="row" class="fw-normal text-body-secondary">Languages</th>
												<td class="text-break">{list(rom.languages)}</td>
											</tr>
										{/if}
										{#if rom.flags.length}
											<tr>
												<th scope="row" class="fw-normal text-body-secondary">Flags</th>
												<td class="text-break">{list(rom.flags)}</td>
											</tr>
										{/if}
										<tr>
											<th scope="row" class="fw-normal text-body-secondary">Identified by</th>
											<td class="text-break">{identifiedBy[rom.identificationMethod]}</td>
										</tr>
										{#if rom.datName}
											<tr>
												<th scope="row" class="fw-normal text-body-secondary">DAT</th>
												<td class="text-break">
													{[rom.datName, rom.datVersion].filter(Boolean).join(' ')}
												</td>
											</tr>
										{/if}
										{#if rom.datGameName}
											<tr>
												<th scope="row" class="fw-normal text-body-secondary">DAT entry</th>
												<td class="text-break">{rom.datGameName}</td>
											</tr>
										{/if}
										<tr>
											<th scope="row" class="fw-normal text-body-secondary">Date added</th>
											<td class="text-break">{formatDateTime(rom.dateAdded)}</td>
										</tr>
									</tbody>
								</table>
							</div>
						</div>
						{#if rom.header || auth.isAdmin}
							<div class="d-flex flex-wrap gap-2 mt-2">
								{#if rom.header}
									<button
										type="button"
										class="btn btn-sm btn-outline-secondary"
										onclick={() => showHeader(rom)}
									>
										<Icon name="info" /> Header details
									</button>
								{/if}
								{#if auth.isAdmin}
									<!-- B9: Header details stays left; Move and Delete release sit right. -->
									<div class="ms-auto d-flex flex-wrap gap-2">
										<button
											type="button"
											class="btn btn-sm btn-outline-secondary"
											aria-label="Edit release {rom.fileName}"
											onclick={() => {
												editRom = rom;
												editOpen = true;
											}}
										>
											<Icon name="edit" /> Edit release
										</button>
										{#if !rom.isPrimary}
											<button
												type="button"
												class="btn btn-sm btn-outline-secondary"
												aria-label="Make primary {rom.fileName}"
												onclick={() => (action = { kind: 'primary', rom })}
											>
												<Icon name="star" /> Make primary
											</button>
										{/if}
										<button
											type="button"
											class="btn btn-sm btn-outline-secondary"
											aria-label="Move release {rom.fileName}"
											onclick={() => (action = { kind: 'move', rom })}
										>
											<Icon name="move" /> Move release
										</button>
										<button
											type="button"
											class="btn btn-sm btn-outline-danger"
											aria-label="Delete release {rom.fileName}"
											onclick={() => (action = { kind: 'deleteRom', rom })}
										>
											<Icon name="delete" /> Delete release
										</button>
									</div>
								{/if}
							</div>
						{/if}
						{#if rom.files?.length}
							{@const filesOpen = openFiles.has(rom.fileName)}
							<!-- B20: the main file and the set's other files (cue, tracks); N counts all of them. -->
							<button
								type="button"
								class="btn btn-sm btn-outline-secondary mt-2"
								aria-expanded={filesOpen}
								aria-controls="release-{rom.id}-files"
								onclick={() =>
									filesOpen ? openFiles.delete(rom.fileName) : openFiles.add(rom.fileName)}
								><Icon name="chevron" /> {rom.files.length + 1} files</button
							>
							<div class="card mt-2" id="release-{rom.id}-files" hidden={!filesOpen}>
								<div class="table-responsive">
									<table class="table table-sm table-striped table-hover align-middle mb-0">
										<caption class="visually-hidden">Files of {rom.fileName}</caption>
										<thead class="table-dark">
											<tr>
												<th scope="col">File</th>
												<th scope="col" class="text-end">Size</th>
												<th scope="col">SHA1</th>
												<th scope="col"><span class="visually-hidden">Actions</span></th>
											</tr>
										</thead>
										<tbody>
											{#each [{ fileName: rom.fileName, sizeBytes: rom.sizeBytes, sha1: rom.sha1, downloadUrl: rom.downloadUrl }, ...rom.files] as file, index (file.fileName)}
												<tr>
													<td class="text-truncate" style="max-width: 32rem" title={file.fileName}
														>{file.fileName}</td
													>
													<td class="text-end text-nowrap"><FileSize bytes={file.sizeBytes} /></td>
													<td class="text-nowrap">
														<span class="font-monospace">{file.sha1}</span>
														<CopyButton value={file.sha1} label="SHA1 of {file.fileName}" />
													</td>
													<td class="text-end">
														<button
															type="button"
															class="btn btn-sm btn-outline-primary"
															aria-label="Download {file.fileName}"
															onclick={() => download(file.downloadUrl, rom)}
														>
															<Icon name="download" />
														</button>
														<!-- Row 0 is the main file; the rest are separately downloadable set files (B1). -->
														{@render bundleButton(
															bundleEntry(game, rom, index === 0 ? undefined : rom.files[index - 1])
														)}
													</td>
												</tr>
											{/each}
										</tbody>
									</table>
								</div>
							</div>
						{/if}
					</div>
				</article>
			{/each}
		</div>
	</div>

	<!-- Outside both panels (a hidden ancestor would hide it) and before the Images panel: game-admin.test.ts takes
	     the first .modal. -->
	{#if auth.isAdmin}
		<GameAdminModal {game} bind:action ondone={onAdminDone} />
	{/if}

	<div id="images-panel" role="tabpanel" aria-labelledby="images-tab" hidden={tab !== 'images'}>
		<GameImages {game} onchanged={setGame} />
	</div>

	<div id="saves-panel" role="tabpanel" aria-labelledby="saves-tab" hidden={tab !== 'saves'}>
		{#if tab === 'saves'}<GameSaves {game} version={savesVersion} onchanged={refreshGame} />{/if}
	</div>

	<div id="zip-panel" role="tabpanel" aria-labelledby="zip-tab" hidden={tab !== 'zip'}>
		<h2 class="h5 visually-hidden">Zip Contents</h2>
		<ZipBrowser {game} active={tab === 'zip'} bind:selected={zipSelected} expanded={zipExpanded} />
	</div>

	<ConfirmModal bind:open={headerOpen} title="Header — {headerRom?.fileName ?? ''}" size="lg">
		{#if headerRom?.header}
			<table class="table table-sm table-striped table-hover mb-0">
				<caption class="visually-hidden">Header of {headerRom.fileName}</caption>
				<tbody>
					<!-- R3: two pairs per row; an odd last pair leaves the second half empty. -->
					{#each headerRows(headerRom.header) as pairs (pairs[0][0])}
						<tr>
							{#each pairs as [key, value] (key)}
								<th scope="row" class="fw-normal text-body-secondary">{formatKey(key)}</th>
								<td>{formatValue(value)}</td>
							{/each}
							{#if pairs.length === 1}<td colspan="2"></td>{/if}
						</tr>
					{/each}
				</tbody>
			</table>
		{/if}
	</ConfirmModal>

	<InfoJsonModal bind:open={infoJsonOpen} gameId={game.id} />

	<ReleaseNoteModal
		bind:open={noteOpen}
		gameId={game.id}
		rom={noteRom}
		initialMode={noteMode}
		onsaved={onNoteSaved}
	/>
	<EditReleaseModal
		bind:open={editOpen}
		gameId={game.id}
		rom={editRom}
		fileSets={game.fileSets}
		onsaved={(_, kind) => void onReleaseEdited(kind)}
	/>
	<EmulatorModal
		bind:open={playOpen}
		gameId={game.id}
		core={game.browserCore}
		rom={playRom}
		disc={playRom != null && game.browserCore != null && usesPlayDisc(game.browserCore, playRom)}
		discSet={playSet}
		onsaved={() => {
			savesVersion++;
			void refreshGame();
		}}
		onimagesaved={() => void refreshGame()}
	/>
	<GameCollectionsModal
		bind:open={collectionsOpen}
		gameId={game.id}
		title={game.displayTitle}
		bind:memberOf
	/>
	<SystemEmulatorsModal
		bind:open={emulatorsOpen}
		system={{ id: game.system, displayName: game.systemName }}
	/>
{/if}

<!-- Bundle Creator B1: adds one file to the working cart; shows a check once it's there. -->
{#snippet bundleButton(entry: BundleEntry)}
	{@const inCart = bundle.has(entry)}
	<button
		type="button"
		class="btn btn-sm {inCart ? 'btn-primary' : 'btn-outline-primary'}"
		aria-label={inCart
			? `${entry.fileName} is in the bundle`
			: `Add ${entry.fileName} to the bundle`}
		title={inCart ? 'In the bundle' : 'Add to bundle'}
		onclick={() => addToBundle(entry)}
	>
		<Icon name={inCart ? 'cart-check' : 'cart-add'} />
	</button>
{/snippet}
