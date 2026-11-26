<script lang="ts">
	import { resolve } from '$app/paths';
	import type { ReviewDetail } from '#lib/types/index.js';
	import Button from '#lib/components/forms/Button.svelte';

	interface Props {
		item: ReviewDetail;
		disabled: boolean;
		onattach: (game: ReviewDetail['candidateGames'][number]) => unknown;
	}

	let { item, disabled, onattach }: Props = $props();
</script>

{#if item.datIdentification}
	<p>
		{item.datIdentification.romName}
		<span class="text-body-secondary small">
			({item.datIdentification.datName}
			{item.datIdentification.datVersion})
		</span>
	</p>
{/if}
<p>Several games share its title. Attach it to one of them:</p>
<ul class="list-group">
	{#each item.candidateGames as game (game.id)}
		<li class="list-group-item d-flex align-items-center gap-2">
			<a class="me-auto" href={resolve('/games/[id]', { id: game.id })}>{game.displayTitle}</a>
			<Button variant="primary" size="sm" {disabled} onclick={() => onattach(game)}>Attach</Button>
		</li>
	{:else}
		<li class="list-group-item text-body-secondary">
			None of the candidate games exist any more. Run ingestion again.
		</li>
	{/each}
</ul>
