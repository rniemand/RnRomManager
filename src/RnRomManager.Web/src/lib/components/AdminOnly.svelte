<script lang="ts">
	import type { Snippet } from 'svelte';
	import { auth } from '../stores/auth.svelte';
	import NotAllowed from './NotAllowed.svelte';

	/** Wraps an Admin page: a `User` sees "Not allowed" instead (spec §11). */
	let { children }: { children: Snippet } = $props();
</script>

{#if auth.isAdmin}
	{@render children()}
{:else}
	<NotAllowed />
{/if}
