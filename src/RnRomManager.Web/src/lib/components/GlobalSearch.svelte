<script lang="ts">
	import { onDestroy, onMount, tick } from 'svelte';
	import { goto } from '$app/navigation';
	import { resolve } from '$app/paths';
	import { api } from '../api/client';
	import { isApiError } from '../api/errors';
	import type { CollectionListItem, GameListItem, Page } from '../api/types';
	import { Modal } from '../bootstrap';
	import { releaseModal } from '../modal';
	import { auth } from '../stores/auth.svelte';
	import FileSize from './FileSize.svelte';
	import Icon from './Icon.svelte';
	import RatingControl from './RatingControl.svelte';
	import SystemBadge from './SystemBadge.svelte';

	/**
	 * Global search (spec B12–B16, B27): Ctrl+Space, Ctrl+K, Cmd+K or the navbar button open it. Up to 5 of the
	 * caller's collections (S2: chips), then the top 20 games by relevance (rich rows), in one keyboard-navigable
	 * combobox list.
	 */
	interface Props {
		/** Shown while true; set back to false when closed. */
		open: boolean;
	}

	let { open = $bindable() }: Props = $props();

	const listId = 'global-search-results';

	let element: HTMLDivElement;
	let input = $state<HTMLInputElement>();
	let modal = $state<InstanceType<typeof Modal> | null>(null);
	let query = $state('');
	type Entry =
		{ kind: 'collection'; item: CollectionListItem } | { kind: 'game'; item: GameListItem };

	let collections = $state<CollectionListItem[]>([]);
	let games = $state<GameListItem[]>([]);
	/** Collections first, then games: what the arrow keys walk through. */
	const results = $derived<Entry[]>([
		...collections.map((item) => ({ kind: 'collection' as const, item })),
		...games.map((item) => ({ kind: 'game' as const, item }))
	]);
	let active = $state(0);
	let status = $state<'hint' | 'loading' | 'done' | 'error'>('hint');
	let error = $state('');
	let timer: ReturnType<typeof setTimeout> | undefined;
	let controller: AbortController | undefined;
	let previous: HTMLElement | null = null;
	let navigated = false;

	const matchLabels = { developer: 'Developer', publisher: 'Publisher', genre: 'Genre' } as const;

	const enabled = $derived(!!auth.me && !auth.me.mustChangePassword);

	onMount(() => {
		modal = new Modal(element, { backdrop: 'static', keyboard: true }); // M1
		element.addEventListener('shown.bs.modal', () => input?.focus());
		element.addEventListener('hidden.bs.modal', () => (open = false));
	});

	onDestroy(() => {
		cancel();
		releaseModal(element); // G1: before dispose()
		modal?.dispose();
	});

	$effect(() => {
		if (!modal) return;
		if (open) {
			navigated = false;
			previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
			modal.show();
			void tick().then(() => input?.focus());
		} else {
			modal.hide();
			if (!navigated) previous?.focus();
			previous = null;
		}
	});

	function isShortcut(e: KeyboardEvent): boolean {
		if (!e.ctrlKey && !e.metaKey) return false;
		if (e.ctrlKey && (e.code === 'Space' || e.key === ' ')) return true;
		return e.key.toLowerCase() === 'k' && !e.altKey && !e.shiftKey;
	}

	function onWindowKeydown(e: KeyboardEvent): void {
		if (!enabled || !isShortcut(e)) return;
		if (open) {
			e.preventDefault();
			input?.focus();
			return;
		}
		// Another dialog (Edit metadata, a confirm) owns the keyboard.
		if (document.querySelector('.modal.show')) return;
		e.preventDefault();
		open = true;
	}

	function cancel(): void {
		clearTimeout(timer);
		controller?.abort();
	}

	function oninput(): void {
		cancel();
		const q = query.trim();
		collections = [];
		games = [];
		active = 0;
		if (q.length < 2) {
			status = 'hint';
			return;
		}
		status = 'loading';
		timer = setTimeout(() => void search(q), 250);
	}

	async function search(q: string): Promise<void> {
		controller = new AbortController();
		const signal = controller.signal;
		const gameParams = new URLSearchParams({ q, sort: 'relevance', pageSize: '20' });
		const collectionParams = new URLSearchParams({ q, pageSize: '5' });
		// B27: one failing keeps the other's results and shows its error.
		const [gamePage, collectionPage] = await Promise.allSettled([
			api.get<Page<GameListItem>>(`/api/games?${gameParams}`, { signal }),
			api.get<Page<CollectionListItem>>(`/api/collections?${collectionParams}`, { signal })
		]);
		if (signal.aborted) return;
		games = gamePage.status === 'fulfilled' ? gamePage.value.items : [];
		collections = collectionPage.status === 'fulfilled' ? collectionPage.value.items : [];
		const failure = [gamePage, collectionPage].find((r) => r.status === 'rejected');
		error = failure
			? isApiError(failure.reason)
				? failure.reason.title
				: 'The search failed.'
			: '';
		active = 0;
		status =
			gamePage.status === 'rejected' && collectionPage.status === 'rejected' ? 'error' : 'done';
	}

	async function choose(entry: Entry): Promise<void> {
		navigated = true;
		cancel();
		query = '';
		collections = [];
		games = [];
		status = 'hint';
		open = false;
		await goto(
			entry.kind === 'game'
				? resolve('/games/[id]', { id: entry.item.id })
				: resolve('/collections/[id]', { id: entry.item.id })
		);
	}

	function onInputKeydown(e: KeyboardEvent): void {
		if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
			if (!results.length) return;
			e.preventDefault();
			const step = e.key === 'ArrowDown' ? 1 : -1;
			active = (active + step + results.length) % results.length;
		} else if (e.key === 'Enter') {
			e.preventDefault();
			if (results[active]) void choose(results[active]);
		}
	}
</script>

<svelte:window onkeydown={onWindowKeydown} />

<div
	bind:this={element}
	class="modal fade global-search"
	tabindex="-1"
	aria-labelledby="global-search-title"
	aria-hidden="true"
>
	<div class="modal-dialog modal-xl modal-dialog-scrollable">
		<div class="modal-content">
			<div class="modal-header">
				<h2 class="modal-title fs-5" id="global-search-title">Search</h2>
				<button type="button" class="btn-close" aria-label="Close" onclick={() => (open = false)}
				></button>
			</div>
			<div class="modal-body">
				<label class="visually-hidden" for="global-search-input">Search games and collections</label
				>
				<div class="input-group">
					<span class="input-group-text"><Icon name="search" /></span>
					<input
						bind:this={input}
						id="global-search-input"
						type="search"
						class="form-control"
						placeholder="Search games and collections"
						autocomplete="off"
						maxlength="255"
						role="combobox"
						aria-expanded={results.length > 0}
						aria-controls={listId}
						aria-autocomplete="list"
						aria-activedescendant={results.length ? `${listId}-${active}` : undefined}
						bind:value={query}
						{oninput}
						onkeydown={onInputKeydown}
					/>
				</div>

				<div class="mt-3" aria-live="polite">
					{#if status === 'hint'}
						<p class="text-body-secondary small mb-0">Type at least 2 characters</p>
					{:else if status === 'loading'}
						<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>
						<span class="visually-hidden">Searching…</span>
					{:else}
						{#if error}<p class="text-danger small mb-0">{error}</p>{/if}
						{#if status === 'done' && results.length === 0 && !error}
							<p class="text-body-secondary small mb-0">No games match</p>
						{/if}
					{/if}
				</div>

				<ul id={listId} class="list-group mt-2" role="listbox" aria-label="Search results">
					{#if collections.length}
						<li class="list-group-item small fw-semibold text-body-secondary" role="presentation">
							Collections
						</li>
						<!-- S2: the collections as compact chips, each still an option of the one listbox. -->
						<li
							class="list-group-item d-flex flex-wrap gap-2"
							role="group"
							aria-label="Collections"
						>
							{#each collections as collection, i (collection.id)}
								<div
									id="{listId}-{i}"
									class="btn btn-sm btn-outline-secondary d-inline-flex align-items-center gap-1"
									class:active={i === active}
									role="option"
									aria-selected={i === active}
									tabindex="-1"
									onclick={() => void choose({ kind: 'collection', item: collection })}
									onkeydown={() => {}}
									onmousemove={() => (active = i)}
								>
									{#if collection.coverUrl}
										<img
											class="rounded object-fit-cover chip-thumb"
											src={collection.coverUrl}
											alt=""
											loading="lazy"
										/>
									{:else}
										<Icon name="collections" />
									{/if}
									<span>{collection.name}</span>
									<span class:text-body-secondary={i !== active}>· {collection.gameCount}</span>
								</div>
							{/each}
						</li>
					{/if}
					{#if games.length}
						<li class="list-group-item small fw-semibold text-body-secondary" role="presentation">
							Games
						</li>
					{/if}
					{#each games as game, g (game.id)}
						{@const i = collections.length + g}
						{@const meta = [game.releaseYear, game.regions.join(', ')].filter(Boolean).join(' · ')}
						<li
							id="{listId}-{i}"
							class="list-group-item list-group-item-action d-grid align-items-center gap-3 game-row"
							class:active={i === active}
							role="option"
							aria-selected={i === active}
							onclick={() => void choose({ kind: 'game', item: game })}
							onkeydown={() => {}}
							onmousemove={() => (active = i)}
						>
							<div
								class="bg-body-secondary rounded d-flex align-items-center justify-content-center overflow-hidden game-thumb"
							>
								{#if game.boxArtUrl}
									<img
										class="w-100 h-100 object-fit-contain"
										src={game.boxArtUrl}
										alt=""
										loading="lazy"
									/>
								{:else}
									<Icon name="system-{game.system}" class="fs-4" />
								{/if}
							</div>
							<div class="min-w-0">
								<div class="text-truncate fw-semibold">{game.displayTitle}</div>
								{#if game.matchedOn}
									<div class="text-truncate small" class:text-body-secondary={i !== active}>
										{matchLabels[game.matchedOn.field]}: {game.matchedOn.value}
									</div>
								{/if}
							</div>
							<SystemBadge
								system={game.system}
								name={game.systemShortName}
								title={game.systemName}
							/>
							<!-- Muted text doesn't contrast on the active (blue) row, which takes its own colour. -->
							<div
								class="d-none d-md-block small text-nowrap"
								class:text-body-secondary={i !== active}
							>
								{meta}{#if game.primarySizeBytes != null}{meta ? ' · ' : ''}<FileSize
										bytes={game.primarySizeBytes}
									/>{/if}
							</div>
							<div class="d-none d-md-block">
								<RatingControl
									gameId={game.id}
									title={game.displayTitle}
									rating={game.rating}
									favourite={game.favourite}
									readonly
								/>
							</div>
						</li>
					{/each}
				</ul>
			</div>
		</div>
	</div>
</div>

<style>
	.chip-thumb {
		width: 20px;
		height: 20px;
	}

	.game-thumb {
		width: 48px;
		height: 48px;
	}

	.min-w-0 {
		min-width: 0;
	}

	/* S2: thumbnail, title, badge; meta and rating join from md. */
	.game-row {
		grid-template-columns: 48px 1fr auto;
	}

	@media (min-width: 768px) {
		.game-row {
			grid-template-columns: 48px 1fr auto auto auto;
		}
	}
</style>
