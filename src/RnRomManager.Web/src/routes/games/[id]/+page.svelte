<script lang="ts">
	import { SvelteSet } from 'svelte/reactivity';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { GameDetail, RomDetail } from '#lib/api/types.js';
	import CopyButton from '#lib/components/CopyButton.svelte';
	import GameAdminModal, {
		type AdminAction,
		type AdminOutcome
	} from '#lib/components/GameAdminModal.svelte';
	import EmptyState from '#lib/components/EmptyState.svelte';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
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
	const expanded = new SvelteSet<number>();
	let reload = $state(0);
	let action = $state<AdminAction | null>(null);

	const id = $derived(page.params.id ?? '');
	const boxArt = $derived(game?.images.find((i) => i.type === 'BoxArt'));
	const snap = $derived(game?.images.find((i) => i.type === 'Snap'));

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
		expanded.clear();
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

	function toggle(romId: number): void {
		if (!expanded.delete(romId)) expanded.add(romId);
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
			{#if boxArt}
				<img
					class="img-fluid rounded border"
					src={boxArt.url}
					width={boxArt.width}
					height={boxArt.height}
					alt="{game.displayTitle} box art"
				/>
			{/if}
			{#if snap}
				<img
					class="img-fluid rounded border mt-3"
					src={snap.url}
					width={snap.width}
					height={snap.height}
					alt="{game.displayTitle} screenshot"
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
			<button
				type="button"
				class="btn btn-primary"
				onclick={() => download(`/api/games/${encodeURIComponent(id)}/download`)}
			>
				<Icon name="download" /> Download ZIP
			</button>
			{#if auth.isAdmin}
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
	<div class="table-responsive">
		<table class="table align-middle">
			<thead>
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
							{rom.fileName}
							{#if rom.isPrimary}<span class="badge text-bg-primary ms-1">Primary</span>{/if}
						</td>
						<td>{list(rom.regions)}</td>
						<td>{rom.revision ?? '—'}</td>
						<td>{list(rom.flags)}</td>
						<td class="text-end text-nowrap">{formatSize(rom.sizeBytes)}</td>
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
									aria-expanded={expanded.has(rom.id)}
									aria-controls="header-{rom.id}"
									onclick={() => toggle(rom.id)}
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
							{#if auth.isAdmin}
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
					{#if rom.header && expanded.has(rom.id)}
						<tr id="header-{rom.id}">
							<td colspan="7">
								<table class="table table-sm mb-0">
									<caption class="visually-hidden">Header of {rom.fileName}</caption>
									<tbody>
										{#each Object.entries(rom.header) as [key, value] (key)}
											<tr>
												<th scope="row" class="fw-normal text-body-secondary">{formatKey(key)}</th>
												<td>{formatValue(value)}</td>
											</tr>
										{/each}
									</tbody>
								</table>
							</td>
						</tr>
					{/if}
				{/each}
			</tbody>
		</table>
	</div>

	{#if auth.isAdmin}
		<GameAdminModal {game} bind:action ondone={onAdminDone} />
	{/if}
{/if}
