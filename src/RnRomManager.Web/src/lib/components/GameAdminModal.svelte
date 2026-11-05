<script lang="ts" module>
	import type { GameDetail, RomDetail } from '../api/types';

	export type AdminAction =
		| { kind: 'edit' }
		| { kind: 'retry' }
		| { kind: 'merge' }
		| { kind: 'deleteGame' }
		| { kind: 'move'; rom: RomDetail }
		| { kind: 'deleteRom'; rom: RomDetail };

	/** What the detail page does next: show this game, go to another game (null = Library), or reload. */
	export type AdminOutcome =
		| { game: GameDetail; message: string }
		| { goTo: string | null; message: string }
		| { reload: true; message?: string; warning?: boolean };
</script>

<script lang="ts">
	import { untrack } from 'svelte';
	import { api } from '../api/client';
	import { ApiError, isApiError } from '../api/errors';
	import type { GameListItem } from '../api/types';
	import {
		titleError,
		toForm,
		toPatch,
		validateGameEdit,
		type GameEditErrors,
		type GameEditForm
	} from '../gameEdit';
	import { toasts } from '../stores/toasts.svelte';
	import ConfirmModal from './ConfirmModal.svelte';
	import GameTypeahead from './GameTypeahead.svelte';

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
	});

	$effect(() => {
		if (!open) action = null;
	});

	const lastRom = $derived(game.roms.length === 1);
	const title = $derived(
		{
			edit: 'Edit metadata',
			retry: 'Retry media',
			merge: 'Merge into…',
			deleteGame: 'Delete game',
			move: 'Move release',
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
				if (Object.keys(errors).length) return null;
				return { game: await api.patch<GameDetail>(base, toPatch(form)), message: 'Saved.' };
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

	function showFieldErrors(e: ApiError): boolean {
		const first = (field: string) => e.errors[field]?.[0];
		if (action?.kind === 'edit') {
			errors = Object.fromEntries(
				(['title', 'releaseYear', 'developers', 'publishers', 'genres', 'description'] as const)
					.filter((f) => first(f))
					.map((f) => [f, first(f)])
			);
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

<ConfirmModal
	bind:open
	{title}
	{busy}
	size={action?.kind === 'edit' ? 'lg' : undefined}
	confirmLabel={destructive ? 'Delete' : title.replace('…', '')}
	confirmClass={destructive ? 'btn-danger' : 'btn-primary'}
	onconfirm={confirm}
>
	{#if error}<div class="alert alert-danger py-2" role="alert">{error}</div>{/if}

	{#if action?.kind === 'edit'}
		<form
			novalidate
			onsubmit={(e) => {
				e.preventDefault();
				void confirm();
			}}
		>
			<div class="row g-3">
				<div class="col-12 col-md-9">
					<label class="form-label" for="edit-title">Title</label>
					<input
						id="edit-title"
						class="form-control"
						class:is-invalid={errors.title}
						maxlength="255"
						bind:value={form.title}
					/>
					{#if errors.title}<div class="invalid-feedback">{errors.title}</div>{/if}
					<div class="form-text">Renaming also renames the ZIP.</div>
				</div>
				<div class="col-12 col-md-3">
					<label class="form-label" for="edit-year">Release year</label>
					<input
						id="edit-year"
						class="form-control"
						class:is-invalid={errors.releaseYear}
						inputmode="numeric"
						bind:value={form.releaseYear}
					/>
					{#if errors.releaseYear}<div class="invalid-feedback">{errors.releaseYear}</div>{/if}
				</div>
				{#each [['developers', 'Developers'], ['publishers', 'Publishers'], ['genres', 'Genres']] as const as [field, label] (field)}
					<div class="col-12 col-md-4">
						<label class="form-label" for="edit-{field}">{label}</label>
						<textarea
							id="edit-{field}"
							class="form-control"
							class:is-invalid={errors[field]}
							rows="3"
							aria-describedby="edit-lists-help"
							bind:value={form[field]}></textarea>
						{#if errors[field]}<div class="invalid-feedback">{errors[field]}</div>{/if}
					</div>
				{/each}
				<div id="edit-lists-help" class="col-12 form-text mt-1">
					Developers, publishers and genres: one per line.
				</div>
				<div class="col-12">
					<label class="form-label" for="edit-description">Description</label>
					<textarea
						id="edit-description"
						class="form-control"
						class:is-invalid={errors.description}
						rows="4"
						maxlength="4000"
						bind:value={form.description}></textarea>
					{#if errors.description}<div class="invalid-feedback">{errors.description}</div>{/if}
				</div>
			</div>
		</form>
	{:else if action?.kind === 'retry'}
		<p class="mb-0">Look up box art and a snap for {game.displayTitle} again?</p>
	{:else if action?.kind === 'merge'}
		<p>
			Every release of {game.displayTitle} moves into the chosen game, and {game.displayTitle} is deleted.
			The target keeps its title and metadata.
		</p>
		<GameTypeahead
			id="merge-target"
			label="Merge into"
			system={game.system}
			excludeId={game.id}
			bind:selected={target}
			invalid={targetError}
		/>
	{:else if action?.kind === 'deleteGame'}
		<p class="mb-0">
			Delete {game.displayTitle}? Its {game.roms.length === 1 ? 'ROM moves' : 'ROMs move'} to the system's
			<code>_removed</code> ingest folder.
		</p>
	{:else if action?.kind === 'deleteRom'}
		<p class="mb-0">
			Delete {action.rom.fileName}? The ROM moves to the system's <code>_removed</code> ingest
			folder.{#if lastRom}
				It's the only release, so the game is deleted too.{/if}
		</p>
	{:else if action?.kind === 'move'}
		<p>Move {action.rom.fileName} to:</p>
		<fieldset class="mb-3">
			<legend class="visually-hidden">Destination</legend>
			<div class="form-check">
				<input
					id="move-existing"
					class="form-check-input"
					type="radio"
					value="existing"
					bind:group={moveTo}
				/>
				<label class="form-check-label" for="move-existing">An existing game</label>
			</div>
			<div class="form-check">
				<input
					id="move-new"
					class="form-check-input"
					type="radio"
					value="new"
					bind:group={moveTo}
				/>
				<label class="form-check-label" for="move-new">A new game</label>
			</div>
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
			<input
				id="move-title"
				class="form-control"
				class:is-invalid={newTitleError}
				maxlength="255"
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
</ConfirmModal>
