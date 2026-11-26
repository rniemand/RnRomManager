<script lang="ts">
	import type { ManualErrors, ManualForm, ReviewDetail } from '#lib/types/index.js';
	import Button from '#lib/components/forms/Button.svelte';
	import NumberInput from '#lib/components/forms/NumberInput.svelte';
	import TextInput from '#lib/components/forms/TextInput.svelte';
	import { manualFileStem } from '#lib/reviewForm.js';

	interface Props {
		item: ReviewDetail;
		/** Bound: the form; the page keeps it across reloads of the same item. */
		manual: ManualForm;
		manualErrors: ManualErrors;
		disabled: boolean;
		/** B11/B12: the next queue item for Add and Next. */
		next: number | 'loading' | 'none' | 'failed';
		/** `event` is null for Add and Next, which is no submit. */
		onsubmit: (event: SubmitEvent | null, andNext?: boolean) => void;
	}

	let { item, manual = $bindable(), manualErrors, disabled, next, onsubmit }: Props = $props();
</script>

<form {onsubmit} novalidate>
	<div class="mb-2">
		<label class="form-label" for="manual-title">Title</label>
		<TextInput
			class={manualErrors.title ? 'is-invalid' : undefined}
			id="manual-title"
			maxlength={255}
			bind:value={manual.title}
		/>
		{#if manualErrors.title}<div class="invalid-feedback">
				{manualErrors.title}
			</div>{/if}
	</div>
	<div class="row g-2 mb-2">
		{#each [['regions', 'Regions', 'e.g. USA, Europe'], ['languages', 'Languages', 'e.g. En, Fr'], ['flags', 'Flags', 'e.g. Proto, Unl']] as const as [field, label, hint] (field)}
			<div class="col-12 col-md-4">
				<label class="form-label" for="manual-{field}">{label}</label>
				<TextInput
					class={manualErrors[field] ? 'is-invalid' : undefined}
					id="manual-{field}"
					aria-describedby="manual-{field}-help"
					bind:value={manual[field]}
				/>
				{#if manualErrors[field]}<div class="invalid-feedback">
						{manualErrors[field]}
					</div>{/if}
				<div id="manual-{field}-help" class="form-text">Comma-separated, {hint}.</div>
			</div>
		{/each}
	</div>
	<div class="mb-2">
		<label class="form-label" for="manual-revision">Revision</label>
		<TextInput
			class={manualErrors.revision ? 'is-invalid' : undefined}
			id="manual-revision"
			placeholder="e.g. Rev 1"
			maxlength={32}
			bind:value={manual.revision}
		/>
		{#if manualErrors.revision}<div class="invalid-feedback">
				{manualErrors.revision}
			</div>{/if}
	</div>
	{#if item.fileSets}
		<div class="mb-2 disc-field">
			<label class="form-label" for="manual-disc">Disc</label>
			<NumberInput
				class={manualErrors.disc ? 'is-invalid' : undefined}
				id="manual-disc"
				min="1"
				max="99"
				aria-describedby="manual-disc-help"
				bind:value={manual.disc}
			/>
			{#if manualErrors.disc}<div class="invalid-feedback">
					{manualErrors.disc}
				</div>{/if}
			<div id="manual-disc-help" class="form-text">Optional, 1–99.</div>
		</div>
	{/if}
	{#if manual.title.trim()}
		<p class="form-text">File name: {manualFileStem(manual)}</p>
	{/if}
	<Button variant="primary" type="submit" {disabled}>Add manually</Button>
	{#if item.hasDat === false}
		<Button
			variant="primary"
			outline
			class="ms-1"
			disabled={disabled || typeof next !== 'number'}
			title={next === 'none' ? 'No more items in the review queue.' : undefined}
			onclick={() => onsubmit(null, true)}>Add and Next</Button
		>
	{/if}
</form>

<style>
	.disc-field {
		max-width: 10rem;
	}
</style>
