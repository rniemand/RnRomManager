<script lang="ts">
	import Icon from '#lib/icons/Icon.svelte';

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

<style>
	/* B1: the section title shows only while the navbar is collapsed; from lg up it stays for screen readers (visually-hidden). */
	@media (min-width: 992px) {
		:global(.page-title) {
			position: absolute !important;
			width: 1px !important;
			height: 1px !important;
			padding: 0 !important;
			margin: -1px !important;
			overflow: hidden !important;
			clip: rect(0, 0, 0, 0) !important;
			white-space: nowrap !important;
			border: 0 !important;
		}
	}
</style>
