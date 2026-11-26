<script lang="ts">
	import { fromAction } from 'svelte/attachments';
	import type { ReviewDetail } from '#lib/types/index.js';
	import Button from '#lib/components/forms/Button.svelte';
	import SystemBadge from '#lib/components/common/SystemBadge.svelte';
	import { googleSearchUrl, reviewSearchQuery } from '#lib/googleSearch.js';
	import { tooltip } from '#lib/tooltip.js';

	interface Props {
		item: ReviewDetail;
		disabled: boolean;
		ondismiss: () => void;
		/** RV1: Shift+click skips the confirmation. */
		ondelete: (event: MouseEvent) => unknown;
	}

	let { item, disabled, ondismiss, ondelete }: Props = $props();
</script>

<div class="d-flex flex-wrap align-items-center gap-2 mb-3">
	<h1 class="h4 mb-0 text-truncate item-title" title={item.originalFileName}>
		{item.originalFileName}
	</h1>
	<SystemBadge system={item.system} />
	<span class="badge text-bg-secondary">{item.reason}</span>
	{#if item.status !== 'Pending'}<span class="badge text-bg-light border">{item.status}</span>{/if}
	<div class="ms-auto d-flex gap-2">
		<!-- External link: resolve() is for app routes. -->
		<!-- eslint-disable svelte/no-navigation-without-resolve -->
		<Button
			variant="secondary"
			outline
			icon="google"
			href={googleSearchUrl(reviewSearchQuery(item, item.systemName))}
			target="_blank"
			rel="noopener noreferrer">Google it</Button
		>
		<!-- eslint-enable svelte/no-navigation-without-resolve -->
		{#if item.dismissible}
			<Button variant="secondary" outline {disabled} onclick={ondismiss}>Dismiss</Button>
		{/if}
		<Button
			variant="danger"
			outline
			icon="delete"
			{disabled}
			{@attach fromAction(tooltip, () => 'Delete (Shift+click to skip confirmation)')}
			onclick={ondelete}>Delete</Button
		>
	</div>
</div>

<style>
	.item-title {
		max-width: 32rem;
	}
</style>
