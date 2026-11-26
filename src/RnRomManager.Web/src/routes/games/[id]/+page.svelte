<script lang="ts">
	import Button from '#lib/components/forms/Button.svelte';
	import RomHeaderModal from '#lib/components/modals/RomHeaderModal.svelte';
	import { replaceUrl } from '#lib/urlState.js';
	import NavTabs from '#lib/components/layout/NavTabs.svelte';
	import { untrack } from 'svelte';
	import { SvelteMap, SvelteSet } from 'svelte/reactivity';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type {
		CollectionRef,
		GameDetail,
		ReleaseNoteState,
		RomDetail,
		AdminAction,
		AdminOutcome
	} from '#lib/types/index.js';
	import { collectionsApi } from '#lib/collections.js';
	import GameAdminModal from '#lib/components/modals/GameAdminModal.svelte';
	import EmptyState from '#lib/components/common/EmptyState.svelte';
	import GameHeader from '#lib/components/games/GameHeader.svelte';
	import GameLinks from '#lib/components/games/GameLinks.svelte';
	import GameImages from '#lib/components/games/GameImages.svelte';
	import GameSaves from '#lib/components/games/GameSaves.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import GameCollectionsModal from '#lib/components/modals/GameCollectionsModal.svelte';
	import SystemEmulatorsModal from '#lib/components/modals/SystemEmulatorsModal.svelte';
	import InfoJsonModal from '#lib/components/modals/InfoJsonModal.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import ReleaseNoteModal from '#lib/components/modals/ReleaseNoteModal.svelte';
	import EditReleaseModal from '#lib/components/modals/EditReleaseModal.svelte';
	import ReleaseTable from '#lib/components/games/ReleaseTable.svelte';
	import type { ReleaseKind } from '#lib/enums/index.js';
	import EmulatorModal from '#lib/components/modals/EmulatorModal.svelte';
	import { canPlay, opensInNewTab, usesPlayDisc } from '#lib/emulator.js';
	import ZipBrowser from '#lib/components/games/ZipBrowser.svelte';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import { compareDlc, compareUpdates, isDlc, isUpdate, orderByDisc } from '#lib/releases.js';

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

	/** P5: Show more on the description; kept across reloads, collapsed again for another game. */
	let descriptionExpanded = $state(false);
	// P1: primary first, then the Releases order.
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
			void replaceUrl(`${next.pathname}${next.search}`);
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
	const notes = new SvelteMap<string, ReleaseNoteState>();
	const pendingNotes = new SvelteSet<string>();

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
			toasts.show(outcome.message, outcome.warning ? 'warning' : 'success');
		} else if ('goTo' in outcome) {
			toasts.show(outcome.message, 'success');
			await goto(outcome.goTo ? resolve('/games/[id]', { id: outcome.goTo }) : resolve('/'));
		} else {
			if (outcome.warning) notice = outcome.message ?? null;
			else if (outcome.message) toasts.show(outcome.message, 'success');
			reload++;
		}
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
			descriptionExpanded = false;
			openReleases.clear();
			openFiles.clear();
			notes.clear();
			zipSelected = '';
			zipExpanded.clear();
		});
	});
	// B20: which releases' file lists are open, by file name like openReleases.
	const openFiles = new SvelteSet<string>();
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
	/** Opened by Next missing IGDB ID or from the Missing IGDB IDs report; kept across tab switches. */
	const fromMissingIgdb = $derived(page.url.searchParams.get('from') === 'missing-igdb');

	/** The game whose Lookup Info was opened on arrival from missing-igdb: once per game, so closing it sticks. */
	let lookupOpenedFor: string | null = null;
	$effect(() => {
		const current = game;
		if (!fromMissingIgdb || !auth.isAdmin || !current || current.id !== id) return;
		untrack(() => {
			if (lookupOpenedFor === current.id) return;
			lookupOpenedFor = current.id;
			// As Find on IGDB: Edit Metadata on Lookup Info, searching at once.
			if (current.igdbConfigured && current.igdbId === null && action === null)
				action = { kind: 'edit', lookup: true };
		});
	});

	function selectTab(next: Tab): void {
		const path = resolve('/games/[id]', { id });
		const query = [
			fromMissingMetadata && 'from=missing-metadata',
			fromMissingIgdb && 'from=missing-igdb',
			next !== 'releases' && `tab=${next}`
		]
			.filter(Boolean)
			.join('&');
		void replaceUrl(query ? `${path}?${query}` : path);
	}
</script>

<svelte:head><title>{game?.displayTitle ?? 'Game'} · RnRomManager</title></svelte:head>

<!-- P1: the top row; the game's own links follow Back to Library once it has loaded. -->
<div class="d-flex flex-wrap gap-2 mb-3">
	<Button variant="primary" outline size="sm" icon="back" href={resolve('/')}
		>Back to Library</Button
	>
	{#if game}
		<GameLinks {game} bind:emulatorsButton onemulators={() => (emulatorsOpen = true)} />
	{/if}
</div>

{#if error}
	<ErrorAlert {error} onretry={() => reload++} />
{:else if notFound}
	<EmptyState icon="not-found" message="This game doesn't exist any more.">
		{#snippet action()}
			<Button variant="primary" href={resolve('/')}>Go to the Library</Button>
		{/snippet}
	</EmptyState>
{:else if !game}
	<Loading label="Loading game…" />
{:else}
	{#if notice}
		<div class="alert alert-warning alert-dismissible" role="alert">
			{notice}
			<Button class="btn-close" aria-label="Dismiss" onclick={() => (notice = null)} />
		</div>
	{/if}

	<GameHeader
		bind:game
		gameId={id}
		{memberOf}
		{downloadRoms}
		{fromMissingMetadata}
		{fromMissingIgdb}
		bind:descriptionExpanded
		ondownload={download}
		onsaved={setGame}
		onstale={() => onAdminDone({ reload: true, message: changedMessage, warning: true })}
		onaction={(a) => (action = a)}
		oninfojson={() => (infoJsonOpen = true)}
		oncollections={() => (collectionsOpen = true)}
	/>

	<NavTabs
		tabs={tabs.map(([value, label, icon]) => ({
			id: value,
			label,
			icon,
			tabId: `${value}-tab`,
			controls: value === 'updates' || value === 'dlc' ? 'releases-panel' : `${value}-panel`
		}))}
		active={tab}
		onselect={selectTab}
		class="mb-3"
	/>

	<!-- Both panels stay mounted (hidden, not removed) so nothing unmounts on a tab switch. -->
	<div id="releases-panel" role="tabpanel" aria-labelledby="{romTab}-tab" hidden={tab !== romTab}>
		<ReleaseTable
			{game}
			gameId={id}
			{tab}
			roms={shownRoms}
			releaseCount={releases.length}
			{openReleases}
			{openFiles}
			{notes}
			{pendingNotes}
			ondownload={download}
			onnote={showNote}
			onheader={showHeader}
			onplay={(rom, discSet) => {
				playRom = rom;
				playSet = discSet;
				playOpen = true;
			}}
			onedit={(rom) => {
				editRom = rom;
				editOpen = true;
			}}
			onaction={(a) => (action = a)}
			onrefresh={refreshGame}
		/>
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

	<RomHeaderModal bind:open={headerOpen} rom={headerRom} />

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
