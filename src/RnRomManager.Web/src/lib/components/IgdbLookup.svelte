<script lang="ts">
	import { onDestroy, onMount, untrack } from 'svelte';
	import { api } from '../api/client';
	import { isApiError } from '../api/errors';
	import type { GameDetail, IgdbMatch } from '../api/types';
	import type { GameEditForm } from '../gameEdit';
	import ErrorAlert from './ErrorAlert.svelte';
	import Icon from './Icon.svelte';

	interface Props {
		game: GameDetail;
		/** The Edit Metadata form; only read here, changed through `onapply`. */
		form: GameEditForm;
		/** M3: the new form after Not on IGDB (or Use this match); the modal switches to Details. */
		onapply: (form: GameEditForm) => void;
		/** M2: search as soon as the panel mounts (IGDB configured only). */
		autostart?: boolean;
	}

	let { game, form, onapply, autostart = false }: Props = $props();

	const maxQuery = 100;

	let q = $state(untrack(() => game.displayTitle.slice(0, maxQuery)));
	let searching = $state(false);
	let notConfigured = $state(untrack(() => !game.igdbConfigured));
	let error = $state<unknown>(null);
	let matches = $state<IgdbMatch[] | null>(null);
	/** The query the shown matches (or the empty text) are for. */
	let searched = $state('');
	let selected = $state<number | null>(null);
	let controller: AbortController | undefined;

	async function search(): Promise<void> {
		const term = q.trim();
		if (!term || notConfigured) return;
		controller?.abort();
		const current = (controller = new AbortController());
		searching = true;
		error = null;
		matches = null;
		selected = null;
		try {
			const params = new URLSearchParams({ q: term });
			const result = await api.get<{ matches: IgdbMatch[] }>(
				`/api/games/${encodeURIComponent(game.id)}/igdb-matches?${params}`,
				{ signal: current.signal }
			);
			if (current !== controller) return;
			matches = result.matches;
			searched = term;
		} catch (e) {
			if (current !== controller || current.signal.aborted) return;
			if (isApiError(e, 'IgdbNotConfigured')) notConfigured = true;
			else error = e;
		} finally {
			if (current === controller) searching = false;
		}
	}

	onMount(() => {
		if (autostart) void search();
	});

	// M3: closing the modal unmounts the panel, which aborts a running search.
	onDestroy(() => controller?.abort());
</script>

{#if notConfigured}
	<div class="alert alert-info mb-0">
		Metadata lookup needs an IGDB Client ID and secret. Set <code class="text-reset"
			>Sources:IgdbClientId</code
		>
		and <code class="text-reset">Sources:IgdbClientSecret</code> and restart.
	</div>
{:else}
	<form
		class="mb-3"
		novalidate
		onsubmit={(e) => {
			e.preventDefault();
			void search();
		}}
	>
		<label class="form-label" for="igdb-q">Search IGDB for</label>
		<div class="d-flex gap-2">
			<input
				id="igdb-q"
				class="form-control"
				maxlength={maxQuery}
				aria-describedby="igdb-q-help"
				bind:value={q}
			/>
			<button type="submit" class="btn btn-primary text-nowrap" disabled={searching || !q.trim()}
				><Icon name="search" /> Lookup Info</button
			>
		</div>
		<div id="igdb-q-help" class="form-text">
			Searches IGDB on {game.systemName}, then on every platform when that finds nothing.
		</div>
	</form>

	<div aria-live="polite">
		{#if searching}
			<p class="mb-3">Searching IGDB…</p>
		{:else if error}
			<ErrorAlert {error} onretry={search} />
		{:else if matches && matches.length === 0}
			<p class="mb-3">No IGDB matches for “{searched}”.</p>
		{:else if matches}
			<div class="list-group mb-3" role="group" aria-label="IGDB matches">
				{#each matches as match (match.igdbId)}
					<button
						type="button"
						class="list-group-item list-group-item-action d-flex align-items-center gap-2"
						class:active={selected === match.igdbId}
						aria-pressed={selected === match.igdbId}
						onclick={() => (selected = match.igdbId)}
					>
						{#if match.coverUrl}
							<img
								src={match.coverUrl}
								alt=""
								width="45"
								height="64"
								class="object-fit-cover flex-shrink-0"
								loading="lazy"
							/>
						{/if}
						<span class="me-auto">
							<span class="d-block"
								>{match.name}{match.releaseYear === null ? '' : ` (${match.releaseYear})`}</span
							>
							{#if match.developers.length}
								<span class="d-block small">{match.developers[0]}</span>
							{/if}
						</span>
						{#if match.igdbId === form.igdbId}
							<span class="badge text-bg-success">Linked</span>
						{/if}
					</button>
				{/each}
			</div>
		{/if}
	</div>

	<button
		type="button"
		class="btn btn-outline-secondary btn-sm"
		onclick={() => onapply({ ...form, igdbId: 0 })}>Not on IGDB</button
	>
{/if}
