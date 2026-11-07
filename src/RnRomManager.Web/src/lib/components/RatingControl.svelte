<script lang="ts">
	import { isApiError } from '../api/errors';
	import type { GameDetail } from '../api/types';
	import { saveRating, type RatingChange } from '../rating';
	import { toasts } from '../stores/toasts.svelte';
	import Icon from './Icon.svelte';

	interface Props {
		gameId: string;
		/** The game's title, for the group's accessible name. */
		title: string;
		rating: number | null;
		favourite: boolean;
		/** Display only (search results): no buttons. */
		readonly?: boolean;
		/** The full detail the save returned (new ROM/image ids). */
		onsaved?: (game: GameDetail) => void;
	}

	let {
		gameId,
		title,
		rating = $bindable(),
		favourite = $bindable(),
		readonly = false,
		onsaved
	}: Props = $props();

	let busy = $state(false);

	/** Optimistic: shows the change at once, reverts it when the save fails (spec B22). */
	async function save(change: RatingChange): Promise<void> {
		if (busy) return;
		const before = { rating, favourite };
		if (change.rating !== undefined) rating = change.rating;
		if (change.favourite !== undefined) favourite = change.favourite;
		busy = true;
		try {
			const saved = await saveRating(gameId, change);
			onsaved?.(saved);
		} catch (e) {
			({ rating, favourite } = before);
			if (!toasts.jobRunning(e))
				toasts.show(isApiError(e) ? e.title : 'The rating could not be saved.', 'danger');
		} finally {
			busy = false;
		}
	}

	const stars = [1, 2, 3, 4, 5];
</script>

{#if readonly}
	{#if rating !== null || favourite}
		<span class="d-inline-flex align-items-center gap-1 text-warning small">
			{#if rating !== null}
				<span aria-hidden="true">
					{#each stars as n (n)}<Icon name={n <= rating ? 'star-fill' : 'star'} />{/each}
				</span>
				<span class="visually-hidden">Rated {rating} of 5</span>
			{/if}
			{#if favourite}
				<span class="text-danger" aria-hidden="true"><Icon name="favourite-fill" /></span>
				<span class="visually-hidden">Favourite</span>
			{/if}
		</span>
	{/if}
{:else}
	<div class="d-inline-flex align-items-center" role="group" aria-label="Rating for {title}">
		{#each stars as n (n)}
			<button
				type="button"
				class="btn btn-link btn-sm p-0 px-1 text-warning target"
				aria-label="Rate {n} of 5"
				aria-pressed={rating === n}
				disabled={busy}
				onclick={() => save({ rating: rating === n ? null : n })}
			>
				<Icon name={rating !== null && n <= rating ? 'star-fill' : 'star'} />
			</button>
		{/each}
		<button
			type="button"
			class="btn btn-link btn-sm p-0 ms-1 text-danger target"
			aria-label="Favourite"
			aria-pressed={favourite}
			disabled={busy}
			onclick={() => save({ favourite: !favourite })}
		>
			<Icon name={favourite ? 'favourite-fill' : 'favourite'} />
		</button>
	</div>
{/if}

<style>
	/* WCAG 2.2 target size: a real 24×24 px minimum, not spacing that a sub-pixel layout shift (e.g. Bootstrap's
	   scrollbar compensation on the sticky navbar while a modal is open) can push under the threshold. */
	.target {
		min-width: 24px;
		min-height: 24px;
		display: inline-flex;
		align-items: center;
		justify-content: center;
	}
</style>
