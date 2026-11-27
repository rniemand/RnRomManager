<script lang="ts">
	import Button from '#lib/components/forms/Button.svelte';
	import type { Snippet } from 'svelte';

	interface Props {
		/** The body's id (the button's `aria-controls`). */
		id: string;
		/** The header `<h2>`'s id; the body is `aria-labelledby` it. */
		headingId: string;
		open?: boolean;
		/** No body padding, for a table or card body that brings its own. */
		flush?: boolean;
		/** The header's heading level; 3 nests the panel under a page `<h2>` (Saves S2a). */
		headingLevel?: 2 | 3;
		header: Snippet;
		children: Snippet;
	}

	let {
		id,
		headingId,
		open = $bindable(false),
		flush = false,
		headingLevel = 2,
		header,
		children
	}: Props = $props();
</script>

<!-- Its own .accordion wrapper: Bootstrap defines the --bs-accordion-* variables there. Open state lives in Svelte (no
     Collapse JS); a closed body stays mounted so its state (e.g. a selection) survives. -->
<div class="accordion mb-3">
	<div class="accordion-item">
		<svelte:element this={`h${headingLevel}`} class="accordion-header" id={headingId}>
			<Button
				aria-expanded={open}
				aria-controls={id}
				onclick={() => (open = !open)}
				class={['accordion-button', !open && 'collapsed'].filter(Boolean).join(' ')}
				>{@render header()}</Button
			>
		</svelte:element>
		<div {id} class="accordion-collapse collapse" class:show={open} aria-labelledby={headingId}>
			<div class="accordion-body" class:p-0={flush}>
				{@render children()}
			</div>
		</div>
	</div>
</div>
