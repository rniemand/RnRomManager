<script lang="ts">
	import { untrack } from 'svelte';
	import BaseModal from './BaseModal.svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import RadioButtons from '#lib/components/forms/RadioButtons.svelte';
	import ErrorAlert from '#lib/components/common/ErrorAlert.svelte';
	import Loading from '#lib/components/common/Loading.svelte';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import type { GameDetail, ImageSuggestion, ImageSuggestions } from '#lib/types/index.js';

	interface Props {
		/** IGDB suggestions S12: opening loads the box art suggestions. */
		open: boolean;
		gameId: string;
		/** After an add: the fresh detail. */
		onchanged: (detail: GameDetail) => void;
		/** Any failure the dialog doesn't show itself. */
		onfailed: (e: unknown) => void;
		/** IM4: the type each opening starts on. */
		initialType?: 'BoxArt' | 'Snap';
	}

	let { open = $bindable(), gameId, onchanged, onfailed, initialType = 'BoxArt' }: Props = $props();

	let suggestType = $state<'BoxArt' | 'Snap'>('BoxArt');
	let suggestions = $state<ImageSuggestion[] | null>(null);
	let suggestError = $state<unknown>(null);
	let suggestNotConfigured = $state(false);
	/** The tile whose add is running. */
	let adding = $state<number | null>(null);
	/** Bumped by each load and each close: a response from an older one is ignored. */
	let loadSeq = 0;
	let loadAbort: AbortController | null = null;
	const suggestLabel = $derived(suggestType === 'BoxArt' ? 'box art' : 'screenshot');

	async function loadSuggestions(): Promise<void> {
		loadAbort?.abort();
		const controller = new AbortController();
		loadAbort = controller;
		const seq = ++loadSeq;
		suggestions = null;
		suggestError = null;
		suggestNotConfigured = false;
		try {
			const result = await api.get<ImageSuggestions>(
				`/api/games/${encodeURIComponent(gameId)}/image-suggestions?type=${suggestType}`,
				{ signal: controller.signal }
			);
			if (seq === loadSeq) suggestions = result.suggestions;
		} catch (e) {
			if (seq !== loadSeq) return;
			if (isApiError(e, 'IgdbNotConfigured')) suggestNotConfigured = true;
			else suggestError = e;
		}
	}

	// Opening starts on initialType and loads; S12: closing aborts the load and makes any late answer stale.
	$effect(() => {
		if (open) {
			untrack(() => {
				suggestType = initialType;
				void loadSuggestions();
			});
			return;
		}
		loadAbort?.abort();
		loadAbort = null;
		loadSeq++;
	});

	async function addSuggestion(suggestion: ImageSuggestion, index: number): Promise<void> {
		const seq = loadSeq;
		adding = index;
		try {
			onchanged(
				await api.post<GameDetail>(`/api/games/${encodeURIComponent(gameId)}/images/from-url`, {
					url: suggestion.imageUrl,
					type: suggestType
				})
			);
			toasts.show('Image added from IGDB.', 'success');
			// S1: closes only the same opening and type, never a dialog reopened meanwhile.
			if (seq === loadSeq) open = false;
		} catch (e) {
			if (isApiError(e, 'ImageDownloadFailed') || isApiError(e, 'ImageUrlBlocked'))
				toasts.show(e.title, 'danger');
			else if (isApiError(e, 'ValidationFailed'))
				toasts.show(e.errors.url?.[0] ?? e.errors.type?.[0] ?? e.title, 'danger');
			else onfailed(e);
		} finally {
			adding = null;
		}
	}
</script>

<!-- IGDB suggestions S12: info mode (Close only); Close stays enabled while an add runs (S13). -->
<BaseModal bind:open title="Suggest Images" size="lg">
	<!-- Only while open, so its controls never sit next to the Upload Image dialog's in the page. -->
	{#if open}
		<RadioButtons
			class="mb-3"
			role="group"
			aria-label="Image type"
			name="suggest-type"
			size="sm"
			disabled={adding !== null}
			options={[
				{ value: 'BoxArt', label: 'Box art', id: 'suggest-type-boxart' },
				{ value: 'Snap', label: 'Screenshot', id: 'suggest-type-snap' }
			]}
			bind:value={suggestType}
			onchange={loadSuggestions}
		/>
		<div aria-live="polite">
			{#if suggestNotConfigured}
				<div class="alert alert-info mb-0">
					Image suggestions need an IGDB Client ID and secret. Set <code class="text-reset"
						>Sources:IgdbClientId</code
					>
					and <code class="text-reset">Sources:IgdbClientSecret</code> and restart.
				</div>
			{:else if suggestError}
				<ErrorAlert error={suggestError} onretry={loadSuggestions} />
			{:else if !suggestions}
				<Loading />
			{:else if suggestions.length === 0}
				<p class="mb-0">
					{suggestType === 'BoxArt'
						? 'IGDB has no box art for this game.'
						: 'IGDB has no screenshots for this game.'}
				</p>
			{:else}
				<div class="row row-cols-2 row-cols-md-5 g-2">
					{#each suggestions as suggestion, i (suggestion.imageUrl)}
						<div class="col">
							<Button
								class="btn p-0 border card h-100 w-100 position-relative overflow-hidden"
								aria-label="Add {suggestion.gameName} {suggestLabel} {i + 1}"
								disabled={adding !== null}
								onclick={() => addSuggestion(suggestion, i)}
							>
								<img
									class="suggestion-preview card-img-top object-fit-contain bg-body-tertiary"
									src={suggestion.previewUrl}
									alt=""
									loading="lazy"
								/>
								{#if adding === i}
									<span
										class="spinner-border spinner-border-sm position-absolute top-0 start-0 m-1"
										aria-hidden="true"
									></span>
								{/if}
								<span class="d-block w-100 p-1 small text-start">
									<span class="d-block text-truncate" title={suggestion.gameName}
										>{suggestion.gameName}</span
									>
									{#if suggestion.width && suggestion.height}
										<span class="d-block text-body-secondary"
											>{suggestion.width}×{suggestion.height}</span
										>
									{/if}
								</span>
							</Button>
						</div>
					{/each}
				</div>
			{/if}
		</div>
		<div class="form-text">Suggestions from IGDB.com. Click one to add it to the game.</div>
	{/if}
</BaseModal>

<style>
	.suggestion-preview {
		height: 8rem;
	}
</style>
