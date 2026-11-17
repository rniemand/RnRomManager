<script lang="ts">
	import Icon from './Icon.svelte';

	/**
	 * B1: a section page's `<h1>` (icon + text). Shown below `lg`, where the navbar is collapsed; from `lg` up it stays in
	 * the DOM, visually hidden, so screen readers and axe still find the page's one `<h1>`.
	 */
	interface Props {
		icon: string;
		text: string;
		/** Spacing below it when shown (default `mb-3`). */
		class?: string;
	}

	let { icon, text, class: extra = 'mb-3' }: Props = $props();
</script>

<h1 class="h3 page-title {extra}"><Icon name={icon} /> {text}</h1>
