<script lang="ts">
	import type { Snippet } from 'svelte';

	interface Props {
		/** The body's id (the button's `aria-controls`). */
		id: string;
		/** The header `<h2>`'s id; the body is `aria-labelledby` it. */
		headingId: string;
		open?: boolean;
		/** No body padding, for a table or card body that brings its own. */
		flush?: boolean;
		header: Snippet;
		children: Snippet;
	}

	let { id, headingId, open = $bindable(false), flush = false, header, children }: Props = $props();
</script>

<!-- Its own .accordion wrapper: Bootstrap defines the --bs-accordion-* variables there. Open state lives in Svelte (no
     Collapse JS); a closed body stays mounted so its state (e.g. a selection) survives. -->
<div class="accordion mb-3">
	<div class="accordion-item">
		<h2 class="accordion-header" id={headingId}>
			<button
				type="button"
				class="accordion-button"
				class:collapsed={!open}
				aria-expanded={open}
				aria-controls={id}
				onclick={() => (open = !open)}
			>
				{@render header()}
			</button>
		</h2>
		<div {id} class="accordion-collapse collapse" class:show={open} aria-labelledby={headingId}>
			<div class="accordion-body" class:p-0={flush}>
				{@render children()}
			</div>
		</div>
	</div>
</div>
