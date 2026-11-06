<script lang="ts">
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { GameDetail, RomDetail } from '#lib/api/types.js';
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
	import Icon from '#lib/components/Icon.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import RatingControl from '#lib/components/RatingControl.svelte';
	import { formatKey, formatSize, formatValue } from '#lib/format.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';

	const changedMessage = 'This game changed, please retry.';

	let game = $state<GameDetail | null>(null);
	let error = $state<unknown>(null);
	let notFound = $state(false);
	let notice = $state<string | null>(null);
	let headerRom = $state<RomDetail | null>(null);
	let headerOpen = $state(false);
	let reload = $state(0);
	let action = $state<AdminAction | null>(null);

	const id = $derived(page.params.id ?? '');
	// P3: the cover (any type); screenshots are in the Images gallery.
	const cover = $derived(game?.images.find((i) => i.isCover));
	// P1: primary first, then the Releases order.
	const downloadRoms = $derived(
		game ? [...game.roms.filter((r) => r.isPrimary), ...game.roms.filter((r) => !r.isPrimary)] : []
	);

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
			<dl class="row mb-3">
				<dt class="col-sm-3">Year</dt>
				<dd class="col-sm-9">{game.releaseYear ?? '—'}</dd>
				<dt class="col-sm-3">Developers</dt>
				<dd class="col-sm-9">{list(game.developers)}</dd>
				<dt class="col-sm-3">Publishers</dt>
				<dd class="col-sm-9">{list(game.publishers)}</dd>
				<dt class="col-sm-3">Genres</dt>
				<dd class="col-sm-9">{list(game.genres)}</dd>
			</dl>
			{#if game.description}
				<p style="white-space: pre-line">{game.description}</p>
			{/if}
			<div class="d-flex flex-wrap gap-2">
				<button
					type="button"
					class="btn btn-primary"
					onclick={() => download(`/api/games/${encodeURIComponent(id)}/download`)}
				>
					<Icon name="download" /> Download ZIP
				</button>
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

	<h2 class="h5">Releases</h2>
	<div class="card mb-4">
		<div class="table-responsive">
			<table class="table table-sm table-striped table-hover align-middle mb-0">
				<thead class="table-dark">
					<tr>
						<th scope="col">File</th>
						<th scope="col">Region</th>
						<th scope="col">Revision</th>
						<th scope="col">Flags</th>
						<th scope="col" class="text-end">Size</th>
						<th scope="col">Hashes</th>
						<th scope="col"><span class="visually-hidden">Actions</span></th>
					</tr>
				</thead>
				<tbody>
					{#each game.roms as rom (rom.id)}
						<tr>
							<td>
								<span
									class="d-inline-block text-truncate align-middle"
									style="max-width: 32rem"
									title={rom.fileName}>{rom.fileName}</span
								>
								{#if rom.isPrimary}<span class="badge text-bg-primary ms-1">Primary</span>{/if}
							</td>
							<td>
								{#each rom.regions as region (region)}
									<span class="badge text-bg-secondary me-1">{region}</span>
								{:else}
									—
								{/each}
							</td>
							<td>{rom.revision ?? '—'}</td>
							<td>{list(rom.flags)}</td>
							<td class="text-end"><FileSize bytes={rom.sizeBytes} /></td>
							<td class="small font-monospace">
								<div class="text-nowrap">
									CRC32 {rom.crc32}
									<CopyButton value={rom.crc32} label="CRC32" />
								</div>
								<div class="text-nowrap">
									SHA1 {rom.sha1}
									<CopyButton value={rom.sha1} label="SHA1" />
								</div>
							</td>
							<td class="text-nowrap text-end">
								{#if rom.header}
									<button
										type="button"
										class="btn btn-sm btn-outline-secondary"
										onclick={() => showHeader(rom)}
									>
										Header
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
										class="btn btn-sm btn-outline-secondary"
										aria-label="Move {rom.fileName}"
										onclick={() => (action = { kind: 'move', rom })}
									>
										<Icon name="move" />
									</button>
									<button
										type="button"
										class="btn btn-sm btn-outline-danger"
										aria-label="Delete {rom.fileName}"
										onclick={() => (action = { kind: 'deleteRom', rom })}
									>
										<Icon name="delete" />
									</button>
								{/if}
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	</div>

	{#if auth.canEditLibrary}
		<GameAdminModal {game} bind:action ondone={onAdminDone} />
	{/if}

	<!-- These modals stay below the admin modal: game-admin.test.ts takes the first .modal. -->
	<GameImages {game} onchanged={(detail) => (game = detail)} />

	<ConfirmModal bind:open={headerOpen} title="Header — {headerRom?.fileName ?? ''}" size="lg">
		{#if headerRom?.header}
			<table class="table table-sm table-striped table-hover mb-0">
				<caption class="visually-hidden">Header of {headerRom.fileName}</caption>
				<tbody>
					{#each Object.entries(headerRom.header) as [key, value] (key)}
						<tr>
							<th scope="row" class="fw-normal text-body-secondary">{formatKey(key)}</th>
							<td>{formatValue(value)}</td>
						</tr>
					{/each}
				</tbody>
			</table>
		{/if}
	</ConfirmModal>
{/if}
