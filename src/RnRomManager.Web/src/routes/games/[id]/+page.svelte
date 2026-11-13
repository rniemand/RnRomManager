<script lang="ts">
	import { untrack } from 'svelte';
	import { SvelteSet } from 'svelte/reactivity';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { CollectionRef, GameDetail, RomDetail } from '#lib/api/types.js';
	import { collectionsApi } from '#lib/collections.js';
	import ConfirmModal from '#lib/components/ConfirmModal.svelte';
	import CopyButton from '#lib/components/CopyButton.svelte';
	import GameAdminModal, {
		type AdminAction,
		type AdminOutcome
	} from '#lib/components/GameAdminModal.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import GameImages from '#lib/components/GameImages.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import FileSize from '#lib/components/FileSize.svelte';
	import GameCollectionsModal from '#lib/components/GameCollectionsModal.svelte';
	import Icon from '#lib/components/Icon.svelte';
	import InfoJsonModal from '#lib/components/InfoJsonModal.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import RatingControl from '#lib/components/RatingControl.svelte';
	import ReleaseNoteModal from '#lib/components/ReleaseNoteModal.svelte';
	import EmulatorModal from '#lib/components/EmulatorModal.svelte';
	import { canPlay } from '#lib/emulator.js';
	import SystemBadge from '#lib/components/SystemBadge.svelte';
	import ZipBrowser from '#lib/components/ZipBrowser.svelte';
	import { formatDateTime, formatKey, formatSize, formatValue } from '#lib/format.js';
	import { gameSearchQuery, googleSearchUrl } from '#lib/googleSearch.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import { compareUpdates, discNumber, isUpdate, orderByDisc } from '#lib/releases.js';
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
	let noteOpen = $state(false);
	let playRom = $state<RomDetail | null>(null);
	let playOpen = $state(false);
	/** B16/B25: the caller's collections holding this game. */
	let memberOf = $state<CollectionRef[]>([]);
	let collectionsOpen = $state(false);

	const id = $derived(page.params.id ?? '');
	// P3: the cover (any type); screenshots are in the Images gallery.
	const cover = $derived(game?.images.find((i) => i.isCover));
	// P1: primary first, then the Releases order.
	// G5: a release pointer means the game's ROMs are stored next to its ZIP.
	const external = $derived(game?.roms.some((r) => r.externalFileName !== null) ?? false);
	// B9/B11: updates have their own tab, newest first; the rest are the releases.
	const releases = $derived(orderByDisc(game?.roms.filter((r) => !isUpdate(r)) ?? []));
	const updates = $derived(game ? game.roms.filter(isUpdate).sort(compareUpdates) : []);
	// B10: the header download lists releases only; a game with only updates offers those.
	const downloadable = $derived(releases.length ? releases : updates);
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
			game = fresh;
			notice = changedMessage;
		} else {
			window.location.assign(url);
		}
	}

	function showHeader(rom: RomDetail): void {
		headerRom = rom;
		headerOpen = true;
	}

	function showNote(rom: RomDetail): void {
		noteRom = rom;
		noteOpen = true;
	}

	/**
	 * A note write rewrites the ZIP, and the mirror gives every ROM a new id (§11): flip the icon now, then quietly
	 * refetch so the next note/download/edit uses current ids.
	 */
	async function onNoteSaved(romId: number, hasNote: boolean): Promise<void> {
		const rom = game?.roms.find((r) => r.id === romId);
		if (rom) rom.hasNote = hasNote;
		try {
			const fresh = await fetchGame(id);
			if (fresh) game = fresh;
		} catch {
			// The flipped icon stands; a stale id later reloads and asks for a retry as usual.
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
	const identifiedBy = { Dat: 'DAT', Manual: 'Manual', TitleDb: 'Title DB' } as const;

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
	type Tab = 'releases' | 'updates' | 'images' | 'zip';
	// B9: ?tab=updates on a game without updates shows Releases, as an unknown tab does.
	const tab = $derived<Tab>(
		(['images', 'zip', ...(updates.length ? ['updates' as const] : [])] as const).find(
			(t) => t === page.url.searchParams.get('tab')
		) ?? 'releases'
	);
	// B10: one panel lists either tab's cards.
	const shownRoms = $derived(tab === 'updates' ? updates : releases);
	const tabs = $derived<[Tab, string][]>([
		['releases', `Releases (${releases.length})`],
		...(updates.length ? [['updates', `Updates (${updates.length})`] as [Tab, string]] : []),
		['images', `Images (${game?.images.length ?? 0})`],
		['zip', 'Zip Contents']
	]);

	function selectTab(next: Tab): void {
		const path = resolve('/games/[id]', { id });
		// eslint-disable-next-line svelte/no-navigation-without-resolve -- the resolved path plus a query
		void goto(next === 'releases' ? path : `${path}?tab=${next}`, { replace: true, reset: false });
	}
</script>

<svelte:head><title>{game?.displayTitle ?? 'Game'} · RnRomManager</title></svelte:head>

<p><a href={resolve('/')}>← Library</a></p>

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
				<h1 class="h3 mb-0">{game.displayTitle}</h1>
				<RatingControl
					gameId={game.id}
					title={game.displayTitle}
					bind:rating={game.rating}
					bind:favourite={game.favourite}
					onsaved={(detail) => (game = detail)}
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
							<td class="text-break"
								><SystemBadge system={game.system} name={game.systemName} class="text-wrap" /></td
							>
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
			{#if game.description}
				<p style="white-space: pre-line">{game.description}</p>
			{/if}
			<div class="d-flex flex-wrap gap-2">
				{#if external}
					<!-- G5: an external system's ZIP holds no ROMs; they download per release. -->
					<button
						type="button"
						class="btn btn-primary"
						use:tooltip={'ROM files are downloaded per release'}
						onclick={() => download(`/api/games/${encodeURIComponent(id)}/download`)}
					>
						<Icon name="download" /> Download ZIP
					</button>
				{:else}
					<button
						type="button"
						class="btn btn-primary"
						onclick={() => download(`/api/games/${encodeURIComponent(id)}/download`)}
					>
						<Icon name="download" /> Download ZIP
					</button>
				{/if}
				{#if downloadRoms.length === 1}
					<button
						type="button"
						class="btn btn-outline-primary"
						onclick={() => download(downloadRoms[0].downloadUrl, downloadRoms[0])}
					>
						<Icon name="download" /> Download ROM
					</button>
				{:else if downloadRoms.length > 1}
					<div class="dropdown">
						<button
							type="button"
							class="btn btn-outline-primary dropdown-toggle"
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
					class="btn btn-outline-secondary"
					href={googleSearchUrl(gameSearchQuery(game))}
					target="_blank"
					rel="noopener noreferrer"><Icon name="google" /> Google</a
				>
				<!-- eslint-enable svelte/no-navigation-without-resolve -->
				<button
					type="button"
					class="btn btn-outline-secondary"
					onclick={() => (infoJsonOpen = true)}
				>
					<Icon name="code" /> View info.json
				</button>
				<button
					type="button"
					class="btn btn-outline-secondary"
					onclick={() => (collectionsOpen = true)}
				>
					<Icon name="collections" /> Collections
				</button>
			</div>
			{#if auth.canEditLibrary}
				<div class="d-flex flex-wrap gap-2 mt-3" role="group" aria-label="Admin actions">
					<button
						type="button"
						class="btn btn-outline-secondary"
						onclick={() => (action = { kind: 'edit' })}
					>
						<Icon name="edit" /> Edit metadata
					</button>
					<button
						type="button"
						class="btn btn-outline-secondary"
						onclick={() => (action = { kind: 'retry' })}
					>
						<Icon name="retry" /> Retry media
					</button>
					<button
						type="button"
						class="btn btn-outline-secondary"
						onclick={() => (action = { kind: 'merge' })}
					>
						<Icon name="merge" /> Merge into…
					</button>
					<button
						type="button"
						class="btn btn-outline-danger"
						onclick={() => (action = { kind: 'deleteGame' })}
					>
						<Icon name="delete" /> Delete game
					</button>
				</div>
			{/if}
		</div>
	</div>

	<ul class="nav nav-tabs mb-3" role="tablist">
		{#each tabs as [value, label] (value)}
			<li class="nav-item" role="presentation">
				<button
					type="button"
					class="nav-link"
					class:active={tab === value}
					id="{value}-tab"
					role="tab"
					aria-selected={tab === value}
					aria-controls={value === 'updates' ? 'releases-panel' : `${value}-panel`}
					onclick={() => selectTab(value)}>{label}</button
				>
			</li>
		{/each}
	</ul>

	<!-- Both panels stay mounted (hidden, not removed) so nothing unmounts on a tab switch. -->
	<div
		id="releases-panel"
		role="tabpanel"
		aria-labelledby="{tab === 'updates' ? 'updates' : 'releases'}-tab"
		hidden={tab !== 'releases' && tab !== 'updates'}
	>
		<h2 class="h5 visually-hidden">{tab === 'updates' ? 'Updates' : 'Releases'}</h2>
		{#if tab === 'releases' && !releases.length}
			<p class="text-body-secondary">No releases other than updates.</p>
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
							<button
								type="button"
								class="btn btn-sm btn-link p-0 text-body chevron-toggle"
								aria-expanded={open}
								aria-controls="release-{rom.id}-details"
								aria-label="{open ? 'Hide' : 'Show'} details for {rom.fileName}"
								onclick={() => toggleRelease(rom.fileName)}><Icon name="chevron" /></button
							>
							<h3 class="h6 mb-0 text-break" id="release-{rom.id}-name">{rom.fileName}</h3>
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
							<div class="ms-auto d-flex flex-wrap gap-1">
								<!-- R4: a filled button when the release has a note. -->
								<button
									type="button"
									class="btn btn-sm {rom.hasNote ? 'btn-warning' : 'btn-outline-secondary'}"
									aria-label="{rom.hasNote ? 'Note for' : 'Add note for'} {rom.fileName}"
									use:tooltip={rom.hasNote ? 'View note' : 'Add note'}
									onclick={() => showNote(rom)}
								>
									<Icon name={rom.hasNote ? 'note-filled' : 'note'} />
								</button>
								{#if canPlay(game, rom)}
									<button
										type="button"
										class="btn btn-sm btn-outline-success"
										aria-label="Play {rom.fileName}"
										onclick={() => {
											playRom = rom;
											playOpen = true;
										}}
									>
										<Icon name="play" />
									</button>
								{/if}
								<button
									type="button"
									class="btn btn-sm btn-outline-primary"
									aria-label="Download {rom.fileName}"
									onclick={() => download(rom.downloadUrl, rom)}
								>
									<Icon name="download" />
								</button>
								{#if auth.canEditLibrary}
									<button
										type="button"
										class="btn btn-sm btn-outline-danger"
										aria-label="Delete {rom.fileName}"
										onclick={() => (action = { kind: 'deleteRom', rom })}
									>
										<Icon name="delete" />
									</button>
								{/if}
							</div>
						</div>
					</div>
					<div class="card-body small" id="release-{rom.id}-details" hidden={!open}>
						<!-- R2: label/value pairs, two per row from lg; each row only when it has a value. -->
						<dl class="row mb-0">
							{#if rom.originalFileName !== rom.fileName}
								<dt class="col-sm-3 col-lg-2 text-body-secondary">Original file name</dt>
								<dd class="col-sm-9 col-lg-4">
									<span class="text-break">{rom.originalFileName}</span>
									<CopyButton value={rom.originalFileName} label="original file name" />
								</dd>
							{/if}
							<dt class="col-sm-3 col-lg-2 text-body-secondary">Size</dt>
							<dd class="col-sm-9 col-lg-4"><FileSize bytes={rom.totalSizeBytes} /></dd>
							{#each [['SHA1', rom.sha1], ['CRC32', rom.crc32], ['MD5', rom.md5]] as [name, hash] (name)}
								<dt class="col-sm-3 col-lg-2 text-body-secondary">{name}</dt>
								<dd class="col-sm-9 col-lg-4">
									<span class="font-monospace text-break">{hash}</span>
									<CopyButton value={hash} label={name} />
								</dd>
							{/each}
							{#if rom.revision}
								<dt class="col-sm-3 col-lg-2 text-body-secondary">Revision</dt>
								<dd class="col-sm-9 col-lg-4">{rom.revision}</dd>
							{/if}
							{#if rom.languages.length}
								<dt class="col-sm-3 col-lg-2 text-body-secondary">Languages</dt>
								<dd class="col-sm-9 col-lg-4">{list(rom.languages)}</dd>
							{/if}
							{#if rom.flags.length}
								<dt class="col-sm-3 col-lg-2 text-body-secondary">Flags</dt>
								<dd class="col-sm-9 col-lg-4">{list(rom.flags)}</dd>
							{/if}
							<dt class="col-sm-3 col-lg-2 text-body-secondary">Identified by</dt>
							<dd class="col-sm-9 col-lg-4">{identifiedBy[rom.identificationMethod]}</dd>
							{#if rom.datName}
								<dt class="col-sm-3 col-lg-2 text-body-secondary">DAT</dt>
								<dd class="col-sm-9 col-lg-4 text-break">
									{[rom.datName, rom.datVersion].filter(Boolean).join(' ')}
								</dd>
							{/if}
							{#if rom.datGameName}
								<dt class="col-sm-3 col-lg-2 text-body-secondary">DAT entry</dt>
								<dd class="col-sm-9 col-lg-4 text-break">{rom.datGameName}</dd>
							{/if}
							<dt class="col-sm-3 col-lg-2 text-body-secondary">Date added</dt>
							<dd class="col-sm-9 col-lg-4">{formatDateTime(rom.dateAdded)}</dd>
						</dl>
						{#if rom.header || auth.canEditLibrary}
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
								{#if auth.canEditLibrary}
									<button
										type="button"
										class="btn btn-sm btn-outline-secondary"
										aria-label="Move {rom.fileName}"
										onclick={() => (action = { kind: 'move', rom })}
									>
										<Icon name="move" /> Move release
									</button>
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
											{#each [{ fileName: rom.fileName, sizeBytes: rom.sizeBytes, sha1: rom.sha1, downloadUrl: rom.downloadUrl }, ...rom.files] as file (file.fileName)}
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
	{#if auth.canEditLibrary}
		<GameAdminModal {game} bind:action ondone={onAdminDone} />
	{/if}

	<div id="images-panel" role="tabpanel" aria-labelledby="images-tab" hidden={tab !== 'images'}>
		<GameImages {game} onchanged={(detail) => (game = detail)} />
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

	<ReleaseNoteModal bind:open={noteOpen} gameId={game.id} rom={noteRom} onsaved={onNoteSaved} />
	<EmulatorModal bind:open={playOpen} gameId={game.id} core={game.browserCore} rom={playRom} />
	<GameCollectionsModal
		bind:open={collectionsOpen}
		gameId={game.id}
		title={game.displayTitle}
		bind:memberOf
	/>
{/if}
