<script lang="ts">
	import type { Snippet } from 'svelte';

	interface Props {
		label: string;
		/** The control's `id`. */
		for: string;
		/** Keeps the label for screen readers only (`visually-hidden`). */
		hiddenLabel?: boolean;
		/** Appended to `form-label` (e.g. `small mb-1`). */
		labelClass?: string;
		children?: Snippet;
	}

	let { label, for: htmlFor, hiddenLabel = false, labelClass, children }: Props = $props();
</script>

<!-- No wrapper: the site keeps its own (`mb-3`, `col-12 col-md-6`…). -->
<label for={htmlFor} class={hiddenLabel ? 'visually-hidden' : ['form-label', labelClass]}
	>{label}</label
>
{@render children?.()}
