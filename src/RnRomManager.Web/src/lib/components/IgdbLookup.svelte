<script lang="ts">
	import { onDestroy, onMount, untrack } from 'svelte';
	import { api } from '../api/client';
	import { isApiError } from '../api/errors';
	import type { GameDetail, IgdbMatch } from '../api/types';
	import {
		applyMatch,
		copyFields,
		defaultCopyFields,
		toList,
		type CopyField,
		type CopyState,
		type GameEditForm
	} from '../gameEdit';
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
	/** M3: the compare table's rows for the selected match. */
	let copy = $state<CopyState | null>(null);
	const chosen = $derived(matches?.find((m) => m.igdbId === selected) ?? null);

	const fieldLabels: Record<CopyField, string> = {
		title: 'Title',
		releaseYear: 'Release year',
		developers: 'Developers',
		publishers: 'Publishers',
		genres: 'Genres',
		description: 'Description'
	};

	function select(match: IgdbMatch): void {
		selected = match.igdbId;
		copy = defaultCopyFields(form, match);
	}

	/** M3: lists joined with `, `, the description cut to 200 characters, empty as `—`. */
	function shown(field: CopyField, value: string | string[] | number | null): string {
		const text = Array.isArray(value) ? value.join(', ') : (value?.toString().trim() ?? '');
		if (!text) return '—';
		return field === 'description' && text.length > 200 ? `${text.slice(0, 200)}…` : text;
	}

	function current(field: CopyField): string {
		const value = form[field];
		return shown(
			field,
			field === 'developers' || field === 'publishers' || field === 'genres' ? toList(value) : value
		);
	}

	function igdb(match: IgdbMatch, field: CopyField): string {
		return shown(field, field === 'title' ? match.name : match[field]);
	}
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
		copy = null;
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
						onclick={() => select(match)}
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

	{#if chosen && copy}
		<div class="table-responsive mb-2">
			<table class="table table-sm align-middle mb-0">
				<caption class="visually-hidden">Fields to copy from IGDB</caption>
				<thead>
					<tr>
						<th scope="col"><span class="visually-hidden">Copy</span></th>
						<th scope="col">Field</th>
						<th scope="col">Current</th>
						<th scope="col">IGDB</th>
					</tr>
				</thead>
				<tbody>
					{#each copyFields as field (field)}
						<tr>
							<td>
								<input
									type="checkbox"
									class="form-check-input"
									aria-label="Copy {fieldLabels[field]}"
									disabled={copy[field].disabled}
									bind:checked={copy[field].checked}
								/>
							</td>
							<th scope="row" class="fw-normal">{fieldLabels[field]}</th>
							<td>{current(field)}</td>
							<td>{igdb(chosen, field)}</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
		{#if chosen.url}
			<p class="mb-2">
				<!-- eslint-disable-next-line svelte/no-navigation-without-resolve -- external IGDB page -->
				<a href={chosen.url} target="_blank" rel="noopener noreferrer">View on IGDB</a>
			</p>
		{/if}
		<button
			type="button"
			class="btn btn-primary btn-sm mb-3"
			onclick={() =>
				onapply(
					applyMatch(
						form,
						chosen,
						copyFields.filter((f) => copy![f].checked && !copy![f].disabled)
					)
				)}>Use this match</button
		>
	{/if}

	<div>
		<button
			type="button"
			class="btn btn-outline-secondary btn-sm"
			onclick={() => onapply({ ...form, igdbId: 0 })}>Not on IGDB</button
		>
	</div>
{/if}
