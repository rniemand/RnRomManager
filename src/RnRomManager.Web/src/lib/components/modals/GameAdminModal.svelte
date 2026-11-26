<script lang="ts">
	import Checkbox from '#lib/components/forms/Checkbox.svelte';
	import Textarea from '#lib/components/forms/Textarea.svelte';
	import TextInput from '#lib/components/forms/TextInput.svelte';
	import Button from '#lib/components/forms/Button.svelte';
	import { untrack } from 'svelte';
	import type {
		GameDetail,
		GameListItem,
		IgdbMatch,
		Page,
		GameEditErrors,
		GameEditForm,
		AdminAction,
		AdminOutcome
	} from '#lib/types/index.js';
	import { api } from '#lib/api/client.js';
	import { ApiError, isApiError } from '#lib/api/errors.js';
	import { titleError, toForm, toPatch, validateGameEdit } from '#lib/gameEdit.js';
	import { activity } from '#lib/stores/activity.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import BaseModal from './BaseModal.svelte';
	import GameTypeahead from '#lib/components/forms/GameTypeahead.svelte';
	import IgdbLookup from '#lib/components/games/IgdbLookup.svelte';
	import NavTabs from '#lib/components/layout/NavTabs.svelte';

	interface Props {
		game: GameDetail;
		/** Opens the modal for this action; set back to null when closed. */
		action: AdminAction | null;
		ondone: (outcome: AdminOutcome) => void;
	}

	let { game, action = $bindable(), ondone }: Props = $props();

	const changed = 'This game changed, please retry.';

	let open = $state(false);
	let busy = $state(false);
	let error = $state<string | null>(null);
	let form = $state<GameEditForm>(untrack(() => toForm(game)));
	let errors = $state<GameEditErrors>({});
	let target = $state<GameListItem | null>(null);
	let targetError = $state<string | undefined>();
	let moveTo = $state<'existing' | 'new'>('existing');
	let newTitle = $state('');
	let newTitleError = $state<string | undefined>();
	let typeahead = $state<{ select: (game: GameListItem) => void }>();
	let suggestions = $state<GameListItem[]>([]);
	let suggestionController: AbortController | undefined;
	/** M1: Edit Metadata's tab; M2 opens it on Lookup Info with `lookup: true`. */
	let editTab = $state<'details' | 'lookup'>('details');
	const editTabs = (
		[
			['details', 'Details'],
			['lookup', 'Lookup Info']
		] as const
	).map(([id, label]) => ({ id, label, tabId: `edit-${id}-tab`, controls: `edit-${id}-panel` }));
	/** M1: the Lookup Info panel, and what its footer buttons need from it. */
	let lookup = $state<ReturnType<typeof IgdbLookup>>();
	let lookupChosen = $state<IgdbMatch | null>(null);
	let lookupNotConfigured = $state(false);
	/** M4: Lookup Info changed the form; cleared when the modal reopens. */
	let igdbApplied = $state(false);
	/** The used match's full-size cover, added as the box art cover after the save; cleared when the modal reopens. */
	let pendingCover = $state<string | null>(null);
	/** Only a game without real box art (none, or just the placeholder) takes the match's cover. */
	const needsCover = $derived(!game.images.some((i) => i.type === 'BoxArt' && !i.isDefault));

	/** GP5: the display title without `(…)`/`[…]` tags, cut to `GameQuery.MaxWords` (8) words. */
	function mergeTerm(title: string): string {
		return title
			.replace(/\([^)]*\)|\[[^\]]*\]/g, ' ')
			.split(/\s+/)
			.filter(Boolean)
			.slice(0, 8)
			.join(' ');
	}

	async function fetchSuggestions(q: string, signal: AbortSignal): Promise<GameListItem[]> {
		const params = new URLSearchParams({
			system: game.system,
			q,
			sort: 'relevance',
			pageSize: '6'
		});
		const page = await api.get<Page<GameListItem>>(`/api/games?${params}`, { signal });
		return page.items.filter((g) => g.id !== game.id).slice(0, 5);
	}

	/** Up to 5 same-system games; one retry with the first word of 3+ characters. Failures are silent. */
	async function loadSuggestions(): Promise<void> {
		const controller = (suggestionController = new AbortController());
		const term = mergeTerm(game.displayTitle);
		if (!term) return;
		try {
			let found = await fetchSuggestions(term, controller.signal);
			const word = term.split(' ').find((w) => w.length >= 3);
			if (!found.length && word && word !== term)
				found = await fetchSuggestions(word, controller.signal);
			if (!controller.signal.aborted) suggestions = found;
		} catch {
			// the typeahead still works
		}
	}

	$effect(() => {
		if (!action) {
			open = false;
			return;
		}
		open = true;
		busy = false;
		error = null;
		errors = {};
		target = null;
		targetError = undefined;
		moveTo = 'existing';
		newTitle = '';
		newTitleError = undefined;
		form = untrack(() => toForm(game));
		editTab = action.kind === 'edit' && action.lookup ? 'lookup' : 'details';
		lookupChosen = null;
		igdbApplied = false;
		pendingCover = null;
		suggestionController?.abort();
		suggestions = [];
		if (action.kind === 'merge') void untrack(loadSuggestions);
	});

	$effect(() => {
		if (!open) action = null;
	});

	const lastRom = $derived(game.roms.length === 1);
	const title = $derived(
		{
			edit: 'Edit Metadata',
			retry: 'Retry media',
			merge: 'Merge into…',
			deleteGame: 'Delete game',
			move: 'Move release',
			primary: 'Make primary',
			deleteRom: 'Delete release'
		}[action?.kind ?? 'edit']
	);
	const destructive = $derived(action?.kind === 'deleteGame' || action?.kind === 'deleteRom');

	const base = $derived(`/api/games/${encodeURIComponent(game.id)}`);

	/** Runs the action; null when it stopped on a client-side validation error. */
	async function run(a: AdminAction): Promise<AdminOutcome | null> {
		switch (a.kind) {
			case 'edit': {
				errors = validateGameEdit(form);
				if (Object.keys(errors).length) {
					editTab = 'details';
					return null;
				}
				const saved = await api.patch<GameDetail>(base, toPatch(form));
				return pendingCover
					? await addCover(saved, pendingCover)
					: { game: saved, message: 'Saved.' };
			}
			case 'retry':
				return {
					game: await api.post<GameDetail>(`${base}/media/retry`),
					message: 'Media lookup finished.'
				};
			case 'merge': {
				if (!target) {
					targetError = 'Choose the game to merge into.';
					return null;
				}
				const done = await api.post<{ targetGameId: string }>(`${base}/merge`, {
					targetGameId: target.id
				});
				return { goTo: done.targetGameId, message: `Merged into ${target.displayTitle}.` };
			}
			case 'deleteGame':
				await api.delete(base);
				return { goTo: null, message: `${game.displayTitle} deleted.` };
			case 'deleteRom':
				await api.delete(`${base}/roms/${a.rom.id}`);
				return lastRom
					? { goTo: null, message: `${game.displayTitle} deleted.` }
					: { reload: true, message: `${a.rom.fileName} deleted.` };
			case 'primary':
				return {
					game: await api.post<GameDetail>(`${base}/roms/${a.rom.id}/primary`),
					message: `${a.rom.fileName} is now the primary release.`
				};
			case 'move': {
				let body: { targetGameId: string } | { newGameTitle: string };
				if (moveTo === 'existing') {
					if (!target) {
						targetError = 'Choose the game to move to.';
						return null;
					}
					body = { targetGameId: target.id };
				} else {
					newTitleError = titleError(newTitle);
					if (newTitleError) return null;
					body = { newGameTitle: newTitle.trim() };
				}
				const done = await api.post<{ targetGameId: string }>(
					`${base}/roms/${a.rom.id}/move`,
					body
				);
				// The emptied source game is deleted (§11), so follow the release.
				return lastRom
					? { goTo: done.targetGameId, message: `${a.rom.fileName} moved.` }
					: { reload: true, message: `${a.rom.fileName} moved.` };
			}
		}
	}

	/**
	 * After the metadata save: downloads the match's cover as box art (it replaces the placeholder and takes its cover
	 * status), then makes it the cover if another image still is. A failure here keeps the saved metadata.
	 */
	async function addCover(saved: GameDetail, url: string): Promise<AdminOutcome> {
		let detail = saved;
		try {
			detail = await api.post<GameDetail>(`${base}/images/from-url`, { url, type: 'BoxArt' });
			// The game had no real box art, so the only one now is the one just added.
			const added = detail.images.find((i) => i.type === 'BoxArt' && !i.isDefault);
			if (added && !added.isCover)
				detail = await api.post<GameDetail>(`${base}/images/${added.id}/cover`);
			return { game: detail, message: 'Saved, with the IGDB cover.' };
		} catch (e) {
			const reason = isApiError(e) ? e.title : 'Something went wrong.';
			return {
				game: detail,
				message: `Saved, but the IGDB cover couldn't be added: ${reason}`,
				warning: true
			};
		}
	}

	function showFieldErrors(e: ApiError): boolean {
		const first = (field: string) => e.errors[field]?.[0];
		if (action?.kind === 'edit') {
			errors = Object.fromEntries(
				(
					[
						'title',
						'releaseYear',
						'developers',
						'publishers',
						'genres',
						'description',
						'igdbId'
					] as const
				)
					.filter((f) => first(f))
					.map((f) => [f, first(f)])
			);
			// M1: show the errors, which are all on Details.
			if (Object.keys(errors).length) editTab = 'details';
			return Object.keys(errors).length > 0;
		}
		targetError = first('targetGameId');
		newTitleError = first('newGameTitle');
		return !!(targetError || newTitleError);
	}

	async function confirm(): Promise<void> {
		if (!action || busy) return;
		busy = true;
		error = null;
		if (action.kind === 'edit') void activity.refresh(); // L5: the footer shows the save at once
		try {
			const outcome = await run(action);
			if (!outcome) return;
			action = null;
			ondone(outcome);
		} catch (e) {
			if (toasts.jobRunning(e)) {
				action = null;
			} else if (isApiError(e) && e.status === 404) {
				// §11: a stale rom/game id → reload the detail and ask for a retry.
				action = null;
				ondone({ reload: true, message: changed, warning: true });
			} else if (!(isApiError(e, 'ValidationFailed') && showFieldErrors(e))) {
				error = isApiError(e) ? e.title : 'Something went wrong.';
			}
		} finally {
			busy = false;
		}
	}
</script>

<!-- M1: Lookup Info's actions sit in the modal footer, left of Cancel and Edit Metadata. -->
{#snippet lookupFooter()}
	{#if lookupChosen}
		<Button variant="success" disabled={busy} onclick={() => lookup?.useMatch(false)}
			>Use this match</Button
		>
		<Button variant="success" disabled={busy} onclick={() => lookup?.useMatch(true)}
			>Use this match and save</Button
		>
	{/if}
	{#if !lookupNotConfigured}
		<Button variant="warning" outline disabled={busy} onclick={() => lookup?.notOnIgdb()}
			>Not on IGDB</Button
		>
	{/if}
{/snippet}

<BaseModal
	bind:open
	{title}
	{busy}
	size={action?.kind === 'edit' ? 'lg' : undefined}
	confirmLabel={destructive ? 'Delete' : title.replace('…', '')}
	confirmClass={destructive ? 'btn-danger' : 'btn-primary'}
	onconfirm={confirm}
	footer={action?.kind === 'edit' && editTab === 'lookup' ? lookupFooter : undefined}
>
	{#if error}<div class="alert alert-danger py-2" role="alert">{error}</div>{/if}

	{#if action?.kind === 'edit'}
		{#if busy}
			<!-- L5: a save rewrites the ZIP, which for a large game takes a while. -->
			<div class="mb-3">
				<div class="progress" role="progressbar" aria-label="Saving">
					<div class="progress-bar progress-bar-striped progress-bar-animated w-100"></div>
				</div>
				<div class="form-text">Saving… large games can take a minute.</div>
			</div>
		{/if}
		<NavTabs
			tabs={editTabs}
			active={editTab}
			onselect={(id) => (editTab = id)}
			class="mb-3"
			scroll={false}
		/>
		<!-- Both panels stay mounted (hidden) so switching tabs keeps every value. Lookup Info sits outside the form (M1). -->
		<div
			id="edit-details-panel"
			role="tabpanel"
			aria-labelledby="edit-details-tab"
			hidden={editTab !== 'details'}
		>
			{#if igdbApplied}
				<div class="alert alert-info py-2" role="status">
					<span>IGDB values applied. Save to keep them.</span>
					{#if pendingCover}<span>Saving also adds the IGDB cover as the game's cover.</span>{/if}
				</div>
			{/if}
			<!-- The keydown only handles Enter bubbling up from the form's own fields. -->
			<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
			<form
				novalidate
				onsubmit={(e) => {
					e.preventDefault();
					void confirm();
				}}
				onkeydown={(e) => {
					// M2: Enter saves from an input; a textarea keeps Enter for newlines and saves on Ctrl/⌘+Enter.
					if (e.key !== 'Enter' || e.isComposing) return;
					const tag = (e.target as HTMLElement).tagName;
					if (tag === 'INPUT' || (tag === 'TEXTAREA' && (e.ctrlKey || e.metaKey))) {
						e.preventDefault();
						void confirm();
					}
				}}
			>
				<fieldset disabled={busy}>
					<div class="row g-3">
						<div class="col-12 col-md-9">
							<label class="form-label" for="edit-title">Title</label>
							<TextInput
								class={errors.title ? 'is-invalid' : undefined}
								id="edit-title"
								maxlength={255}
								bind:value={form.title}
							/>
							{#if errors.title}<div class="invalid-feedback">{errors.title}</div>{/if}
							<div class="form-text">Renaming also renames the ZIP.</div>
						</div>
						<div class="col-12 col-md-3">
							<label class="form-label" for="edit-year">Release year</label>
							<TextInput
								class={errors.releaseYear ? 'is-invalid' : undefined}
								id="edit-year"
								inputmode="numeric"
								bind:value={form.releaseYear}
							/>
							{#if errors.releaseYear}<div class="invalid-feedback">{errors.releaseYear}</div>{/if}
						</div>
						{#each [['developers', 'Developers'], ['publishers', 'Publishers'], ['genres', 'Genres']] as const as [field, label] (field)}
							<div class="col-12 col-md-4">
								<label class="form-label" for="edit-{field}">{label}</label>
								<Textarea
									class={errors[field] ? 'is-invalid' : undefined}
									id="edit-{field}"
									rows={3}
									aria-describedby="edit-lists-help"
									bind:value={form[field]}
								/>
								{#if errors[field]}<div class="invalid-feedback">{errors[field]}</div>{/if}
							</div>
						{/each}
						<div id="edit-lists-help" class="col-12 form-text mt-1">
							Developers, publishers and genres: one per line.
						</div>
						<div class="col-12">
							<label class="form-label" for="edit-description">Description</label>
							<Textarea
								class={errors.description ? 'is-invalid' : undefined}
								id="edit-description"
								rows={4}
								maxlength={4000}
								bind:value={form.description}
							/>
							{#if errors.description}<div class="invalid-feedback">{errors.description}</div>{/if}
						</div>
						<div class="col-12">
							<span class="form-text"
								>IGDB: {form.igdbId === null
									? 'not linked'
									: form.igdbId === 0
										? 'marked as not on IGDB'
										: `linked (#${form.igdbId})`}</span
							>
							{#if form.igdbId !== null}
								<Button
									class="btn btn-link btn-sm p-0 ms-1 align-baseline"
									onclick={() => {
										form.igdbId = null;
										pendingCover = null;
									}}>Unlink</Button
								>
							{/if}
							{#if errors.igdbId}<div class="invalid-feedback d-block">{errors.igdbId}</div>{/if}
						</div>
						<div class="col-12 form-text">
							Press Enter to save (Ctrl+Enter in a multi-line field).
						</div>
					</div>
				</fieldset>
			</form>
		</div>
		<div
			id="edit-lookup-panel"
			role="tabpanel"
			aria-labelledby="edit-lookup-tab"
			hidden={editTab !== 'lookup'}
		>
			<fieldset disabled={busy}>
				<IgdbLookup
					bind:this={lookup}
					bind:chosen={lookupChosen}
					bind:notConfigured={lookupNotConfigured}
					{game}
					{form}
					autostart={action.lookup}
					onapply={(next, options) => {
						form = next;
						editTab = 'details';
						igdbApplied = true;
						pendingCover = (needsCover && options?.match?.coverImageUrl) || null;
						if (options?.save) void confirm();
					}}
				/>
			</fieldset>
		</div>
	{:else if action?.kind === 'retry'}
		<p class="mb-0">Look up box art and a snap for {game.displayTitle} again?</p>
	{:else if action?.kind === 'merge'}
		<p>
			Every release of {game.displayTitle} moves into the chosen game, and {game.displayTitle} is deleted.
			The target keeps its title and metadata.
		</p>
		{#if suggestions.length}
			<div class="mb-3">
				<p class="form-label mb-1" id="merge-suggested">Suggested</p>
				<div class="list-group" role="group" aria-labelledby="merge-suggested">
					{#each suggestions as suggestion (suggestion.id)}
						<Button
							aria-pressed={target?.id === suggestion.id}
							onclick={() => typeahead?.select(suggestion)}
							class={[
								'list-group-item list-group-item-action',
								target?.id === suggestion.id && 'active'
							]
								.filter(Boolean)
								.join(' ')}
							>{suggestion.displayTitle}
							{#if suggestion.releaseYear}<span class="small">({suggestion.releaseYear})</span
								>{/if}</Button
						>
					{/each}
				</div>
			</div>
		{/if}
		<GameTypeahead
			bind:this={typeahead}
			id="merge-target"
			label="Merge into"
			system={game.system}
			excludeId={game.id}
			bind:selected={target}
			invalid={targetError}
		/>
	{:else if action?.kind === 'deleteGame'}
		<p class="mb-0">
			Delete {game.displayTitle}{game.roms.some((r) => r.hasNote) ? ' and its notes' : ''}? Its {game
				.roms.length === 1
				? 'ROM moves'
				: 'ROMs move'} to the system's
			<code>_removed</code> ingest folder.
		</p>
	{:else if action?.kind === 'deleteRom'}
		<p class="mb-0">
			Delete {action.rom.fileName}{action.rom.hasNote ? ' and its note' : ''}? The ROM{action.rom
				.files?.length
				? ` and its ${action.rom.files.length} other files move`
				: ' moves'} to the system's <code>_removed</code> ingest folder.{#if lastRom}
				It's the only release, so the game is deleted too.{/if}
		</p>
	{:else if action?.kind === 'primary'}
		<p class="mb-0">
			Make {action.rom.fileName} the primary release of {game.displayTitle}? It stays primary when
			other releases are added or removed.
		</p>
	{:else if action?.kind === 'move'}
		<p>Move {action.rom.fileName} to:</p>
		<fieldset class="mb-3">
			<legend class="visually-hidden">Destination</legend>
			<Checkbox
				type="radio"
				id="move-existing"
				label="An existing game"
				value="existing"
				bind:group={moveTo}
			/>
			<Checkbox type="radio" id="move-new" label="A new game" value="new" bind:group={moveTo} />
		</fieldset>
		{#if moveTo === 'existing'}
			<GameTypeahead
				id="move-target"
				label="Target game"
				system={game.system}
				excludeId={game.id}
				bind:selected={target}
				invalid={targetError}
			/>
		{:else}
			<label class="form-label" for="move-title">New game title</label>
			<TextInput
				class={newTitleError ? 'is-invalid' : undefined}
				id="move-title"
				maxlength={255}
				bind:value={newTitle}
			/>
			{#if newTitleError}<div class="invalid-feedback">{newTitleError}</div>{/if}
		{/if}
		{#if lastRom}
			<p class="form-text mb-0">
				It's the only release, so {game.displayTitle} is deleted after the move.
			</p>
		{/if}
	{/if}
</BaseModal>
