<script lang="ts">
	import { onDestroy } from 'svelte';
	import { api } from '../api/client';
	import type { GameListItem, Page } from '../api/types';
	import Icon from './Icon.svelte';
	import SystemBadge from './SystemBadge.svelte';

	/**
	 * Game picker for move / merge / attach (spec §11): `GET /api/games?q=…&system=…&pageSize=10`, debounced 300 ms,
	 * from 2 characters; the source game is left out client-side. Without `system` it searches every system and
	 * shows each result's short system name (B24, P4). `excludeCollection` leaves out that collection's games (P2).
	 */
	interface Props {
		id: string;
		label: string;
		system?: string;
		excludeId?: string;
		excludeCollection?: string;
		selected: GameListItem | null;
		invalid?: string;
	}

	let {
		id,
		label,
		system,
		excludeId,
		excludeCollection,
		selected = $bindable(),
		invalid
	}: Props = $props();

	let query = $state('');
	let results = $state<GameListItem[]>([]);
	let searching = $state(false);
	let timer: ReturnType<typeof setTimeout> | undefined;
	let controller: AbortController | undefined;

	function cancel(): void {
		clearTimeout(timer);
		controller?.abort();
	}

	function oninput(): void {
		selected = null;
		cancel();
		const q = query.trim();
		if (q.length < 2) {
			results = [];
			searching = false;
			return;
		}
		searching = true;
		timer = setTimeout(() => void search(q), 300);
	}

	async function search(q: string): Promise<void> {
		controller = new AbortController();
		const signal = controller.signal;
		searching = true;
		const params = new URLSearchParams({
			q,
			...(system ? { system } : {}),
			...(excludeCollection ? { notInCollection: excludeCollection } : {}),
			pageSize: '10'
		});
		try {
			const page = await api.get<Page<GameListItem>>(`/api/games?${params}`, { signal });
			results = page.items.filter((game) => game.id !== excludeId);
		} catch {
			if (!signal.aborted) results = [];
		} finally {
			if (!signal.aborted) searching = false;
		}
	}

	/** Picks `game` as if it was chosen from the results (GP5: the merge suggestions call this through `bind:this`). */
	export function select(game: GameListItem): void {
		cancel();
		selected = game;
		query = game.displayTitle;
		results = [];
	}

	onDestroy(cancel);
</script>

<label class="form-label" for={id}>{label}</label>
<input
	{id}
	type="search"
	class="form-control"
	class:is-invalid={invalid}
	placeholder="Type at least 2 characters"
	autocomplete="off"
	aria-describedby="{id}-help"
	bind:value={query}
	{oninput}
/>
{#if invalid}<div class="invalid-feedback">{invalid}</div>{/if}
<div id="{id}-help" class="form-text" aria-live="polite">
	{#if selected}
		Selected: {selected.displayTitle}
	{:else if searching}
		Searching…
	{:else if query.trim().length >= 2 && results.length === 0}
		No matching games.
	{/if}
</div>
{#if results.length}
	<div class="list-group mt-1">
		{#each results as game (game.id)}
			<button
				type="button"
				class="list-group-item list-group-item-action d-flex align-items-center gap-2"
				onclick={() => select(game)}
			>
				<span
					class="flex-shrink-0 bg-body-secondary rounded d-flex align-items-center justify-content-center overflow-hidden"
					style="width: 32px; height: 40px"
				>
					{#if game.boxArtUrl}
						<img
							class="w-100 h-100 object-fit-contain"
							src={game.boxArtUrl}
							alt=""
							loading="lazy"
						/>
					{:else}
						<Icon name="system-{game.system}" />
					{/if}
				</span>
				{#if !system}<SystemBadge
						system={game.system}
						name={game.systemShortName}
						title={game.systemName}
					/>{/if}
				{game.displayTitle}
				{#if game.releaseYear}<span class="text-body-secondary small">({game.releaseYear})</span
					>{/if}
			</button>
		{/each}
	</div>
{/if}
