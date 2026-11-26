<script lang="ts">
	import type { GameEditErrors, GameEditForm } from '#lib/types/index.js';
	import Button from '#lib/components/forms/Button.svelte';
	import Textarea from '#lib/components/forms/Textarea.svelte';
	import TextInput from '#lib/components/forms/TextInput.svelte';

	interface Props {
		/** Bound: Edit Metadata's Details form. */
		form: GameEditForm;
		errors: GameEditErrors;
		busy: boolean;
		/** Bound: the IGDB cover to add after the save; Unlink clears it. */
		pendingCover: string | null;
		onsave: () => void;
	}

	let { form = $bindable(), errors, busy, pendingCover = $bindable(), onsave }: Props = $props();
</script>

<!-- The keydown only handles Enter bubbling up from the form's own fields. -->
<!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
<form
	novalidate
	onsubmit={(e) => {
		e.preventDefault();
		onsave();
	}}
	onkeydown={(e) => {
		// M2: Enter saves from an input; a textarea keeps Enter for newlines and saves on Ctrl/⌘+Enter.
		if (e.key !== 'Enter' || e.isComposing) return;
		const tag = (e.target as HTMLElement).tagName;
		if (tag === 'INPUT' || (tag === 'TEXTAREA' && (e.ctrlKey || e.metaKey))) {
			e.preventDefault();
			onsave();
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
			<div class="col-12 form-text">Press Enter to save (Ctrl+Enter in a multi-line field).</div>
		</div>
	</fieldset>
</form>
