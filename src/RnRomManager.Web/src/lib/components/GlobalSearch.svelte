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
	import Icon from './Icon.svelte';
	import RatingControl from './RatingControl.svelte';

	/**
	 * Global search (spec B12–B16, B27): Ctrl+Space, Ctrl+K, Cmd+K or the navbar button open it. Up to 5 of the
	 * caller's collections, then the top 10 games by relevance, in one keyboard-navigable combobox list.
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
		const gameParams = new URLSearchParams({ q, sort: 'relevance', pageSize: '10' });
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
	<div class="modal-dialog modal-dialog-scrollable">
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
					{#each results as entry, i (`${entry.kind}-${entry.item.id}`)}
						{#if i === 0 && entry.kind === 'collection'}
							<li class="list-group-item small fw-semibold text-body-secondary" role="presentation">
								Collections
							</li>
						{/if}
						{#if entry.kind === 'game' && i === collections.length}
							<li class="list-group-item small fw-semibold text-body-secondary" role="presentation">
								Games
							</li>
						{/if}
						<li
							id="{listId}-{i}"
							class="list-group-item list-group-item-action d-flex align-items-center gap-3"
							class:active={i === active}
							role="option"
							aria-selected={i === active}
							onclick={() => void choose(entry)}
							onkeydown={() => {}}
							onmousemove={() => (active = i)}
						>
							<div
								class="flex-shrink-0 bg-body-secondary rounded d-flex align-items-center justify-content-center overflow-hidden"
								style="width: 3rem; height: 3rem"
							>
								{#if entry.kind === 'collection'}
									{#if entry.item.coverUrl}
										<img
											class="w-100 h-100 object-fit-cover"
											src={entry.item.coverUrl}
											alt=""
											loading="lazy"
										/>
									{:else}
										<Icon name="collections" class="fs-4" />
									{/if}
								{:else if entry.item.boxArtUrl}
									<img
										class="w-100 h-100 object-fit-contain"
										src={entry.item.boxArtUrl}
										alt=""
										loading="lazy"
									/>
								{:else}
									<Icon name="system-{entry.item.system}" class="fs-4" />
								{/if}
							</div>
							<div class="flex-grow-1 min-w-0" style="min-width: 0">
								{#if entry.kind === 'collection'}
									<div class="text-truncate fw-semibold">{entry.item.name}</div>
									<div class="small text-body-secondary">
										{`Collection · ${entry.item.gameCount} ${entry.item.gameCount === 1 ? 'game' : 'games'}`}
									</div>
								{:else}
									{@const game = entry.item}
									<div class="text-truncate fw-semibold">{game.displayTitle}</div>
									{#if game.matchedOn}
										<div class="text-truncate small text-body-secondary">
											{matchLabels[game.matchedOn.field]}: {game.matchedOn.value}
										</div>
									{/if}
									<div class="d-flex flex-wrap align-items-center gap-2 small">
										<span class="badge text-bg-secondary">{game.systemName}</span>
										{#if game.releaseYear}<span>{game.releaseYear}</span>{/if}
										<RatingControl
											gameId={game.id}
											title={game.displayTitle}
											rating={game.rating}
											favourite={game.favourite}
											readonly
										/>
									</div>
								{/if}
							</div>
						</li>
					{/each}
				</ul>
			</div>
		</div>
	</div>
</div>
