<script lang="ts">
	import type { Snippet } from 'svelte';
	import Icon from './Icon.svelte';

	interface Props {
		message: string;
		icon?: string;
		/** The next action (spec §11), e.g. a link to Ingestion for Admins. */
		action?: Snippet;
	}

	let { message, icon = 'empty', action }: Props = $props();
</script>

<div class="text-center text-body-secondary py-5">
	<Icon name={icon} class="fs-1 d-block mb-2" />
	<p class="mb-3">{message}</p>
	{#if action}{@render action()}{/if}
</div>
