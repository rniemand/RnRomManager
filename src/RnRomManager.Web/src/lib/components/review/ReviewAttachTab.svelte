<script lang="ts">
	import type { GameListItem, ReviewDetail } from '#lib/types/index.js';
	import type { AttachFlag } from '#lib/enums/index.js';
	import Button from '#lib/components/forms/Button.svelte';
	import Checkbox from '#lib/components/forms/Checkbox.svelte';
	import GameTypeahead from '#lib/components/forms/GameTypeahead.svelte';
	import NumberInput from '#lib/components/forms/NumberInput.svelte';

	interface Props {
		item: ReviewDetail;
		/** B16: the flags this item's system offers. */
		attachFlags: readonly AttachFlag[];
		/** Bound: the chosen game. */
		attachTarget: GameListItem | null;
		attachTargetError?: string;
		/** Bound: the chosen flag. */
		attachFlag: AttachFlag;
		/** Bound: the disc number for the Disc flag. */
		attachDisc: number | null;
		attachDiscError?: string;
		disabled: boolean;
		onsubmit: (event: SubmitEvent) => unknown;
	}

	let {
		item,
		attachFlags,
		attachTarget = $bindable(),
		attachTargetError,
		attachFlag = $bindable(),
		attachDisc = $bindable(),
		attachDiscError,
		disabled,
		onsubmit
	}: Props = $props();
</script>

<p class="small text-body-secondary">
	{item.fileSets
		? 'Attach to a game as a new release, a hack, a translation or another disc.'
		: item.addOns
			? 'Attach to a game as a new release, a hack, a translation or a DLC.'
			: 'Attach to a game as a new release, a hack or a translation.'}
</p>
<form {onsubmit} novalidate>
	<div class="mb-2">
		<!-- B21: a fresh typeahead (its typed text too) per item. -->
		{#key item.id}
			<GameTypeahead
				id="attach-target"
				label="Game"
				system={item.system}
				bind:selected={attachTarget}
				invalid={attachTargetError}
			/>
		{/key}
	</div>
	<fieldset class="mb-2">
		<legend class="form-label fs-6">Attach as</legend>
		{#each attachFlags as flag (flag)}
			<Checkbox
				type="radio"
				id="flag-{flag}"
				label={flag}
				inline
				value={flag}
				bind:group={attachFlag}
			/>
		{/each}
	</fieldset>
	{#if attachFlag === 'Disc'}
		<div class="mb-2 disc-field">
			<label class="form-label" for="attach-disc">Disc</label>
			<NumberInput
				class={attachDiscError ? 'is-invalid' : undefined}
				id="attach-disc"
				min="1"
				max="99"
				required
				bind:value={attachDisc}
			/>
			{#if attachDiscError}<div class="invalid-feedback">{attachDiscError}</div>{/if}
		</div>
	{/if}
	<Button variant="primary" type="submit" {disabled}>Attach</Button>
</form>

<style>
	.disc-field {
		max-width: 10rem;
	}
</style>
